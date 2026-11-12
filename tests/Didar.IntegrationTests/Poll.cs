namespace Didar.IntegrationTests;

public static class Poll
{
    public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutMs = 10000, int intervalMs = 100)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(intervalMs);
        }

        throw new TimeoutException($"Condition was not met within {timeoutMs}ms.");
    }
}
