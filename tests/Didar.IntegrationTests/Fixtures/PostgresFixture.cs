using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Didar.IntegrationTests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    public static readonly Guid SpecialtyOne = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid SpecialtyTwo = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private readonly string _adminConnectionString;
    private readonly string _databaseName = $"didar_test_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public PostgresFixture()
    {
        var configured = Environment.GetEnvironmentVariable("DidarTests__Postgres")
            ?? "Host=localhost;Port=54329;Database=postgres;Username=didar;Password=didar-local";
        _adminConnectionString = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false
        }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
            Pooling = false
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<DidarDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using var db = new DidarDbContext(options);
        await db.Database.MigrateAsync();
        db.Specialties.AddRange(new Specialty { SpecialtyId = SpecialtyOne }, new Specialty { SpecialtyId = SpecialtyTwo });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public async Task<Guid> AddSpecialtyAsync(Guid? specialtyId = null)
    {
        var id = specialtyId ?? Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO specialties (specialty_id) VALUES (@id)", connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<Guid> AddExpertAsync(
        string status,
        int capacity,
        DateTime? lastAssignedAt = null,
        Guid? expertId = null)
    {
        var id = expertId ?? Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO experts (expert_id, status, capacity, last_assigned_at) VALUES (@id, @status, @capacity, @last)",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("capacity", capacity);
        command.Parameters.AddWithValue("last", (object?)lastAssignedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public async Task AddExpertSpecialtyAsync(Guid expertId, Guid specialtyId, string status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO expert_specialties (expert_id, specialty_id, status) VALUES (@expert, @specialty, @status)",
            connection);
        command.Parameters.AddWithValue("expert", expertId);
        command.Parameters.AddWithValue("specialty", specialtyId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Guid> AddRequestAsync(
        Guid specialtyId,
        string status,
        Guid? expertId = null,
        DateTime? createdAt = null,
        Guid? requestId = null,
        string priority = "Normal")
    {
        var id = requestId ?? Guid.NewGuid();
        var key = $"seed-{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO requests (request_id, request_key, specialty_id, priority, status, created_at, expert_id)
            VALUES (@id, @key, @specialty, @priority, @status, @created, @expert)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("specialty", specialtyId);
        command.Parameters.AddWithValue("priority", priority);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("created", NpgsqlTypes.NpgsqlDbType.TimestampTz, createdAt ?? DateTime.UtcNow);
        command.Parameters.AddWithValue("expert", (object?)expertId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return id;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
