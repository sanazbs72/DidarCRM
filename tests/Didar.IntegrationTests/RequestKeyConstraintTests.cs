using Didar.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Didar.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RequestKeyConstraintTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DatabaseRejectsDuplicateAndNullKeys()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var key = $"constraint-{Guid.NewGuid():N}";
        await InsertAsync(connection, Guid.NewGuid(), key);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(connection, Guid.NewGuid(), key));
        Assert.Equal("23505", duplicate.SqlState);

        var missing = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(connection, Guid.NewGuid(), null));
        Assert.Equal("23502", missing.SqlState);
    }

    private static async Task InsertAsync(NpgsqlConnection connection, Guid id, string? key)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO requests (request_id, request_key, specialty_id, priority, status, created_at)
            VALUES (@id, @key, @specialty, 'Normal', 'Pending', now())
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("key", NpgsqlTypes.NpgsqlDbType.Text, (object?)key ?? DBNull.Value);
        command.Parameters.AddWithValue("specialty", PostgresFixture.SpecialtyOne);
        await command.ExecuteNonQueryAsync();
    }
}
