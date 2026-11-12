using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class MultiInstanceAssignmentTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ConcurrentWorkersSplitRequestsBetweenExperts()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();

        var a = await fixture.AddExpertAsync("Active", 2);
        var b = await fixture.AddExpertAsync("Active", 3);
        await fixture.AddExpertSpecialtyAsync(a, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(a, s2, "Active");
        await fixture.AddExpertSpecialtyAsync(b, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(b, s2, "Active");
        await fixture.AddRequestAsync(s1, "Assigned", b);

        var r1 = await fixture.AddRequestAsync(s1, "Pending");
        var r2 = await fixture.AddRequestAsync(s2, "Pending");

        var barrierDir = Path.Combine(Path.GetTempPath(), $"didar-barrier-{Guid.NewGuid():N}");
        var barrier = new Dictionary<string, string>
        {
            ["Assignment__BarrierDirectory"] = barrierDir,
            ["Assignment__BarrierParties"] = "2",
            ["Assignment__BarrierTimeoutSeconds"] = "20",
        };

        var env1 = new Dictionary<string, string>(barrier) { ["Assignment__SpecialtyId"] = s1.ToString() };
        var env2 = new Dictionary<string, string>(barrier) { ["Assignment__SpecialtyId"] = s2.ToString() };

        await using var host1 = await DidarProcess.StartAsync(fixture.ConnectionString, env1);
        await using var host2 = await DidarProcess.StartAsync(fixture.ConnectionString, env2);

        await Poll.UntilAsync(async () =>
            (await ReadRequestAsync(r1)).Status == "Assigned"
            && (await ReadRequestAsync(r2)).Status == "Assigned", timeoutMs: 30000);

        var assignedExperts = new[]
        {
            (await ReadRequestAsync(r1)).ExpertId!.Value,
            (await ReadRequestAsync(r2)).ExpertId!.Value,
        };
        Assert.Contains(a, assignedExperts);
        Assert.Contains(b, assignedExperts);
        Assert.Equal(1, await CountAssignedAsync(a));
        Assert.Equal(2, await CountAssignedAsync(b));
    }

    [Fact]
    public async Task ConcurrentAttemptsOnSameRequestAssignAtMostOnce()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 10);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var request = await fixture.AddRequestAsync(s1, "Pending");

        await using var host1 = await DidarProcess.StartAsync(fixture.ConnectionString);
        await using var host2 = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(request)).Status == "Assigned", timeoutMs: 30000);
        Assert.Equal(expert, (await ReadRequestAsync(request)).ExpertId);
        Assert.Equal(1, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task TwoRequestsRacingForLastSlotAssignOnlyOne()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(expert, s2, "Active");
        var r1 = await fixture.AddRequestAsync(s1, "Pending");
        var r2 = await fixture.AddRequestAsync(s2, "Pending");

        await using var host1 = await DidarProcess.StartAsync(fixture.ConnectionString);
        await using var host2 = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () =>
            (await ReadRequestAsync(r1)).Status == "Assigned"
            || (await ReadRequestAsync(r2)).Status == "Assigned", timeoutMs: 30000);

        await Task.Delay(2000);
        var r1Assigned = (await ReadRequestAsync(r1)).Status == "Assigned";
        var r2Assigned = (await ReadRequestAsync(r2)).Status == "Assigned";
        Assert.NotEqual(r1Assigned, r2Assigned);
        Assert.Equal(1, await CountAssignedAsync(expert));
    }

    [Fact]
    public async Task TwentyRequestsForCapacityFiveAssignExactlyFive()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 5);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        for (var i = 0; i < 20; i++)
        {
            await fixture.AddRequestAsync(s1, "Pending");
        }

        await using var host1 = await DidarProcess.StartAsync(fixture.ConnectionString);
        await using var host2 = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () => await CountRequestsAsync(s1, "Assigned") == 5, timeoutMs: 40000);
        Assert.Equal(15, await CountRequestsAsync(s1, "Pending"));
        Assert.Equal(5, await CountAssignedAsync(expert));
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

    private async Task<int> CountRequestsAsync(Guid specialtyId, string status)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM requests WHERE specialty_id = @specialty AND status = @status", connection);
        command.Parameters.AddWithValue("specialty", specialtyId);
        command.Parameters.AddWithValue("status", status);
        return checked((int)(long)(await command.ExecuteScalarAsync())!);
    }
}
