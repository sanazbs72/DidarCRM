using System.Data;
using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Didar.Api.Features.Assignment;

public enum AssignmentOutcome
{
    Assigned,
    NoAssignableRequest,
    Retry
}

public sealed class AssignmentService(
    DidarDbContext dbContext,
    TimeProvider timeProvider,
    IAssignmentBarrier barrier,
    QueuePolicy queuePolicy,
    IConfiguration configuration)
{
    private readonly TimeSpan _rankDelay =
        TimeSpan.FromMilliseconds(configuration.GetValue("Assignment:RankDelayMs", 0));

   
    public async Task<AssignmentOutcome> TryAssignNextAsync(Guid specialtyId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
            var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            var specialtyExists = await ScalarExistsAsync(
                connection, npgsqlTransaction,
                "SELECT 1 FROM specialties WHERE specialty_id = @specialty FOR UPDATE",
                command => command.Parameters.AddWithValue("specialty", specialtyId),
                cancellationToken);
            if (!specialtyExists)
            {
                await transaction.CommitAsync(cancellationToken);
                return AssignmentOutcome.NoAssignableRequest;
            }
            
            var now = timeProvider.GetUtcNow();
            var headRequestId = await ReadQueueHeadAsync(
                connection, npgsqlTransaction, specialtyId, now, queuePolicy.AgingThreshold, cancellationToken);
            if (headRequestId is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return AssignmentOutcome.NoAssignableRequest;
            }

            var candidates = await ReadCandidatesAsync(
                connection, npgsqlTransaction, specialtyId, cancellationToken);
            if (candidates.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return AssignmentOutcome.NoAssignableRequest;
            }

            var selected = Rank(candidates);

            await barrier.ArriveAsync(specialtyId, selected.ExpertId, cancellationToken);

            if (_rankDelay > TimeSpan.Zero)
            {
                await Task.Delay(_rankDelay, cancellationToken);
            }

            await LockExpertAsync(connection, npgsqlTransaction, selected.ExpertId, cancellationToken);

            var transferred = await TransferRequestAsync(
                connection, npgsqlTransaction, headRequestId.Value, selected.ExpertId, cancellationToken);
            if (!transferred)
            {
                await transaction.CommitAsync(cancellationToken);
                return AssignmentOutcome.NoAssignableRequest;
            }

            await TouchExpertAsync(connection, npgsqlTransaction, selected.ExpertId, now, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return AssignmentOutcome.Assigned;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await RollBackSafelyAsync(transaction);
            return AssignmentOutcome.Retry;
        }
        catch (DbUpdateException ex) when (IsSerializationFailure(ex))
        {
            await RollBackSafelyAsync(transaction);
            return AssignmentOutcome.Retry;
        }
    }

    private static async Task<bool> ScalarExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configure(command);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task<Guid?> ReadQueueHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid specialtyId,
        DateTimeOffset now,
        TimeSpan aging,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT request_id
            FROM requests
            WHERE specialty_id = @specialty AND status = 'Pending'
            ORDER BY {QueuePolicy.EffectivePrioritySql}, created_at ASC, request_id ASC
            LIMIT 1
            """,
            connection, transaction);
        command.Parameters.AddWithValue("specialty", specialtyId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("aging", aging);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (Guid)result;
    }

    private static async Task<List<Candidate>> ReadCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid specialtyId,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        await using var command = new NpgsqlCommand(
            """
            SELECT e.expert_id, e.capacity, e.last_assigned_at, COUNT(r.expert_id) AS load
            FROM experts e
            JOIN expert_specialties es ON es.expert_id = e.expert_id AND es.specialty_id = @specialty
            LEFT JOIN requests r ON r.expert_id = e.expert_id AND r.status = 'Assigned'
            WHERE e.status = 'Active' AND es.status = 'Active'
            GROUP BY e.expert_id, e.capacity, e.last_assigned_at
            HAVING COUNT(r.expert_id) < e.capacity
            """,
            connection, transaction);
        command.Parameters.AddWithValue("specialty", specialtyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new Candidate(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                checked((int)reader.GetInt64(3))));
        }

        return candidates;
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

    private static async Task<bool> TransferRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        Guid expertId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE requests
            SET expert_id = @expert, status = 'Assigned'
            WHERE request_id = @request AND status = 'Pending'
            """,
            connection, transaction);
        command.Parameters.AddWithValue("expert", expertId);
        command.Parameters.AddWithValue("request", requestId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task TouchExpertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid expertId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE experts SET last_assigned_at = @now WHERE expert_id = @expert",
            connection, transaction);
        command.Parameters.AddWithValue("expert", expertId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Candidate Rank(IReadOnlyList<Candidate> candidates)
    {
        Candidate? best = null;
        foreach (var candidate in candidates)
        {
            if (best is null || IsBetter(candidate, best))
            {
                best = candidate;
            }
        }

        return best!;
    }

    private static bool IsBetter(Candidate a, Candidate b)
    {
        var aRank = (long)a.Load * b.Capacity;
        var bRank = (long)b.Load * a.Capacity;
        if (aRank != bRank)
        {
            return aRank < bRank;
        }

        var aNull = a.LastAssignedAt is null;
        var bNull = b.LastAssignedAt is null;
        if (aNull != bNull)
        {
            return aNull;
        }

        if (!aNull)
        {
            var comparison = a.LastAssignedAt!.Value.CompareTo(b.LastAssignedAt!.Value);
            if (comparison != 0)
            {
                return comparison < 0;
            }
        }

        return CompareUuids(a.ExpertId, b.ExpertId) < 0;
    }

    private static int CompareUuids(Guid a, Guid b)
    {
        Span<byte> aBytes = stackalloc byte[16];
        Span<byte> bBytes = stackalloc byte[16];
        a.TryWriteBytes(aBytes, bigEndian: true, out _);
        b.TryWriteBytes(bBytes, bigEndian: true, out _);
        return aBytes.SequenceCompareTo(bBytes);
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
            // The transaction is already aborted by the serialization failure.
        }
    }

    private sealed record Candidate(Guid ExpertId, int Capacity, DateTime? LastAssignedAt, int Load);
}
