using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesk;

internal enum InputDesktopWtsState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9
}

internal readonly record struct InputDesktopSessionSnapshot(
    uint? SessionId,
    InputDesktopWtsState? State,
    int SessionIdError,
    int WtsError);

internal readonly record struct InputDesktopUserObjectSnapshot(
    nint Handle,
    string? Name,
    bool? IsInput,
    int HandleError,
    int NameError,
    int InputError);

internal readonly record struct InputDesktopOpenResult(
    nint Handle,
    int Error);

internal readonly record struct InputDesktopNativeCallResult(
    bool Succeeded,
    int Error);

internal interface IInputDesktopNativeApi
{
    uint GetCurrentThreadId();

    InputDesktopSessionSnapshot GetCurrentSession();

    InputDesktopUserObjectSnapshot GetProcessWindowStation();

    InputDesktopUserObjectSnapshot GetThreadDesktop(uint threadId);

    InputDesktopOpenResult OpenInputDesktop();

    InputDesktopUserObjectSnapshot InspectUserObject(nint handle);

    InputDesktopNativeCallResult SetThreadDesktop(nint desktop);

    InputDesktopNativeCallResult CloseDesktop(nint desktop);
}

internal readonly record struct WindowsInteractiveDesktopAvailability(
    bool IsAvailable,
    string Diagnostic);

internal static class WindowsInteractiveDesktopProbe
{
    public static WindowsInteractiveDesktopAvailability InspectCurrent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsInteractiveDesktopAvailability(
                IsAvailable: true,
                "non-Windows platform");
        }

        return Inspect(WindowsInputDesktopNativeApi.Instance);
    }

    internal static WindowsInteractiveDesktopAvailability Inspect(
        IInputDesktopNativeApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        InputDesktopSessionSnapshot session =
            nativeApi.GetCurrentSession();
        if (session.State != InputDesktopWtsState.Active)
        {
            return new WindowsInteractiveDesktopAvailability(
                IsAvailable: false,
                $"WTS={session.State?.ToString() ?? "unknown"}," +
                $"sessionError={session.SessionIdError}," +
                $"wtsError={session.WtsError}");
        }

        InputDesktopOpenResult opened =
            nativeApi.OpenInputDesktop();
        if (opened.Handle == 0)
        {
            return new WindowsInteractiveDesktopAvailability(
                IsAvailable: false,
                $"OpenInputDesktopError={opened.Error}");
        }

        try
        {
            InputDesktopUserObjectSnapshot desktop =
                nativeApi.InspectUserObject(opened.Handle);
            bool available =
                string.Equals(
                    desktop.Name,
                    "Default",
                    StringComparison.OrdinalIgnoreCase) &&
                desktop.IsInput == true;
            return new WindowsInteractiveDesktopAvailability(
                available,
                $"desktop={desktop.Name ?? "unknown"}," +
                $"UOI_IO={desktop.IsInput?.ToString() ?? "unknown"}," +
                $"nameError={desktop.NameError}," +
                $"inputError={desktop.InputError}");
        }
        finally
        {
            nativeApi.CloseDesktop(opened.Handle);
        }
    }
}

/// <summary>
/// Serializes every native input operation for one authenticated host client on a
/// dedicated, non-async thread. A stable thread is important because a Windows
/// desktop is attached to a thread, not to an async operation or a logical task.
/// </summary>
internal sealed class InputInjectionDispatcher : IDisposable
{
    internal const int CursorRetryLimit = 2;

    private readonly IInputDesktopNativeApi _nativeApi;
    private readonly Action<RemoteInputCommand, Rectangle, Size> _applyInput;
    private readonly Action _sendPasteShortcut;
    private readonly Action<RemoteInputCommand> _releaseKey;
    private readonly Action<RemoteMouseButton> _releaseMouseButton;
    private readonly int _cursorRetryLimit;
    private readonly BlockingCollection<WorkItem> _workItems =
        new(new ConcurrentQueue<WorkItem>());
    private readonly ManualResetEventSlim _workerStarted = new(false);
    private readonly object _lifecycleLock = new();
    private readonly Thread _worker;

    private bool _disposed;
    private int _workerManagedThreadId;
    private nint _ownedInputDesktop;

