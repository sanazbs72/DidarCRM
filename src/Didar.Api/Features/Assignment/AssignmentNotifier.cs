namespace Didar.Api.Features.Assignment;


public interface IAssignmentNotifier
{
    void Notify();
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class AssignmentNotifier : IAssignmentNotifier
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A signal is already pending; coalesce.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}
