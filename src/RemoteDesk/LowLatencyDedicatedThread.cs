namespace RemoteDesk;

internal static class LowLatencyDedicatedThread
{
    internal static Task Start(
        string name,
        Action action,
        ThreadPriority priority = ThreadPriority.Normal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);

        var completion =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var worker = new Thread(
            () =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            })
        {
            IsBackground = true,
            Name = name,
            Priority = priority
        };
        worker.Start();
        return completion.Task;
    }
}
