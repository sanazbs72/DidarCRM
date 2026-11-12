using System.Net;
using System.Net.Http.Json;
using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ExpertAvailabilityTests(PostgresFixture fixture)
{
    private sealed record StatusBody(string Status);

    [Fact]
    public async Task ReducingCapacityPreservesAssignmentsAndGatesNewWork()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 5);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var a1 = await fixture.AddRequestAsync(s1, "Assigned", expert);
        var a2 = await fixture.AddRequestAsync(s1, "Assigned", expert);
        var a3 = await fixture.AddRequestAsync(s1, "Assigned", expert);
        var a4 = await fixture.AddRequestAsync(s1, "Assigned", expert);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal(HttpStatusCode.OK, (await PatchCapacityAsync(client, expert, 3)).Status);

        Assert.Equal(4, await CountAssignedAsync(expert));

        var pending = await fixture.AddRequestAsync(s1, "Pending");
        await Task.Delay(2000);
        Assert.Equal("Pending", (await ReadRequestAsync(pending)).Status);

        await CloseAsync(client, a1);
        await Task.Delay(2000);
        Assert.Equal("Pending", (await ReadRequestAsync(pending)).Status);

        await CloseAsync(client, a2);
        await Poll.UntilAsync(async () => (await ReadRequestAsync(pending)).Status == "Assigned");
        Assert.Equal(expert, (await ReadRequestAsync(pending)).ExpertId);
    }

    [Fact]
    public async Task SpecialtyDeactivationBlocksOnlyThatSpecialtyAndDrains()
    {
        var financial = await fixture.AddSpecialtyAsync();
        var legal = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, financial, "Active");
        await fixture.AddExpertSpecialtyAsync(expert, legal, "Active");
        var f1 = await fixture.AddRequestAsync(financial, "Assigned", expert);
        var l1 = await fixture.AddRequestAsync(legal, "Assigned", expert);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        var (code, resolved) = await PatchSpecialtyStatusAsync(client, expert, financial, "Inactive");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("PendingDeactivation", resolved);

        var f2 = await fixture.AddRequestAsync(financial, "Pending");
        await Task.Delay(2000);
        Assert.Equal("Pending", (await ReadRequestAsync(f2)).Status);

        var l2 = await fixture.AddRequestAsync(legal, "Pending");
        await Poll.UntilAsync(async () => (await ReadRequestAsync(l2)).Status == "Assigned");
        Assert.Equal(expert, (await ReadRequestAsync(l2)).ExpertId);

        await CloseAsync(client, f1);
        await Poll.UntilAsync(async () => await ReadSpecialtyStatusAsync(expert, financial) == "Inactive");
        Assert.Equal("Inactive", await ReadSpecialtyStatusAsync(expert, financial));
        Assert.Equal("Active", await ReadSpecialtyStatusAsync(expert, legal));
        Assert.Equal("Assigned", (await ReadRequestAsync(l1)).Status);
    }

    [Fact]
    public async Task ExpertDeactivationBlocksAllSpecialtiesAndDrainsToInactive()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(expert, s2, "Active");
        var a1 = await fixture.AddRequestAsync(s1, "Assigned", expert);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        var (code, resolved) = await PatchExpertStatusAsync(client, expert, "Inactive");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("PendingDeactivation", resolved);

        var p1 = await fixture.AddRequestAsync(s2, "Pending");
        await Task.Delay(2000);
        Assert.Equal("Pending", (await ReadRequestAsync(p1)).Status);

        await CloseAsync(client, a1);
        await Poll.UntilAsync(async () => await ReadExpertStatusAsync(expert) == "Inactive");
        Assert.Equal("Inactive", await ReadExpertStatusAsync(expert));
    }

    [Fact]
    public async Task ReactivatingPendingDeactivationRestoresEligibility()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", expert);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal("PendingDeactivation", (await PatchExpertStatusAsync(client, expert, "Inactive")).Resolved);

        var (code, resolved) = await PatchExpertStatusAsync(client, expert, "Active");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Active", resolved);

        var p1 = await fixture.AddRequestAsync(s1, "Pending");
        await Poll.UntilAsync(async () => (await ReadRequestAsync(p1)).Status == "Assigned");
    }

    [Fact]
    public async Task CapacityIncreaseReconsidersPendingRequest()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", expert);
        var pending = await fixture.AddRequestAsync(s1, "Pending");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        await Task.Delay(2000);
        Assert.Equal("Pending", (await ReadRequestAsync(pending)).Status);

        Assert.Equal(HttpStatusCode.OK, (await PatchCapacityAsync(client, expert, 2)).Status);
        await Poll.UntilAsync(async () => (await ReadRequestAsync(pending)).Status == "Assigned");
    }

    [Fact]
    public async Task AssignmentRacesWithDeactivation()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var pending = await fixture.AddRequestAsync(s1, "Pending");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        Assert.Equal(HttpStatusCode.OK, (await PatchExpertStatusAsync(client, expert, "Inactive")).Status);

        await Poll.UntilAsync(async () =>
        {
            var status = await ReadExpertStatusAsync(expert);
            var request = (await ReadRequestAsync(pending)).Status;
            return (status == "PendingDeactivation" && request == "Assigned")
                || (status == "Inactive" && request == "Pending");
        });
    }

    private static async Task<(HttpStatusCode Status, string? Resolved)> PatchCapacityAsync(HttpClient client, Guid expertId, int capacity)
    {
        using var response = await client.PatchAsJsonAsync($"/experts/{expertId:D}/capacity", new { capacity });
        var body = await response.Content.ReadFromJsonAsync<StatusBody>();
        return (response.StatusCode, body?.Status);
    }

    private static async Task<(HttpStatusCode Status, string? Resolved)> PatchExpertStatusAsync(HttpClient client, Guid expertId, string status)
    {
        using var response = await client.PatchAsJsonAsync($"/experts/{expertId:D}/status", new { status });
        var body = await response.Content.ReadFromJsonAsync<StatusBody>();
        return (response.StatusCode, body?.Status);
    }

    private static async Task<(HttpStatusCode Status, string? Resolved)> PatchSpecialtyStatusAsync(HttpClient client, Guid expertId, Guid specialtyId, string status)
    {
        using var response = await client.PatchAsJsonAsync($"/experts/{expertId:D}/specialties/{specialtyId:D}/status", new { status });
        var body = await response.Content.ReadFromJsonAsync<StatusBody>();
        return (response.StatusCode, body?.Status);
    }

    private static async Task CloseAsync(HttpClient client, Guid requestId)
    {
        using var response = await client.PostAsync($"/requests/{requestId:D}/close", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> ReadExpertStatusAsync(Guid expertId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM experts WHERE expert_id = @id", connection);
        command.Parameters.AddWithValue("id", expertId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadSpecialtyStatusAsync(Guid expertId, Guid specialtyId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM expert_specialties WHERE expert_id = @expert AND specialty_id = @specialty", connection);
        command.Parameters.AddWithValue("expert", expertId);
        command.Parameters.AddWithValue("specialty", specialtyId);
        return (string)(await command.ExecuteScalarAsync())!;
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
