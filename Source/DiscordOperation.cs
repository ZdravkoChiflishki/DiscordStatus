namespace DiscordStatus;

// Shared boundary for Discord work launched by void game callbacks.
internal static class DiscordOperation
{
    internal static async Task<bool> RunAsync(Func<Task> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Exception messages/URLs can contain webhook tokens. Log only the type.
            DSLog.Log(2, $"Discord {operation} failed ({ex.GetType().Name}).");
            return false;
        }
    }

    internal static System.Timers.Timer StartTimer(TimeSpan interval, Func<Task> action)
    {
        var timer = new System.Timers.Timer(interval.TotalMilliseconds);
        // Keep the void event handler synchronous; the returned Task contains its failures.
        timer.Elapsed += (_, _) => { _ = RunAsync(action, "periodic status update"); };
        timer.Start();
        return timer;
    }
}