    public InputInjectionDispatcher()
        : this(
            WindowsInputDesktopNativeApi.Instance,
            InputInjector.Apply,
            InputInjector.SendPasteShortcut,
            InputInjector.ReleaseKey,
            InputInjector.ReleaseMouseButton,
            CursorRetryLimit)
    {
    }

    internal InputInjectionDispatcher(
        IInputDesktopNativeApi nativeApi,
        Action<RemoteInputCommand, Rectangle, Size> applyInput,
        Action sendPasteShortcut,
        Action<RemoteInputCommand> releaseKey,
        Action<RemoteMouseButton> releaseMouseButton,
        int cursorRetryLimit = CursorRetryLimit)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        ArgumentNullException.ThrowIfNull(applyInput);
        ArgumentNullException.ThrowIfNull(sendPasteShortcut);
        ArgumentNullException.ThrowIfNull(releaseKey);
        ArgumentNullException.ThrowIfNull(releaseMouseButton);
        if (cursorRetryLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorRetryLimit));
        }

        _nativeApi = nativeApi;
        _applyInput = applyInput;
        _sendPasteShortcut = sendPasteShortcut;
        _releaseKey = releaseKey;
        _releaseMouseButton = releaseMouseButton;
        _cursorRetryLimit = cursorRetryLimit;
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "RemoteDesk host input",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
        _workerStarted.Wait();
    }

    public void Apply(
        RemoteInputCommand command,
        Rectangle captureBounds,
        Size frameSize) =>
        Dispatch(() => _applyInput(command, captureBounds, frameSize));

    public void SendPasteShortcut() =>
        Dispatch(_sendPasteShortcut);

    public void ReleaseKey(RemoteInputCommand pressedCommand) =>
        Dispatch(() => _releaseKey(pressedCommand));

    public void ReleaseMouseButton(RemoteMouseButton button) =>
        Dispatch(() => _releaseMouseButton(button));

    public void TryReleaseKey(RemoteInputCommand pressedCommand) =>
        Dispatch(
            () => TryRelease(
                () => _releaseKey(pressedCommand)));

    public void TryReleaseMouseButton(RemoteMouseButton button) =>
        Dispatch(
            () => TryRelease(
                () => _releaseMouseButton(button)));

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            if (Environment.CurrentManagedThreadId == _workerManagedThreadId)
            {
                throw new InvalidOperationException(
                    "不能从 RemoteDesk 原生输入线程释放输入调度器。");
            }

            _disposed = true;
            _workItems.CompleteAdding();
        }

        _worker.Join();
        nint ownedDesktop = _ownedInputDesktop;
        _ownedInputDesktop = 0;
        if (ownedDesktop != 0)
        {
            // Close only after the worker exits. CloseDesktop must not be called
            // while any thread is still assigned to this handle.
            _nativeApi.CloseDesktop(ownedDesktop);
        }

        _workerStarted.Dispose();
        _workItems.Dispose();
    }

    private void Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Environment.CurrentManagedThreadId == _workerManagedThreadId)
        {
            ExecuteWithInputDesktopRecovery(action);
            return;
        }

        var item = new WorkItem(action);
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _workItems.Add(item);
        }

        item.Completion.Task.GetAwaiter().GetResult();
    }

    private static void TryRelease(Action release)
    {
        try
        {
            release();
        }
        catch (InvalidOperationException)
        {
            // Compensation paths must remain best-effort. Session teardown
            // uses the strict Release* methods so a native failure reaches
            // desktop recovery and, if still unsuccessful, its failure
            // accounting.
        }
    }

    private void Run()
    {
        _workerManagedThreadId = Environment.CurrentManagedThreadId;
        _workerStarted.Set();

        foreach (WorkItem item in _workItems.GetConsumingEnumerable())
        {
            try
            {
                ExecuteWithInputDesktopRecovery(item.Action);
                item.Completion.SetResult(null);
            }
            catch (Exception ex)
            {
                item.Completion.SetException(ex);
            }
        }
    }

    private void ExecuteWithInputDesktopRecovery(Action action)
    {
        try
        {
            action();
            return;
        }
        catch (NativeInputInjectionException initialFailure) when (
            initialFailure.CanRetry)
        {
            RecoverInputDesktopAndRetry(action, initialFailure);
        }
    }

    private void RecoverInputDesktopAndRetry(
        Action action,
        NativeInputInjectionException initialFailure)
    {
        uint nativeThreadId = _nativeApi.GetCurrentThreadId();
        InputDesktopSessionSnapshot session =
            _nativeApi.GetCurrentSession();
        InputDesktopUserObjectSnapshot windowStation =
            _nativeApi.GetProcessWindowStation();
        InputDesktopUserObjectSnapshot threadDesktop =
            _nativeApi.GetThreadDesktop(nativeThreadId);

        if (session.State != InputDesktopWtsState.Active)
        {
            throw CreateRecoveryException(
                "当前 WTS 会话不是 Active，已拒绝向锁屏或断开的会话注入输入",
                initialFailure,
                nativeThreadId,
                session,
                windowStation,
                threadDesktop,
                inputDesktop: null,
                openError: null,
                setThreadDesktopError: null,
                retryErrors: []);
        }

        if (!string.Equals(
                windowStation.Name,
                "WinSta0",
                StringComparison.OrdinalIgnoreCase))
        {
            throw CreateRecoveryException(
                "进程未连接到交互窗口站 WinSta0，已拒绝原生输入恢复",
                initialFailure,
                nativeThreadId,
                session,
                windowStation,
                threadDesktop,
                inputDesktop: null,
                openError: null,
                setThreadDesktopError: null,
                retryErrors: []);
        }

        InputDesktopOpenResult opened = _nativeApi.OpenInputDesktop();
        if (opened.Handle == 0)
        {
            throw CreateRecoveryException(
                "OpenInputDesktop 失败，已拒绝原生输入恢复",
                initialFailure,
                nativeThreadId,
                session,
                windowStation,
                threadDesktop,
                inputDesktop: null,
                openError: opened.Error,
                setThreadDesktopError: null,
                retryErrors: []);
        }

        nint inputDesktopHandle = opened.Handle;
        bool adoptedInputDesktop = false;
        try
        {
            InputDesktopUserObjectSnapshot inputDesktop =
                _nativeApi.InspectUserObject(inputDesktopHandle);
            if (!string.Equals(
                    inputDesktop.Name,
                    "Default",
                    StringComparison.OrdinalIgnoreCase) ||
                inputDesktop.IsInput != true)
            {
                throw CreateRecoveryException(
                    "输入桌面不是正在接收输入的 Default；它可能是 Winlogon、UAC 或锁屏安全桌面，已拒绝注入",
                    initialFailure,
                    nativeThreadId,
                    session,
                    windowStation,
                    threadDesktop,
                    inputDesktop,
                    openError: opened.Error,
                    setThreadDesktopError: null,
                    retryErrors: []);
            }

            InputDesktopNativeCallResult setDesktop =
                _nativeApi.SetThreadDesktop(inputDesktopHandle);
            if (!setDesktop.Succeeded)
            {
                throw CreateRecoveryException(
                    "SetThreadDesktop(Default) 失败，已拒绝原生输入恢复",
                    initialFailure,
                    nativeThreadId,
                    session,
                    windowStation,
                    threadDesktop,
                    inputDesktop,
                    openError: opened.Error,
                    setThreadDesktopError: setDesktop.Error,
                    retryErrors: []);
            }

            nint previousOwnedDesktop = _ownedInputDesktop;
            _ownedInputDesktop = inputDesktopHandle;
            adoptedInputDesktop = true;
            if (previousOwnedDesktop != 0 &&
                previousOwnedDesktop != inputDesktopHandle)
            {
                // The worker is now assigned to the new handle, so an older
                // handle owned by this dispatcher is no longer in use.
                _nativeApi.CloseDesktop(previousOwnedDesktop);
            }

            var retryErrors = new List<int>(_cursorRetryLimit);
            NativeInputInjectionException lastFailure = initialFailure;
            for (int attempt = 0; attempt < _cursorRetryLimit; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (NativeInputInjectionException retryFailure) when (
                    retryFailure.CanRetry)
                {
                    lastFailure = retryFailure;
                    retryErrors.Add(retryFailure.NativeError);
                    if (attempt + 1 < _cursorRetryLimit)
                    {
                        Thread.Yield();
                    }
                }
            }

            throw CreateRecoveryException(
                $"SetThreadDesktop(Default) 后 {lastFailure.ApiName} 仍失败，已在 {_cursorRetryLimit} 次有界重试后停止",
                initialFailure,
                nativeThreadId,
                session,
                windowStation,
                threadDesktop,
                inputDesktop,
                openError: opened.Error,
                setThreadDesktopError: 0,
                retryErrors,
                lastFailure);
        }
        finally
        {
            if (!adoptedInputDesktop)
            {
                _nativeApi.CloseDesktop(inputDesktopHandle);
            }
        }
    }

    private static InvalidOperationException CreateRecoveryException(
        string reason,
        NativeInputInjectionException initialFailure,
        uint nativeThreadId,
        InputDesktopSessionSnapshot session,
        InputDesktopUserObjectSnapshot windowStation,
        InputDesktopUserObjectSnapshot threadDesktop,
        InputDesktopUserObjectSnapshot? inputDesktop,
        int? openError,
        int? setThreadDesktopError,
        IReadOnlyList<int> retryErrors,
        Exception? innerException = null)
    {
        var detail = new StringBuilder(reason);
        detail.Append("；API=")
            .Append(initialFailure.ApiName)
            .Append("，initialError=")
            .Append(initialFailure.NativeError)
            .Append('，')
            .Append(initialFailure.DiagnosticContext)
            .Append("；threadId=")
            .Append(nativeThreadId)
            .Append("；session=")
            .Append(session.SessionId?.ToString() ?? "unknown")
            .Append("，WTS=")
            .Append(session.State?.ToString() ?? "unknown")
            .Append("，sessionIdError=")
            .Append(session.SessionIdError)
            .Append("，wtsError=")
            .Append(session.WtsError)
            .Append("；windowStation=")
            .Append(FormatUserObject(windowStation))
            .Append("；threadDesktop=")
            .Append(FormatUserObject(threadDesktop));

        detail.Append("；inputDesktop=")
            .Append(
                inputDesktop is null
                    ? "not-opened/UOI_IO=unknown"
                    : FormatUserObject(inputDesktop.Value));

        if (openError is not null)
        {
            detail.Append("；OpenInputDesktopError=")
                .Append(openError.Value);
        }

        if (setThreadDesktopError is not null)
        {
            detail.Append("；SetThreadDesktopError=")
                .Append(setThreadDesktopError.Value);
        }

        if (retryErrors.Count > 0)
        {
            detail.Append("；retryErrors=[")
                .Append(string.Join(',', retryErrors))
                .Append(']');
        }

        return new InvalidOperationException(
            detail.ToString(),
            innerException ?? initialFailure);
    }

    private static string FormatUserObject(
        InputDesktopUserObjectSnapshot value) =>
        $"{value.Name ?? "unknown"}/UOI_IO={value.IsInput?.ToString() ?? "unknown"}" +
        $"(handleError={value.HandleError},nameError={value.NameError},ioError={value.InputError})";

    private sealed record WorkItem(Action Action)
    {
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class WindowsInputDesktopNativeApi : IInputDesktopNativeApi
{
    private const int UoiName = 2;
    private const int UoiIo = 6;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopWriteObjects = 0x0080;
    private const int WtsConnectState = 8;
    private const int ErrorInvalidData = 13;

    public static WindowsInputDesktopNativeApi Instance { get; } = new();

    private WindowsInputDesktopNativeApi()
    {
    }

    public uint GetCurrentThreadId() => NativeGetCurrentThreadId();

    public InputDesktopSessionSnapshot GetCurrentSession()
    {
        if (!ProcessIdToSessionId(GetCurrentProcessId(), out uint sessionId))
        {
            int error = Marshal.GetLastWin32Error();
            return new InputDesktopSessionSnapshot(
                SessionId: null,
                State: null,
                SessionIdError: error,
                WtsError: 0);
        }

        if (!WTSQuerySessionInformation(
                0,
                sessionId,
                WtsConnectState,
                out nint buffer,
                out int bytesReturned))
        {
            int error = Marshal.GetLastWin32Error();
            return new InputDesktopSessionSnapshot(
                sessionId,
                State: null,
                SessionIdError: 0,
                WtsError: error);
        }

        try
        {
            if (buffer == 0 || bytesReturned < sizeof(int))
            {
                return new InputDesktopSessionSnapshot(
                    sessionId,
                    State: null,
                    SessionIdError: 0,
                    WtsError: ErrorInvalidData);
            }

            return new InputDesktopSessionSnapshot(
                sessionId,
                (InputDesktopWtsState)Marshal.ReadInt32(buffer),
                SessionIdError: 0,
                WtsError: 0);
        }
        finally
        {
            if (buffer != 0)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    public InputDesktopUserObjectSnapshot GetProcessWindowStation()
    {
        nint handle = NativeGetProcessWindowStation();
        if (handle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            return new InputDesktopUserObjectSnapshot(
                0,
                Name: null,
                IsInput: null,
                HandleError: error,
                NameError: 0,
                InputError: 0);
        }

        return InspectUserObject(handle);
    }

    public InputDesktopUserObjectSnapshot GetThreadDesktop(uint threadId)
    {
        nint handle = NativeGetThreadDesktop(threadId);
        if (handle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            return new InputDesktopUserObjectSnapshot(
                0,
                Name: null,
                IsInput: null,
                HandleError: error,
                NameError: 0,
                InputError: 0);
        }

        return InspectUserObject(handle);
    }

    public InputDesktopOpenResult OpenInputDesktop()
    {
        nint handle = NativeOpenInputDesktop(
            0,
            inherit: false,
            DesktopReadObjects |
            DesktopWriteObjects);
        int error = handle == 0
            ? Marshal.GetLastWin32Error()
            : 0;
        return new InputDesktopOpenResult(handle, error);
    }

    public InputDesktopUserObjectSnapshot InspectUserObject(nint handle)
    {
        (string? name, int nameError) = ReadUserObjectName(handle);
        (bool? isInput, int inputError) = ReadUserObjectInputFlag(handle);
        return new InputDesktopUserObjectSnapshot(
            handle,
            name,
            isInput,
            HandleError: 0,
            nameError,
            inputError);
    }

    public InputDesktopNativeCallResult SetThreadDesktop(nint desktop)
    {
        bool succeeded = NativeSetThreadDesktop(desktop);
        int error = succeeded ? 0 : Marshal.GetLastWin32Error();
        return new InputDesktopNativeCallResult(succeeded, error);
    }

    public InputDesktopNativeCallResult CloseDesktop(nint desktop)
    {
        bool succeeded = NativeCloseDesktop(desktop);
        int error = succeeded ? 0 : Marshal.GetLastWin32Error();
        return new InputDesktopNativeCallResult(succeeded, error);
    }

    private static (string? Name, int Error) ReadUserObjectName(nint handle)
    {
        NativeGetUserObjectInformation(
            handle,
            UoiName,
            0,
            0,
            out uint requiredBytes);
        if (requiredBytes == 0)
        {
            return (null, Marshal.GetLastWin32Error());
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!NativeGetUserObjectInformation(
                    handle,
                    UoiName,
                    buffer,
                    requiredBytes,
                    out _))
            {
                int error = Marshal.GetLastWin32Error();
                return (null, error);
            }

            return (Marshal.PtrToStringUni(buffer), 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (bool? IsInput, int Error) ReadUserObjectInputFlag(
        nint handle)
    {
        nint buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!NativeGetUserObjectInformation(
                    handle,
                    UoiIo,
                    buffer,
                    sizeof(int),
                    out _))
            {
                int error = Marshal.GetLastWin32Error();
                return (null, error);
            }

            return (Marshal.ReadInt32(buffer) != 0, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint NativeGetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    [DllImport("user32.dll", EntryPoint = "GetProcessWindowStation", SetLastError = true)]
    private static extern nint NativeGetProcessWindowStation();

    [DllImport("user32.dll", EntryPoint = "GetThreadDesktop", SetLastError = true)]
    private static extern nint NativeGetThreadDesktop(uint threadId);

    [DllImport("user32.dll", EntryPoint = "OpenInputDesktop", SetLastError = true)]
    private static extern nint NativeOpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", EntryPoint = "SetThreadDesktop", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetThreadDesktop(nint desktop);

    [DllImport("user32.dll", EntryPoint = "CloseDesktop", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCloseDesktop(nint desktop);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetUserObjectInformation(
        nint handle,
        int index,
        nint information,
        uint length,
        out uint requiredLength);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        nint server,
        uint sessionId,
        int infoClass,
        out nint buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}
