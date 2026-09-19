using System.Diagnostics;
using System.Runtime.CompilerServices;
using RemoteDesk;

// Own process/message loop only: no displayed UI, connection, clipboard or settings.
internal static class BackgroundFaultProbe
{
    internal static int Run(string output)
    {
        FatalExitGuard.Install();
        long firstObservedAt = 0;
        int observed = 0;
        bool markedObserved = false;
        void Observe(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            markedObserved = args.Observed;
            Interlocked.CompareExchange(ref firstObservedAt, Stopwatch.GetTimestamp(), 0);
            Interlocked.Increment(ref observed);
        }
        TaskScheduler.UnobservedTaskException += Observe;
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        var elapsed = Stopwatch.StartNew();
        int heartbeats = 0;
        bool healthyCompletion = false;
        WeakReference? abandoned = null;
        timer.Tick += (_, _) =>
        {
            heartbeats++;
            if (abandoned is null) abandoned = AbandonFaultedTask();
            if (Volatile.Read(ref observed) == 0) GC.Collect();
            long first = Volatile.Read(ref firstObservedAt);
            if (first != 0 && Stopwatch.GetElapsedTime(first) >= TimeSpan.FromSeconds(3))
            {
                healthyCompletion = true;
                context.ExitThread();
            }
            else if (elapsed.Elapsed > TimeSpan.FromSeconds(12)) context.ExitThread();
        };
        try
        {
            timer.Start();
            Application.Run(context);
        }
        finally
        {
            timer.Stop();
            TaskScheduler.UnobservedTaskException -= Observe;
        }
        bool passed = healthyCompletion && observed > 0 && markedObserved && heartbeats > 20;
        Program.Save(Path.Combine(output, "background-fault.json"), new
        {
            passed, observed, markedObserved, heartbeats, healthyCompletion,
            elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            scope = "Owned process with the actual Windows message loop and product exception handlers; GC of one faulted task; no user UI, clipboard or connections."
        });
        return passed ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonFaultedTask()
    {
        var source = new TaskCompletionSource<bool>();
        source.SetException(new IOException("owned synthetic background failure"));
        return new WeakReference(source.Task);
    }
}
