using System.Net;
using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class QueuePolicyTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 10, 0, TimeSpan.Zero);
    private const string FixedNowText = "2026-01-01T00:10:00Z";

    private static DateTime At(int secondsAgo) => FixedNow.UtcDateTime.AddSeconds(-secondsAgo);

    [Fact]
    public async Task UrgentPrecedesSubThresholdNormal()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var normal = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(299), priority: "Normal");
        var urgent = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(60), priority: "Urgent");

        await using var host = await StartFixedHostAsync();

        await Poll.UntilAsync(async () => (await ReadRequestAsync(urgent)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(normal)).Status);
    }

    [Fact]
    public async Task ExistingUrgentBeatsNewNormal()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var urgent = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(120), priority: "Urgent");
        var normal = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(30), priority: "Normal");

        await using var host = await StartFixedHostAsync();

        await Poll.UntilAsync(async () => (await ReadRequestAsync(urgent)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(normal)).Status);
    }

    [Fact]
    public async Task TieBreakUsesRequestId()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var smaller = await fixture.AddRequestAsync(
            s1, "Pending", createdAt: At(60), priority: "Urgent",
            requestId: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var larger = await fixture.AddRequestAsync(
            s1, "Pending", createdAt: At(60), priority: "Urgent",
            requestId: Guid.Parse("00000000-0000-0000-0000-000000000002"));

        await using var host = await StartFixedHostAsync();

        await Poll.UntilAsync(async () => (await ReadRequestAsync(smaller)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(larger)).Status);
    }

    [Fact]
    public async Task AgedNormalPrecedesNewerUrgent()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var normal = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(300), priority: "Normal");
        var urgent = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(60), priority: "Urgent");

        await using var host = await StartFixedHostAsync();

        await Poll.UntilAsync(async () => (await ReadRequestAsync(normal)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(urgent)).Status);
        Assert.Equal("Normal", await ReadRequestPriorityAsync(normal));
    }

    [Fact]
    public async Task AgingOrderingFollowsExample()
    {
        var s1 = await fixture.AddSpecialtyAsync();
        var expert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(expert, s1, "Active");

        var b = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(360), priority: "Normal"); // 6 min
        var d = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(180), priority: "Urgent"); // 3 min
        var c = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(60), priority: "Urgent");  // 1 min
        var a = await fixture.AddRequestAsync(s1, "Pending", createdAt: At(120), priority: "Normal"); // 2 min

        await using var host = await StartFixedHostAsync();
        using var client = new HttpClient { BaseAddress = host.Address };

        await Poll.UntilAsync(async () => (await ReadRequestAsync(b)).Status == "Assigned");
        await CloseAsync(client, b);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(d)).Status == "Assigned");
        await CloseAsync(client, d);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(c)).Status == "Assigned");
        await CloseAsync(client, c);

        await Poll.UntilAsync(async () => (await ReadRequestAsync(a)).Status == "Assigned");
    }

    [Fact]
    public async Task NoEligibleSpecialtyStaysPendingWhileOtherAssigns()
    {
        var financial = await fixture.AddSpecialtyAsync();
        var legal = await fixture.AddSpecialtyAsync();

        var legalExpert = await fixture.AddExpertAsync("Active", 1);
        await fixture.AddExpertSpecialtyAsync(legalExpert, legal, "Active");
        var financialExpert = await fixture.AddExpertAsync("Active", 1);

        var f1 = await fixture.AddRequestAsync(financial, "Pending", createdAt: At(600), priority: "Normal");
        var f2 = await fixture.AddRequestAsync(financial, "Pending", createdAt: At(60), priority: "Normal");
        var legalRequest = await fixture.AddRequestAsync(legal, "Pending", createdAt: At(1800), priority: "Normal");

        await using var host = await StartFixedHostAsync();

        await Poll.UntilAsync(async () => (await ReadRequestAsync(legalRequest)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(f1)).Status);
        Assert.Equal("Pending", (await ReadRequestAsync(f2)).Status);

        await fixture.AddExpertSpecialtyAsync(financialExpert, financial, "Active");

        await Poll.UntilAsync(async () => (await ReadRequestAsync(f1)).Status == "Assigned");
        Assert.Equal("Pending", (await ReadRequestAsync(f2)).Status);
    }

    private Task<DidarProcess> StartFixedHostAsync() =>
        DidarProcess.StartAsync(
            fixture.ConnectionString,
            new Dictionary<string, string> { ["Assignment__FixedNow"] = FixedNowText });

    private async Task CloseAsync(HttpClient client, Guid requestId)
    {
        using var response = await client.PostAsync($"/requests/{requestId:D}/close", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    private async Task<string> ReadRequestPriorityAsync(Guid requestId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT priority FROM requests WHERE request_id = @id", connection);
        command.Parameters.AddWithValue("id", requestId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
