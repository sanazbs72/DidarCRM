using System.Net;
using System.Net.Http.Json;
using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RequestLifecycleTests(PostgresFixture fixture)
{
    private sealed record RequestResponse(Guid RequestId, string Status);

    [Fact]
    public async Task CloseAssignedReleasesCapacityAndAssignsWaitingRequest()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var assigned = await fixture.AddRequestAsync(s1, "Assigned", expert);
        var waiting = await fixture.AddRequestAsync(s1, "Pending");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        var (status, body) = await CloseAsync(client, assigned);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Closed", body);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(waiting)).Status == "Assigned");
        Assert.Equal(expert, (await ReadRequestAsync(waiting)).ExpertId);
        Assert.Equal(1, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task CloseReleasesCapacityForAnotherSpecialty()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(expert, s2, "Active");
        var assigned = await fixture.AddRequestAsync(s1, "Assigned", expert);
        var waiting = await fixture.AddRequestAsync(s2, "Pending");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(client, assigned)).Status);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(waiting)).Status == "Assigned");
        Assert.Equal(expert, (await ReadRequestAsync(waiting)).ExpertId);
    }

    [Fact]
    public async Task CancelAssignedReleasesCapacity()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var assigned = await fixture.AddRequestAsync(s1, "Assigned", expert);
        var waiting = await fixture.AddRequestAsync(s1, "Pending");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        var (status, body) = await CancelAsync(client, assigned);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Cancelled", body);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(waiting)).Status == "Assigned");
        Assert.Equal("Cancelled", (await ReadRequestAsync(assigned)).Status);
        Assert.Equal(1, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task CancelPendingReleasesNoCapacity()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var pending = await fixture.AddRequestAsync(s1, "Pending");

        await using var host = await DidarProcess.StartAsync(
            fixture.ConnectionString,
            new Dictionary<string, string> { ["Assignment__Enabled"] = "false" });
        using var client = new HttpClient { BaseAddress = host.Address };

        var (status, body) = await CancelAsync(client, pending);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Cancelled", body);

        Assert.Equal("Cancelled", (await ReadRequestAsync(pending)).Status);
        Assert.Null((await ReadRequestAsync(pending)).ExpertId);
        Assert.Equal(0, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task RepeatCloseIsIdempotent()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var assigned = await fixture.AddRequestAsync(s1, "Assigned", expert);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal("Closed", (await CloseAsync(client, assigned)).Body);
        Assert.Equal("Closed", (await CloseAsync(client, assigned)).Body);
        Assert.Equal(0, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task RepeatCancelIsIdempotent()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var pending = await fixture.AddRequestAsync(s1, "Pending");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal("Cancelled", (await CancelAsync(client, pending)).Body);
        Assert.Equal("Cancelled", (await CancelAsync(client, pending)).Body);
    }

    [Fact]
    public async Task ConflictingTransitionsAreRejected()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var pending = await fixture.AddRequestAsync(s1, "Pending");
        var cancelled = await fixture.AddRequestAsync(s1, "Pending");
        var closed = await fixture.AddRequestAsync(s1, "Assigned", expert);

        await using var host = await DidarProcess.StartAsync(
            fixture.ConnectionString,
            new Dictionary<string, string> { ["Assignment__Enabled"] = "false" });
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal(HttpStatusCode.Conflict, (await CloseAsync(client, pending)).Status);
        Assert.Equal("Pending", (await ReadRequestAsync(pending)).Status);

        await CancelAsync(client, cancelled);
        Assert.Equal(HttpStatusCode.Conflict, (await CloseAsync(client, cancelled)).Status);

        await CloseAsync(client, closed);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(client, closed)).Status);

        Assert.Equal(0, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task UnknownRequestReturnsNotFound()
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await CloseAsync(client, id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CancelAsync(client, id)).Status);

        using var bad = await client.PostAsync("/requests/not-a-guid/close", null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task ConcurrentCloseAndCancelResolveToSingleTransition()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var assigned = await fixture.AddRequestAsync(s1, "Assigned", expert);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        var closeTask = CloseAsync(client, assigned);
        var cancelTask = CancelAsync(client, assigned);
        var results = await Task.WhenAll(closeTask, cancelTask);

        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);

        var final = (await ReadRequestAsync(assigned)).Status;
        Assert.True(final is "Closed" or "Cancelled");
        Assert.Equal(0, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task CancelRacingWithAssignmentLeavesConsistentState()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var pending = await fixture.AddRequestAsync(s1, "Pending");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(client, pending)).Status);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(pending)).Status == "Cancelled");
        Assert.Equal(0, await CountAssignedAsync(expert));
    }

    private static async Task<(HttpStatusCode Status, string? Body)> CloseAsync(HttpClient client, Guid id)
    {
        using var response = await client.PostAsync($"/requests/{id:D}/close", null);
        string? status = null;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            status = (await response.Content.ReadFromJsonAsync<RequestResponse>())?.Status;
        }

        return (response.StatusCode, status);
    }

    private static async Task<(HttpStatusCode Status, string? Body)> CancelAsync(HttpClient client, Guid id)
    {
        using var response = await client.PostAsync($"/requests/{id:D}/cancel", null);
        string? status = null;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            status = (await response.Content.ReadFromJsonAsync<RequestResponse>())?.Status;
        }

        return (response.StatusCode, status);
    }

    private async Task<(string Status, Guid? ExpertId)> ReadRequestAsync(Guid requestId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, expert_id FROM requests WHERE request_id = @id", connection);
        command.Parameters.AddWithValue("id", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private async Task<int> CountAssignedAsync(Guid expertId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM requests WHERE expert_id = @id AND status = 'Assigned'", connection);
        command.Parameters.AddWithValue("id", expertId);
        return checked((int)(long)(await command.ExecuteScalarAsync())!);
    }
}
