using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Didar.Api.Persistence;
using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RegistrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task NewRequestIsPendingAndCanBeRead()
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var key = $"new-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync("/requests", Payload(key));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<RequestResponse>();
        Assert.NotNull(created);
        Assert.Equal("Pending", created.Status);
        Assert.EndsWith($"/requests/{created.RequestId:D}", response.Headers.Location!.ToString());

        var read = await client.GetFromJsonAsync<RequestResponse>($"/requests/{created.RequestId:D}");
        Assert.Equal(created, read);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT specialty_id, priority, status FROM requests WHERE request_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(PostgresFixture.SpecialtyOne, reader.GetGuid(0));
        Assert.Equal("Normal", reader.GetString(1));
        Assert.Equal("Pending", reader.GetString(2));
    }

    [Fact]
    public async Task InvalidInputReturnsValidationProblemAndUnknownIdsAreNotFound()
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };

        using var invalid = await client.PostAsJsonAsync("/requests", new { requestKey = " ", specialtyId = PostgresFixture.SpecialtyOne, priority = "Normal" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);

        using var unknown = await client.GetAsync($"/requests/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var malformed = await client.GetAsync("/requests/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var oversized = await client.PostAsJsonAsync("/requests", Payload(new string('ا', 513)));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        var invalidInputs = new HttpContent[]
        {
            JsonContent.Create(new { specialtyId = PostgresFixture.SpecialtyOne, priority = "Normal" }),
            JsonContent.Create(new { requestKey = "missing-specialty", priority = "Normal" }),
            JsonContent.Create(new { requestKey = "missing-priority", specialtyId = PostgresFixture.SpecialtyOne }),
            JsonContent.Create(Payload($"nul-{Guid.NewGuid():N}\0-key")),
            new StringContent("null", Encoding.UTF8, "application/json"),
        };
        foreach (var body in invalidInputs)
        {
            using (body)
            using (var response = await client.PostAsync("/requests", body))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task InvalidSpecialtyAndPriorityDoNotCreateRequests()
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        using var invalidSpecialty = await client.PostAsJsonAsync("/requests", Payload($"invalid-{Guid.NewGuid():N}", Guid.NewGuid()));
        using var invalidPriority = await client.PostAsJsonAsync("/requests", Payload($"invalid-{Guid.NewGuid():N}", PostgresFixture.SpecialtyOne, "urgent"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidSpecialty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPriority.StatusCode);
    }

    [Fact]
    public async Task RequestKeysUseExactComparison()
    {
        await using var host = await DidarProcess.StartAsync(fixture.ConnectionString);
        using var client = new HttpClient { BaseAddress = host.Address };
        var prefix = $"key-{Guid.NewGuid():N}";
        var keys = new[] { prefix, prefix.ToUpperInvariant(), $"{prefix} ", $"{prefix}é", $"{prefix}é" };
        var responses = new List<RequestResponse>();
        foreach (var key in keys)
        {
            using var response = await client.PostAsJsonAsync("/requests", Payload(key));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            responses.Add((await response.Content.ReadFromJsonAsync<RequestResponse>())!);
        }

        Assert.Equal(keys.Length, responses.Select(x => x.RequestId).Distinct().Count());
    }

    internal static object Payload(string key, Guid? specialty = null, string priority = "Normal") =>
        new { requestKey = key, specialtyId = specialty ?? PostgresFixture.SpecialtyOne, priority };

    internal sealed record RequestResponse(Guid RequestId, string Status);
}
