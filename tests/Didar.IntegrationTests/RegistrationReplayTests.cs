using System.Net;
using System.Net.Http.Json;
using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RegistrationReplayTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReplayReturnsSameIdAndPreservesOriginalRegistration()
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var key = $"replay-{Guid.NewGuid():N}";
        using var first = await client.PostAsJsonAsync("/requests", RegistrationTests.Payload(key));
        var initial = await first.Content.ReadFromJsonAsync<RegistrationTests.RequestResponse>();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await client.PostAsJsonAsync("/requests",
            RegistrationTests.Payload(key, PostgresFixture.SpecialtyTwo, "Urgent"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var repeated = await replay.Content.ReadFromJsonAsync<RegistrationTests.RequestResponse>();
        Assert.Equal(initial, repeated);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT specialty_id, priority FROM requests WHERE request_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(PostgresFixture.SpecialtyOne, reader.GetGuid(0));
        Assert.Equal("Normal", reader.GetString(1));
    }

    [Fact]
    public async Task ReplayReturnsCurrentStatus()
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var key = $"assigned-{Guid.NewGuid():N}";
        using var first = await client.PostAsJsonAsync("/requests", RegistrationTests.Payload(key));
        var initial = await first.Content.ReadFromJsonAsync<RegistrationTests.RequestResponse>();

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            var expertId = Guid.NewGuid();
            await using (var insertExpert = new NpgsqlCommand(
                "INSERT INTO experts (expert_id, status, capacity) VALUES (@expert, 'Active', 10)", connection))
            {
                insertExpert.Parameters.AddWithValue("expert", expertId);
                await insertExpert.ExecuteNonQueryAsync();
            }

            await using var update = new NpgsqlCommand(
                "UPDATE requests SET status = 'Assigned', expert_id = @expert WHERE request_id = @id", connection);
            update.Parameters.AddWithValue("id", initial!.RequestId);
            update.Parameters.AddWithValue("expert", expertId);
            await update.ExecuteNonQueryAsync();
        }

        using var replay = await client.PostAsJsonAsync("/requests", RegistrationTests.Payload(key));
        var result = await replay.Content.ReadFromJsonAsync<RegistrationTests.RequestResponse>();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(initial!.RequestId, result!.RequestId);
        Assert.Equal("Assigned", result.Status);
        var read = await client.GetFromJsonAsync<RegistrationTests.RequestResponse>($"/requests/{result.RequestId:D}");
        Assert.Equal("Assigned", read!.Status);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("invalid-priority")]
    public async Task InvalidReplayDoesNotChangeExistingRequest(string invalidField)
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var key = $"invalid-replay-{Guid.NewGuid():N}";
        using var first = await client.PostAsJsonAsync("/requests", RegistrationTests.Payload(key));
        var initial = await first.Content.ReadFromJsonAsync<RegistrationTests.RequestResponse>();
        var replayPayload = invalidField == "invalid"
            ? RegistrationTests.Payload(key, Guid.NewGuid())
            : RegistrationTests.Payload(key, PostgresCollectionId(), "invalid");
        using var replay = await client.PostAsJsonAsync("/requests", replayPayload);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        var current = await client.GetFromJsonAsync<RegistrationTests.RequestResponse>($"/requests/{initial!.RequestId:D}");
        Assert.Equal("Pending", current!.Status);
    }

    private static Guid PostgresCollectionId() => PostgresFixture.SpecialtyOne;
}
