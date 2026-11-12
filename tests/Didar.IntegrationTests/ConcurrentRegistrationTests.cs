using System.Net;
using System.Net.Http.Json;
using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ConcurrentRegistrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TwentyConcurrentRegistrationsAcrossTwoProcessesCreateOneRequest()
    {
        await using var firstHost = await DidarProcess.StartAsync(fixture.ConnectionString);
        await using var secondHost = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var firstClient = new HttpClient { BaseAddress = firstHost.Address };
        using var secondClient = new HttpClient { BaseAddress = secondHost.Address };
        using var gate = new ManualResetEventSlim(false);
        var key = $"concurrent-{Guid.NewGuid():N}";

        var requests = Enumerable.Range(0, 20).Select(index =>
            Task.Run(async () =>
            {
                gate.Wait();
                var client = index % 2 == 0 ? firstClient : secondClient;
                using var response = await client.PostAsJsonAsync("/requests", RegistrationTests.Payload(key));
                var body = await response.Content.ReadFromJsonAsync<RegistrationTests.RequestResponse>();
                return (response.StatusCode, Body: body);
            })).ToArray();

        gate.Set();
        var results = await Task.WhenAll(requests);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(19, results.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.All(results, x => Assert.NotNull(x.Body));
        Assert.Single(results.Select(x => x.Body!.RequestId).Distinct());

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM requests WHERE request_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        await reader.DisposeAsync();
        await using var lookup = new NpgsqlCommand(
            "SELECT request_id FROM requests WHERE request_key = @key", connection);
        lookup.Parameters.AddWithValue("key", key);
        Assert.Equal(results[0].Body!.RequestId, (Guid)(await lookup.ExecuteScalarAsync())!);
    }
}
