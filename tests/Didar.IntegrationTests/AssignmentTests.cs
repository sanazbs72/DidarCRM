using System.Net;
using System.Net.Http.Json;
using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AssignmentTests(PostgresFixture fixture)
{
    private sealed record RequestResponse(Guid RequestId, string Status);

    [Fact]
    public async Task LowestRatioExpertIsSelectedAcrossAllSpecialties()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();

        var a = await fixture.AddExpertAsync("Active", 10);
        var b = await fixture.AddExpertAsync("Active", 4);
        await fixture.AddExpertSpecialtyAsync(a, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(b, s1, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", a);
        await fixture.AddRequestAsync(s2, "Assigned", a);
        await fixture.AddRequestAsync(s2, "Assigned", a);
        await fixture.AddRequestAsync(s1, "Assigned", b);
        await fixture.AddRequestAsync(s1, "Assigned", b);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var requestId = await RegisterAsync(client, $"lowest-{Guid.NewGuid():N}", s1);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(requestId)).Status == "Assigned");
        Assert.Equal(a, (await ReadRequestAsync(requestId)).ExpertId);
    }

    [Fact]
    public async Task IneligibleExpertsAreExcluded()
    {
        var s1 = await fixture.AddSpecialtyAsync();

        var eligible = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(eligible, s1, "Active");
        for (var i = 0; i < 5; i++)
        {
            await fixture.AddRequestAsync(s1, "Assigned", eligible);
        }

        var inactive = await fixture.AddExpertAsync("Inactive", 100);
        await fixture.AddExpertSpecialtyAsync(inactive, s1, "Active");

        var inactiveRelation = await fixture.AddExpertAsync("Active", 100);
        await fixture.AddExpertSpecialtyAsync(inactiveRelation, s1, "Inactive");

        var missingRelation = await fixture.AddExpertAsync("Active", 100);

        var zeroCapacity = await fixture.AddExpertAsync("Active", 0);
        await fixture.AddExpertSpecialtyAsync(zeroCapacity, s1, "Active");

        var full = await fixture.AddExpertAsync("Active", 2);
        await fixture.AddExpertSpecialtyAsync(full, s1, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", full);
        await fixture.AddRequestAsync(s1, "Assigned", full);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var requestId = await RegisterAsync(client, $"eligible-{Guid.NewGuid():N}", s1);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(requestId)).Status == "Assigned");
        Assert.Equal(eligible, (await ReadRequestAsync(requestId)).ExpertId);
    }

    [Fact]
    public async Task NoEligibleExpertLeavesRequestPending()
    {
        var s1 = await fixture.AddSpecialtyAsync();

        // Only a full expert: no eligible option.
        var full = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(full, s1, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", full);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var requestId = await RegisterAsync(client, $"no-option-{Guid.NewGuid():N}", s1);

        await Task.Delay(2000);
        var (status, expertId) = await ReadRequestAsync(requestId);
        Assert.Equal("Pending", status);
        Assert.Null(expertId);
        Assert.Null(await ReadExpertLastAssignedAsync(full));
    }

    [Fact]
    public async Task NoOptionSpecialtyDoesNotBlockAnotherSpecialty()
    {
        var stuck = await fixture.AddSpecialtyAsync();
        var assignable = await fixture.AddSpecialtyAsync();

        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, assignable, "Active");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var stuckId = await RegisterAsync(client, $"stuck-{Guid.NewGuid():N}", stuck);
        var assignableId = await RegisterAsync(client, $"assignable-{Guid.NewGuid():N}", assignable);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(assignableId)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(stuckId)).Status);
        Assert.Equal(expert, (await ReadRequestAsync(assignableId)).ExpertId);
    }

    [Fact]
    public async Task TieBreakPrefersNullLastAssignedAt()
    {
        var s1 = await fixture.AddSpecialtyAsync();

        var withTime = await fixture.AddExpertAsync("Active", 4, DateTime.UtcNow.AddMinutes(-1));
        var noTime = await fixture.AddExpertAsync("Active", 2, null);
        await fixture.AddExpertSpecialtyAsync(withTime, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(noTime, s1, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", withTime);
        await fixture.AddRequestAsync(s1, "Assigned", withTime);
        await fixture.AddRequestAsync(s1, "Assigned", noTime);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var requestId = await RegisterAsync(client, $"null-first-{Guid.NewGuid():N}", s1);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(requestId)).Status == "Assigned");
        Assert.Equal(noTime, (await ReadRequestAsync(requestId)).ExpertId);
    }

    [Fact]
    public async Task TieBreakPrefersOlderLastAssignedAtThenSmallestExpertId()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();

        var older = await fixture.AddExpertAsync("Active", 2, DateTime.UtcNow.AddMinutes(-5));
        var newer = await fixture.AddExpertAsync("Active", 2, DateTime.UtcNow.AddMinutes(-1));
        await fixture.AddExpertSpecialtyAsync(older, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(newer, s1, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", older);
        await fixture.AddRequestAsync(s1, "Assigned", newer);

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var requestId = await RegisterAsync(client, $"older-first-{Guid.NewGuid():N}", s1);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(requestId)).Status == "Assigned");
        Assert.Equal(older, (await ReadRequestAsync(requestId)).ExpertId);

        var small = await fixture.AddExpertAsync("Active", 2, null, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var large = await fixture.AddExpertAsync("Active", 2, null, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await fixture.AddExpertSpecialtyAsync(small, s2, "Active");
        await fixture.AddExpertSpecialtyAsync(large, s2, "Active");
        await fixture.AddRequestAsync(s2, "Assigned", small);
        await fixture.AddRequestAsync(s2, "Assigned", large);

        var second = await RegisterAsync(client, $"smallest-id-{Guid.NewGuid():N}", s2);
        await Poll.UntilAsync(async () => (await ReadRequestAsync(second)).Status == "Assigned");
        Assert.Equal(small, (await ReadRequestAsync(second)).ExpertId);
    }

    [Fact]
    public async Task QueuePrefersOlderCreatedAtThenSmallerRequestId()
    {
        var s1 = await fixture.AddSpecialtyAsync();

        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var older = await fixture.AddRequestAsync(s1, "Pending", createdAt: baseTime);
        var newer = await fixture.AddRequestAsync(s1, "Pending", createdAt: baseTime.AddMinutes(1));

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(older)).Status == "Assigned");
        Assert.Equal(expert, (await ReadRequestAsync(older)).ExpertId);
        Assert.Equal("Pending", (await ReadRequestAsync(newer)).Status);
    }

    [Fact]
    public async Task QueuePrefersSmallerRequestIdOnEqualCreatedAt()
    {
        var s1 = await fixture.AddSpecialtyAsync();

        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var time = DateTime.UtcNow.AddMinutes(-10);
        var smaller = await fixture.AddRequestAsync(
            s1, "Pending", createdAt: time,
            requestId: Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var larger = await fixture.AddRequestAsync(
            s1, "Pending", createdAt: time,
            requestId: Guid.Parse("10000000-0000-0000-0000-000000000002"));

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(smaller)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(larger)).Status);
    }

    [Fact]
    public async Task PriorityDoesNotAffectSliceTwoQueueOrder()
    {
        var s1 = await fixture.AddSpecialtyAsync();

        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var olderNormal = await fixture.AddRequestAsync(
            s1, "Pending", createdAt: baseTime, priority: "Normal");
        var newerUrgent = await fixture.AddRequestAsync(
            s1, "Pending", createdAt: baseTime.AddMinutes(1), priority: "Urgent");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(olderNormal)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(newerUrgent)).Status);
    }

    [Fact]
    public async Task RecoveryAfterWorkerDisabledAssignsOnRestart()
    {
        var s1 = await fixture.AddSpecialtyAsync();

        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var key = $"recovery-{Guid.NewGuid():N}";
        Guid requestId;
        await using (var host = await DidarProcess.StartAsync(
            fixture.ConnectionString,
            new Dictionary<string, string> { ["Assignment__Enabled"] = "false" }))
        {
            using var client = new HttpClient { BaseAddress = host.Address };
            requestId = await RegisterAsync(client, key, s1);
        }

        Assert.Equal("Pending", (await ReadRequestAsync(requestId)).Status);

        await using var restarted = await DidarProcess.StartAsync(fixture.ConnectionString);
        await Poll.UntilAsync(async () => (await ReadRequestAsync(requestId)).Status == "Assigned");
        Assert.Equal(expert, (await ReadRequestAsync(requestId)).ExpertId);
    }

    [Fact]
    public async Task ReplayAfterAssignmentReturnsCurrentStatus()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();

        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var key = $"replay-assign-{Guid.NewGuid():N}";
        var requestId = await RegisterAsync(client, key, s1);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(requestId)).Status == "Assigned");

        using var replay = await client.PostAsJsonAsync("/requests",
            new { requestKey = key, specialtyId = s2, priority = "Urgent" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = await replay.Content.ReadFromJsonAsync<RequestResponse>();
        Assert.Equal(requestId, result!.RequestId);
        Assert.Equal("Assigned", result.Status);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT specialty_id, priority FROM requests WHERE request_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(s1, reader.GetGuid(0));
        Assert.Equal("Normal", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    private async Task<Guid> RegisterAsync(HttpClient client, string key, Guid specialty, string priority = "Normal")
    {
        using var response = await client.PostAsJsonAsync("/requests",
            new { requestKey = key, specialtyId = specialty, priority });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RequestResponse>();
        return body!.RequestId;
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

    private async Task<DateTime?> ReadExpertLastAssignedAsync(Guid expertId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT last_assigned_at FROM experts WHERE expert_id = @id", connection);
        command.Parameters.AddWithValue("id", expertId);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (DateTime)result;
    }
}
