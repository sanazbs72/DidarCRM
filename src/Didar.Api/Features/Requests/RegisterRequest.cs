using System.Data;
using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Didar.Api.Features.Requests;

public sealed class RegisterRequest(DidarDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<RegistrationResult> ExecuteAsync(
        RegisterRequestInput input,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        var specialtyExists = await dbContext.Specialties
            .AnyAsync(x => x.SpecialtyId == input.SpecialtyId, cancellationToken);
        if (!specialtyExists)
        {
            return RegistrationResult.Invalid("specialtyId", "The specialty does not exist.");
        }

        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO requests (request_id, request_key, specialty_id, priority, status, created_at)
            VALUES (@request_id, @request_key, @specialty_id, @priority, 'Pending', @created_at)
            ON CONFLICT (request_key) DO NOTHING
            RETURNING request_id, status
            """,
            connection,
            (NpgsqlTransaction)transaction.GetDbTransaction());
        var requestId = Guid.NewGuid();
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("request_key", NpgsqlDbType.Text, input.RequestKey!);
        command.Parameters.AddWithValue("specialty_id", input.SpecialtyId!.Value);
        command.Parameters.AddWithValue("priority", NpgsqlDbType.Text, input.Priority!);
        command.Parameters.AddWithValue(
            "created_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());

        Guid actualId;
        string status;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                actualId = reader.GetGuid(0);
                status = reader.GetString(1);
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return RegistrationResult.New(actualId, status);
            }
        }

        await using var lookup = new NpgsqlCommand(
            "SELECT request_id, status FROM requests WHERE request_key = @request_key",
            connection,
            (NpgsqlTransaction)transaction.GetDbTransaction());
        lookup.Parameters.AddWithValue("request_key", NpgsqlDbType.Text, input.RequestKey!);
        await using var existing = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await existing.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Conflicting request key was not visible to the follow-up read.");
        }

        actualId = existing.GetGuid(0);
        status = existing.GetString(1);
        await existing.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return RegistrationResult.Existing(actualId, status);
    }
}

public sealed record RegistrationResult(
    Guid RequestId,
    string Status,
    bool Created,
    Dictionary<string, string[]> ValidationErrors)
{
    public static RegistrationResult New(Guid id, string status) => new(id, status, true, []);
    public static RegistrationResult Existing(Guid id, string status) => new(id, status, false, []);
    public static RegistrationResult Invalid(string field, string error) =>
        new(Guid.Empty, string.Empty, false, new() { [field] = [error] });
}
