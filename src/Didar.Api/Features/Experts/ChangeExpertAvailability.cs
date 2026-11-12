using System.Data;
using Didar.Api.Features.Assignment;
using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Didar.Api.Features.Experts;

public enum ChangeAvailabilityOutcome
{
    Success,
    Conflict,
    NotFound,
    RetryExhausted
}

public sealed record ChangeAvailabilityResult(ChangeAvailabilityOutcome Outcome, string Status);

public sealed class ChangeExpertAvailability(
    IServiceScopeFactory scopeFactory,
    IAssignmentNotifier notifier)
{
    private const int MaxAttempts = 3;
    private const string Active = "Active";
    private const string PendingDeactivation = "PendingDeactivation";
    private const string Inactive = "Inactive";

    public async Task<ChangeAvailabilityResult> SetCapacityAsync(Guid expertId, int capacity, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var result = await TrySetCapacityAsync(expertId, capacity, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.RetryExhausted, string.Empty);
    }

    public async Task<ChangeAvailabilityResult> SetExpertStatusAsync(Guid expertId, string targetStatus, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var result = await TrySetExpertStatusAsync(expertId, targetStatus, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.RetryExhausted, string.Empty);
    }

    public async Task<ChangeAvailabilityResult> SetSpecialtyStatusAsync(Guid expertId, Guid specialtyId, string targetStatus, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var result = await TrySetSpecialtyStatusAsync(expertId, specialtyId, targetStatus, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.RetryExhausted, string.Empty);
    }

    private async Task<ChangeAvailabilityResult?> TrySetCapacityAsync(Guid expertId, int capacity, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DidarDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            var status = await LockAndReadExpertStatusAsync(connection, npgsqlTransaction, expertId, cancellationToken);
            if (status is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.NotFound, string.Empty);
            }

            await using (var update = new NpgsqlCommand(
                "UPDATE experts SET capacity = @capacity WHERE expert_id = @id",
                connection, npgsqlTransaction))
            {
                update.Parameters.AddWithValue("capacity", capacity);
                update.Parameters.AddWithValue("id", expertId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            notifier.Notify();
            return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.Success, status);
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

    private async Task<ChangeAvailabilityResult?> TrySetExpertStatusAsync(Guid expertId, string targetStatus, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DidarDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            var current = await LockAndReadExpertStatusAsync(connection, npgsqlTransaction, expertId, cancellationToken);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.NotFound, string.Empty);
            }

            if (targetStatus == Active)
            {
                if (current == Inactive)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.Conflict, Inactive);
                }

                await UpdateExpertStatusAsync(connection, npgsqlTransaction, expertId, Active, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                notifier.Notify();
                return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.Success, Active);
            }

            var resolved = await HasOpenWorkForExpertAsync(connection, npgsqlTransaction, expertId, cancellationToken)
                ? PendingDeactivation
                : Inactive;
            await UpdateExpertStatusAsync(connection, npgsqlTransaction, expertId, resolved, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.Success, resolved);
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

    private async Task<ChangeAvailabilityResult?> TrySetSpecialtyStatusAsync(Guid expertId, Guid specialtyId, string targetStatus, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DidarDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            var expertStatus = await LockAndReadExpertStatusAsync(connection, npgsqlTransaction, expertId, cancellationToken);
            if (expertStatus is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.NotFound, string.Empty);
            }

            var current = await ReadSpecialtyStatusAsync(connection, npgsqlTransaction, expertId, specialtyId, cancellationToken);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.NotFound, string.Empty);
            }

            if (targetStatus == Active)
            {
                if (current == Inactive)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.Conflict, Inactive);
                }

                await UpdateSpecialtyStatusAsync(connection, npgsqlTransaction, expertId, specialtyId, Active, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                notifier.Notify();
                return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.Success, Active);
            }

            var resolved = await HasOpenWorkForSpecialtyAsync(connection, npgsqlTransaction, expertId, specialtyId, cancellationToken)
                ? PendingDeactivation
                : Inactive;
            await UpdateSpecialtyStatusAsync(connection, npgsqlTransaction, expertId, specialtyId, resolved, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ChangeAvailabilityResult(ChangeAvailabilityOutcome.Success, resolved);
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

    private static async Task<string?> LockAndReadExpertStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT status FROM experts WHERE expert_id = @id FOR UPDATE",
            connection, transaction);
        command.Parameters.AddWithValue("id", expertId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (string)result;
    }

    private static async Task<string?> ReadSpecialtyStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        Guid specialtyId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT status FROM expert_specialties WHERE expert_id = @expert AND specialty_id = @specialty",
            connection, transaction);
        command.Parameters.AddWithValue("expert", expertId);
        command.Parameters.AddWithValue("specialty", specialtyId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (string)result;
    }

    private static async Task<bool> HasOpenWorkForExpertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM requests WHERE expert_id = @expert AND status = 'Assigned')",
            connection, transaction);
        command.Parameters.AddWithValue("expert", expertId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> HasOpenWorkForSpecialtyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        Guid specialtyId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM requests WHERE expert_id = @expert AND specialty_id = @specialty AND status = 'Assigned')",
            connection, transaction);
        command.Parameters.AddWithValue("expert", expertId);
        command.Parameters.AddWithValue("specialty", specialtyId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task UpdateExpertStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE experts SET status = @status WHERE expert_id = @id",
            connection, transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", expertId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateSpecialtyStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        Guid specialtyId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE expert_specialties SET status = @status WHERE expert_id = @expert AND specialty_id = @specialty",
            connection, transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("expert", expertId);
        command.Parameters.AddWithValue("specialty", specialtyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
