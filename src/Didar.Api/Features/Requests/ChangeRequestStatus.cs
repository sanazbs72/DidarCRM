using System.Data;
using Didar.Api.Features.Assignment;
using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Didar.Api.Features.Requests;

public enum ChangeOutcome
{
    Success,
    Conflict,
    NotFound,
    RetryExhausted
}

public sealed record ChangeResult(ChangeOutcome Outcome, string Status);

public sealed class ChangeRequestStatus(
    IServiceScopeFactory scopeFactory,
    IAssignmentNotifier notifier)
{
    private const int MaxAttempts = 3;
    private const string Pending = "Pending";
    private const string Assigned = "Assigned";
    private const string Closed = "Closed";
    private const string Cancelled = "Cancelled";
    private const string PendingDeactivation = "PendingDeactivation";
    private const string Inactive = "Inactive";

    public Task<ChangeResult> CloseAsync(Guid requestId, CancellationToken cancellationToken) =>
        ExecuteAsync(requestId, target: Closed, cancellationToken);

    public Task<ChangeResult> CancelAsync(Guid requestId, CancellationToken cancellationToken) =>
        ExecuteAsync(requestId, target: Cancelled, cancellationToken);

    private async Task<ChangeResult> ExecuteAsync(Guid requestId, string target, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var result = await TryOnceAsync(requestId, target, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return new ChangeResult(ChangeOutcome.RetryExhausted, string.Empty);
    }

    private async Task<ChangeResult?> TryOnceAsync(Guid requestId, string target, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DidarDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            var current = await ReadRequestAsync(connection, npgsqlTransaction, requestId, cancellationToken);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChangeResult(ChangeOutcome.NotFound, string.Empty);
            }

            var (status, expertId, specialtyId) = current.Value;

            if (target == Closed)
            {
                if (status == Closed)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new ChangeResult(ChangeOutcome.Success, Closed);
                }

                if (status != Assigned)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new ChangeResult(ChangeOutcome.Conflict, status);
                }

                await LockExpertAsync(connection, npgsqlTransaction, expertId!.Value, cancellationToken);
                if (!await TransitionAsync(connection, npgsqlTransaction, requestId, Assigned, Closed, cancellationToken))
                {
                    await RollBackSafelyAsync(transaction);
                    return null;
                }

                await DrainAsync(connection, npgsqlTransaction, expertId!.Value, specialtyId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                notifier.Notify();
                return new ChangeResult(ChangeOutcome.Success, Closed);
            }

            if (status == Cancelled)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChangeResult(ChangeOutcome.Success, Cancelled);
            }

            if (status == Closed)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChangeResult(ChangeOutcome.Conflict, Closed);
            }

            if (status == Pending)
            {
                if (await TransitionAsync(connection, npgsqlTransaction, requestId, Pending, Cancelled, cancellationToken))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new ChangeResult(ChangeOutcome.Success, Cancelled);
                }

                await RollBackSafelyAsync(transaction);
                return null;
            }

            await LockExpertAsync(connection, npgsqlTransaction, expertId!.Value, cancellationToken);
            if (!await TransitionAsync(connection, npgsqlTransaction, requestId, Assigned, Cancelled, cancellationToken))
            {
                await RollBackSafelyAsync(transaction);
                return null;
            }

            await DrainAsync(connection, npgsqlTransaction, expertId!.Value, specialtyId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            notifier.Notify();
            return new ChangeResult(ChangeOutcome.Success, Cancelled);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await RollBackSafelyAsync(transaction);
            return null;
        }
        catch (DbUpdateException ex) when (IsSerializationFailure(ex))
        {
            await RollBackSafelyAsync(transaction);
            return null;
        }
    }

    private static async Task<(string Status, Guid? ExpertId, Guid SpecialtyId)?> ReadRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT status, expert_id, specialty_id FROM requests WHERE request_id = @id",
            connection, transaction);
        command.Parameters.AddWithValue("id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetGuid(2));
    }

    private static async Task LockExpertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT expert_id FROM experts WHERE expert_id = @expert FOR UPDATE",
            connection, transaction);
        command.Parameters.AddWithValue("expert", expertId);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<bool> TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE requests SET status = @to WHERE request_id = @id AND status = @from",
            connection, transaction);
        command.Parameters.AddWithValue("to", toStatus);
        command.Parameters.AddWithValue("id", requestId);
        command.Parameters.AddWithValue("from", fromStatus);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task DrainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        Guid specialtyId,
        CancellationToken cancellationToken)
    {
        await using (var drainExpert = new NpgsqlCommand(
            """
            UPDATE experts SET status = 'Inactive'
            WHERE expert_id = @expert AND status = 'PendingDeactivation'
              AND NOT EXISTS (SELECT 1 FROM requests WHERE expert_id = @expert AND status = 'Assigned')
            """,
            connection, transaction))
        {
            drainExpert.Parameters.AddWithValue("expert", expertId);
            await drainExpert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var drainSpecialty = new NpgsqlCommand(
            """
            UPDATE expert_specialties SET status = 'Inactive'
            WHERE expert_id = @expert AND specialty_id = @specialty AND status = 'PendingDeactivation'
              AND NOT EXISTS (SELECT 1 FROM requests
                              WHERE expert_id = @expert AND specialty_id = @specialty AND status = 'Assigned')
            """,
            connection, transaction))
        {
            drainSpecialty.Parameters.AddWithValue("expert", expertId);
            drainSpecialty.Parameters.AddWithValue("specialty", specialtyId);
            await drainSpecialty.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static bool IsSerializationFailure(DbUpdateException exception) =>
        exception.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.SerializationFailure;

    private static async Task RollBackSafelyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // Already aborted by the serialization failure.
        }
    }
}
