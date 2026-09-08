using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteDesk;

// One GUI/host per interactive user session. The cross-integrity message can only show the UI.
internal sealed class WindowsAppInstance : IDisposable
{
    internal static readonly uint ActivateMessage = RegisterWindowMessage("RemoteDesk.ShowMainWindow.v1");
    private readonly Mutex mutex;
    public bool IsOwner { get; }

    public WindowsAppInstance()
    {
        mutex = new Mutex(false, $"Local\\RemoteDesk.Gui.{WindowsPersistentStartup.UserSid}.{Process.GetCurrentProcess().SessionId}");
        try { IsOwner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
    }

    public static void AllowActivation(IntPtr window)
    {
        if (ActivateMessage != 0) ChangeWindowMessageFilterEx(window, ActivateMessage, 1, IntPtr.Zero);
    }

    public static void ActivateExisting()
    {
        if (ActivateMessage != 0) PostMessage(new IntPtr(0xffff), ActivateMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (IsOwner) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
