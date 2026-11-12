namespace Didar.Api.Features.Assignment;


public interface IAssignmentBarrier
{
    Task ArriveAsync(Guid specialtyId, Guid selectedExpertId, CancellationToken cancellationToken);
}

public sealed class NoopAssignmentBarrier : IAssignmentBarrier
{
    public static readonly NoopAssignmentBarrier Instance = new();

    public Task ArriveAsync(Guid specialtyId, Guid selectedExpertId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class DirectoryAssignmentBarrier : IAssignmentBarrier
{
    private readonly string _directory;
    private readonly int _parties;
    private readonly TimeSpan _timeout;
    private int _arrived;

    public DirectoryAssignmentBarrier(string directory, int parties, TimeSpan timeout)
    {
        _directory = directory;
        _parties = parties;
        _timeout = timeout;
    }

    public async Task ArriveAsync(Guid specialtyId, Guid selectedExpertId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _arrived, 1) != 0)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var readyFile = Path.Combine(_directory, $"ready-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(readyFile, $"{specialtyId}:{selectedExpertId}", cancellationToken);

        var deadline = DateTime.UtcNow + _timeout;
        while (CountReady() < _parties && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        if (CountReady() < _parties)
        {
            throw new TimeoutException(
                $"Assignment barrier timed out waiting for {_parties} parties in '{_directory}'.");
        }
    }

    private int CountReady()
    {
        try
        {
            return Directory.GetFiles(_directory, "ready-*.txt").Length;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }
}
