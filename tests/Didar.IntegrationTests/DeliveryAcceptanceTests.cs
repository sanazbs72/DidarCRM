using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class DeliveryAcceptanceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TwoWorkersAssignOneRequestToSingleExpert()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var a = await fixture.AddExpertAsync("Active", 10, null, Guid.Parse("60000000-0000-0000-0000-000000000001"));
        var b = await fixture.AddExpertAsync("Active", 10, null, Guid.Parse("60000000-0000-0000-0000-000000000002"));
        await fixture.AddExpertSpecialtyAsync(a, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(b, s1, "Active");
        var request = await fixture.AddRequestAsync(s1, "Pending");

        await using var host1 = await DidarProcess.StartAsync(fixture.ConnectionString);
        await using var host2 = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(request)).Status == "Assigned");

        Assert.Equal(a, (await ReadRequestAsync(request)).ExpertId);
        Assert.Equal(1, await CountAssignedAsync(a));
        Assert.Equal(0, await CountAssignedAsync(b));
        Assert.NotNull(await ReadExpertLastAssignedAsync(a));
        Assert.Null(await ReadExpertLastAssignedAsync(b));
    }

    [Fact]
    public async Task SharedSlotWithAlternativeReassignsLoser()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var s2 = await fixture.AddSpecialtyAsync();
        var shared = await fixture.AddExpertAsync("Active", 1, null, Guid.Parse("70000000-0000-0000-0000-000000000001"));
        var alt1 = await fixture.AddExpertAsync("Active", 10, null, Guid.Parse("70000000-0000-0000-0000-000000000002"));
        var alt2 = await fixture.AddExpertAsync("Active", 10, null, Guid.Parse("70000000-0000-0000-0000-000000000003"));
        await fixture.AddExpertSpecialtyAsync(shared, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(shared, s2, "Active");
        await fixture.AddExpertSpecialtyAsync(alt1, s1, "Active");
        await fixture.AddExpertSpecialtyAsync(alt2, s2, "Active");
        var r1 = await fixture.AddRequestAsync(s1, "Pending");
        var r2 = await fixture.AddRequestAsync(s2, "Pending");

        await using var host1 = await DidarProcess.StartAsync(fixture.ConnectionString);
        await using var host2 = await DidarProcess.StartAsync(fixture.ConnectionString);

        await Poll.UntilAsync(async () =>
            (await ReadRequestAsync(r1)).Status == "Assigned"
            && (await ReadRequestAsync(r2)).Status == "Assigned");

        var experts = new[]
        {
            (await ReadRequestAsync(r1)).ExpertId!.Value,
            (await ReadRequestAsync(r2)).ExpertId!.Value,
        };

        // Exactly one request goes to the shared expert; the other to its alternative.
        Assert.Equal(1, experts.Count(x => x == shared));
        var other = experts.First(x => x != shared);
        Assert.True(other == alt1 || other == alt2);
        Assert.Equal(1, await CountAssignedAsync(shared));
    }

    [Fact]
    public async Task DelayedUrgentStillGetsSlot()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");
        var urgent = await fixture.AddRequestAsync(s1, "Pending", priority: "Urgent");
        var normal = await fixture.AddRequestAsync(s1, "Pending", priority: "Normal");

        // Hold the first request's processing so a second worker attempts the same queue.
        var env = new Dictionary<string, string> { ["Assignment__RankDelayMs"] = "2000" };
        await using var host1 = await DidarProcess.StartAsync(fixture.ConnectionString, env);
        await using var host2 = await DidarProcess.StartAsync(fixture.ConnectionString, env);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(urgent)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(normal)).Status);
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
