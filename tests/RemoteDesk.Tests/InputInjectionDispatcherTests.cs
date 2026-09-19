using System.Collections.Concurrent;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class InputInjectionDispatcherTests
{
    [Fact]
    public void InteractiveDesktopProbeAcceptsActiveDefaultInputDesktop()
    {
        var native = new FakeInputDesktopNativeApi();

        WindowsInteractiveDesktopAvailability result =
            WindowsInteractiveDesktopProbe.Inspect(native);

        Assert.True(result.IsAvailable);
        Assert.Contains("desktop=Default", result.Diagnostic);
        Assert.Equal(
            [FakeInputDesktopNativeApi.InputDesktopHandle],
            native.ClosedDesktopHandles.ToArray());
    }

    [Fact]
    public void InteractiveDesktopProbeRejectsSecureDesktop()
    {
        var native = new FakeInputDesktopNativeApi
        {
            InputDesktop = FakeInputDesktopNativeApi.UserObject(
                "Winlogon",
                isInput: true)
        };

        WindowsInteractiveDesktopAvailability result =
            WindowsInteractiveDesktopProbe.Inspect(native);

        Assert.False(result.IsAvailable);
        Assert.Contains("desktop=Winlogon", result.Diagnostic);
        Assert.Equal(
            [FakeInputDesktopNativeApi.InputDesktopHandle],
            native.ClosedDesktopHandles.ToArray());
    }

    [Fact]
    public void InteractiveDesktopProbeRejectsDisconnectedSessionWithoutOpen()
    {
        var native = new FakeInputDesktopNativeApi
        {
            Session = new InputDesktopSessionSnapshot(
                SessionId: 1,
                InputDesktopWtsState.Disconnected,
                SessionIdError: 0,
                WtsError: 0)
        };

        WindowsInteractiveDesktopAvailability result =
            WindowsInteractiveDesktopProbe.Inspect(native);

        Assert.False(result.IsAvailable);
        Assert.Equal(0, native.OpenInputDesktopCount);
        Assert.Empty(native.ClosedDesktopHandles);
    }

    [Fact]
    public void ApplyPasteAndReleaseUseOneDedicatedThreadInOrder()
    {
        var calls = new ConcurrentQueue<(string Name, int ThreadId)>();
        int callerThread = Environment.CurrentManagedThreadId;
        using var dispatcher = new InputInjectionDispatcher(
            new FakeInputDesktopNativeApi(),
            (command, bounds, size) =>
                calls.Enqueue(("apply", Environment.CurrentManagedThreadId)),
            () => calls.Enqueue(("paste", Environment.CurrentManagedThreadId)),
            command => calls.Enqueue(("release-key", Environment.CurrentManagedThreadId)),
            button => calls.Enqueue(("release-button", Environment.CurrentManagedThreadId)));

        dispatcher.Apply(
            RemoteInputCommand.MouseMove(2, 3),
            new Rectangle(0, 0, 10, 10),
            new Size(10, 10));
        dispatcher.SendPasteShortcut();
        dispatcher.TryReleaseKey(RemoteInputCommand.KeyDown((int)Keys.A));
        dispatcher.TryReleaseMouseButton(RemoteMouseButton.Left);

        (string Name, int ThreadId)[] snapshot = calls.ToArray();
        Assert.Equal(
            ["apply", "paste", "release-key", "release-button"],
            snapshot.Select(call => call.Name).ToArray());
        int workerThread = Assert.Single(
            snapshot.Select(call => call.ThreadId).Distinct());
        Assert.NotEqual(callerThread, workerThread);
    }

    [Fact]
    public void DelegateExceptionPropagatesToCallingThread()
    {
        var expected = new TestInputException("input failed");
        using var dispatcher = new InputInjectionDispatcher(
            new FakeInputDesktopNativeApi(),
            (command, bounds, size) => throw expected,
            () => { },
            command => { },
            button => { });

        TestInputException actual = Assert.Throws<TestInputException>(
            () => dispatcher.Apply(
                RemoteInputCommand.KeyDown((int)Keys.A),
                Rectangle.Empty,
                Size.Empty));

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExpectedFrameDesktopIsRecheckedOnWorkerWithoutReplayingInput(bool capturedSecure, bool actualSecure)
    {
        var native = new FakeInputDesktopNativeApi
        {
            InputDesktop = FakeInputDesktopNativeApi.UserObject(actualSecure ? "Winlogon" : "Default", isInput: true)
        };
        int calls = 0;
        using var dispatcher = new InputInjectionDispatcher(native, (_, _, _) => calls++, () => { }, _ => { }, _ => { });
        void Apply() => dispatcher.Apply(RemoteInputCommand.MouseMove(1, 1), new Rectangle(0, 0, 10, 10), new Size(10, 10), capturedSecure);
        if (capturedSecure == actualSecure)
        {
            Apply();
            Assert.Equal(1, calls);
        }
        else
        {
            Assert.Throws<SecureDesktopTargetException>(Apply);
            Assert.Equal(0, calls);
            Assert.Equal(0, native.SetThreadDesktopCount);
        }
    }

    [Fact]
    public void CursorFailureRecoversToActiveDefaultInputDesktop()
    {
        var native = new FakeInputDesktopNativeApi();
        int applyCount = 0;
        var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) =>
            {
                if (Interlocked.Increment(ref applyCount) == 1)
                {
                    throw new CursorPositionException(101, 202, 5);
                }
            },
            () => { },
            command => { },
            button => { });

        try
        {
            dispatcher.Apply(
                RemoteInputCommand.MouseMove(1, 1),
                new Rectangle(0, 0, 10, 10),
                new Size(10, 10));

            Assert.Equal(2, applyCount);
            Assert.Equal(1, native.OpenInputDesktopCount);
            Assert.Equal(1, native.SetThreadDesktopCount);
            Assert.Empty(native.ClosedDesktopHandles);
            Assert.Equal(
                Assert.Single(
                    native.RecoveryManagedThreadIds.Distinct()),
                native.SetThreadDesktopManagedThreadId);
        }
        finally
        {
            dispatcher.Dispose();
        }

        Assert.Equal(
            [FakeInputDesktopNativeApi.InputDesktopHandle],
            native.ClosedDesktopHandles.ToArray());
    }

    [Fact]
    public void ZeroSendInputFailureRecoversBeforeAnyMouseMove()
    {
        var native = new FakeInputDesktopNativeApi();
        int applyCount = 0;
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) =>
            {
                if (Interlocked.Increment(ref applyCount) == 1)
                {
                    throw new SendInputException(
                        sentCount: 0,
                        expectedCount: 1,
                        nativeError: 5);
                }
            },
            () => { },
            command => { },
            button => { });

        dispatcher.Apply(
            RemoteInputCommand.KeyDown((int)Keys.A),
            Rectangle.Empty,
            Size.Empty);

        Assert.Equal(2, applyCount);
        Assert.Equal(1, native.OpenInputDesktopCount);
        Assert.Equal(1, native.SetThreadDesktopCount);
    }

    [Fact]
    public void ZeroSendInputPasteFailureRecoversAndRetriesWholeBatch()
    {
        var native = new FakeInputDesktopNativeApi();
        int pasteCount = 0;
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) => { },
            () =>
            {
                if (Interlocked.Increment(ref pasteCount) == 1)
                {
                    throw new SendInputException(
                        sentCount: 0,
                        expectedCount: 4,
                        nativeError: 0);
                }
            },
            command => { },
            button => { });

        dispatcher.SendPasteShortcut();

        Assert.Equal(2, pasteCount);
        Assert.Equal(1, native.OpenInputDesktopCount);
        Assert.Equal(1, native.SetThreadDesktopCount);
    }

    [Fact]
    public void BestEffortReleasePreservesCompensationSemantics()
    {
        var native = new FakeInputDesktopNativeApi();
        int releaseCount = 0;
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) => { },
            () => { },
            command =>
            {
                releaseCount++;
                throw new SendInputException(
                    sentCount: 0,
                    expectedCount: 1,
                    nativeError: 5);
            },
            button => { });

        dispatcher.TryReleaseKey(
            RemoteInputCommand.KeyDown((int)Keys.A));

        Assert.Equal(1, releaseCount);
        Assert.Equal(0, native.OpenInputDesktopCount);
        Assert.Equal(0, native.SetThreadDesktopCount);
    }

    [Fact]
    public void StrictTeardownReleaseRecoversAndCountsSuccess()
    {
        var native = new FakeInputDesktopNativeApi();
        int releaseCount = 0;
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) => { },
            () => { },
            command =>
            {
                if (Interlocked.Increment(ref releaseCount) == 1)
                {
                    throw new SendInputException(
                        sentCount: 0,
                        expectedCount: 1,
                        nativeError: 5);
                }
            },
            button => { });
        var tracker =
            new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(
            RemoteInputCommand.KeyDown((int)Keys.A));

        RemoteHostServer.RemoteInputReleaseResult result =
            tracker.ReleaseAll(
                dispatcher.ReleaseKey,
                dispatcher.ReleaseMouseButton);

        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, result.ReleasedCount);
        Assert.Empty(result.Failures);
        Assert.Equal(2, releaseCount);
        Assert.Equal(1, native.OpenInputDesktopCount);
        Assert.Equal(1, native.SetThreadDesktopCount);
    }

    [Fact]
    public void StrictTeardownReleaseReportsFinalFailureContinuesAndIsIdempotent()
    {
        var native = new FakeInputDesktopNativeApi();
        var releaseAttempts = new ConcurrentQueue<int>();
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) => { },
            () => { },
            command =>
            {
                releaseAttempts.Enqueue(command.Data);
                if (command.Data == (int)Keys.A)
                {
                    throw new SendInputException(
                        sentCount: 0,
                        expectedCount: 1,
                        nativeError: 5);
                }
            },
            button => { },
            cursorRetryLimit: 2);
        var tracker =
            new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(
            RemoteInputCommand.KeyDown(
                (int)Keys.ControlKey));
        tracker.Observe(
            RemoteInputCommand.KeyDown((int)Keys.A));

        RemoteHostServer.RemoteInputReleaseResult result =
            tracker.ReleaseAll(
                dispatcher.ReleaseKey,
                dispatcher.ReleaseMouseButton);

        Assert.Equal(2, result.AttemptedCount);
        Assert.Equal(1, result.ReleasedCount);
        InvalidOperationException failure =
            Assert.IsType<InvalidOperationException>(
                Assert.Single(result.Failures));
        Assert.Contains(
            $"释放键盘按键 {(int)Keys.A} 失败",
            failure.Message);
        Assert.Equal(
            [
                (int)Keys.A,
                (int)Keys.A,
                (int)Keys.A,
                (int)Keys.ControlKey
            ],
            releaseAttempts.ToArray());
        Assert.Equal(1, native.OpenInputDesktopCount);
        Assert.Equal(1, native.SetThreadDesktopCount);
        Assert.Equal(0, tracker.PressedKeyCount);

        RemoteHostServer.RemoteInputReleaseResult repeated =
            tracker.ReleaseAll(
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException());
        Assert.Equal(0, repeated.AttemptedCount);
        Assert.Equal(0, repeated.ReleasedCount);
        Assert.Empty(repeated.Failures);
    }

    [Fact]
    public void PartialSendInputFailureIsNeverRetried()
    {
        var native = new FakeInputDesktopNativeApi();
        int applyCount = 0;
        var expected = new SendInputException(
            sentCount: 1,
            expectedCount: 2,
            nativeError: 5);
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) =>
            {
                applyCount++;
                throw expected;
            },
            () => { },
            command => { },
            button => { });

        SendInputException actual = Assert.Throws<SendInputException>(
            () => dispatcher.Apply(
                RemoteInputCommand.KeyDown((int)Keys.A),
                Rectangle.Empty,
                Size.Empty));

        Assert.Same(expected, actual);
        Assert.Equal(1, applyCount);
        Assert.Equal(0, native.OpenInputDesktopCount);
        Assert.Equal(0, native.SetThreadDesktopCount);
    }

    [Fact]
    public void SecureInputDesktopIsRejectedWithoutRetryOrSwitch()
    {
        var native = new FakeInputDesktopNativeApi
        {
            InputDesktop = FakeInputDesktopNativeApi.UserObject(
                "Winlogon",
                isInput: true)
        };
        int applyCount = 0;
        using var dispatcher = CreateAlwaysFailingDispatcher(
            native,
            () => ++applyCount);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => dispatcher.Apply(
                    RemoteInputCommand.MouseMove(1, 1),
                    new Rectangle(0, 0, 10, 10),
                    new Size(10, 10)));

        Assert.Equal(1, applyCount);
        Assert.Equal(1, native.OpenInputDesktopCount);
        Assert.Equal(0, native.SetThreadDesktopCount);
        Assert.Contains("Winlogon、UAC 或锁屏安全桌面", exception.Message);
        Assert.Contains("inputDesktop=Winlogon/UOI_IO=True", exception.Message);
        Assert.Contains(
            FakeInputDesktopNativeApi.InputDesktopHandle,
            native.ClosedDesktopHandles);
    }

    [Fact]
    public void DisconnectedWtsSessionIsRejectedBeforeOpeningDesktop()
    {
        var native = new FakeInputDesktopNativeApi
        {
            Session = new InputDesktopSessionSnapshot(
                SessionId: 9,
                InputDesktopWtsState.Disconnected,
                SessionIdError: 0,
                WtsError: 0)
        };
        int applyCount = 0;
        using var dispatcher = CreateAlwaysFailingDispatcher(
            native,
            () => ++applyCount);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => dispatcher.Apply(
                    RemoteInputCommand.MouseMove(1, 1),
                    new Rectangle(0, 0, 10, 10),
                    new Size(10, 10)));

        Assert.Equal(1, applyCount);
        Assert.Equal(0, native.OpenInputDesktopCount);
        Assert.Equal(0, native.SetThreadDesktopCount);
        Assert.Contains("WTS 会话不是 Active", exception.Message);
        Assert.Contains("session=9，WTS=Disconnected", exception.Message);
        Assert.Contains("threadId=71", exception.Message);
        Assert.Contains("initialError=5", exception.Message);
        Assert.Contains(
            "inputDesktop=not-opened/UOI_IO=unknown",
            exception.Message);
    }

    [Fact]
    public void SendInputRecoveryFailurePreservesImmediateNativeDiagnostic()
    {
        var native = new FakeInputDesktopNativeApi
        {
            Session = new InputDesktopSessionSnapshot(
                SessionId: 11,
                InputDesktopWtsState.Disconnected,
                SessionIdError: 0,
                WtsError: 0)
        };
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) =>
                throw new SendInputException(
                    sentCount: 0,
                    expectedCount: 1,
                    nativeError: 5),
            () => { },
            command => { },
            button => { });

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => dispatcher.Apply(
                    RemoteInputCommand.KeyDown((int)Keys.A),
                    Rectangle.Empty,
                    Size.Empty));

        Assert.Contains("API=SendInput", exception.Message);
        Assert.Contains("initialError=5", exception.Message);
        Assert.Contains("sent=0/1", exception.Message);
        Assert.Contains("session=11，WTS=Disconnected", exception.Message);
        Assert.Equal(0, native.OpenInputDesktopCount);
    }

    [Fact]
    public void CursorRetryIsBoundedAfterSuccessfulDesktopSwitch()
    {
        var native = new FakeInputDesktopNativeApi();
        int applyCount = 0;
        using var dispatcher = new InputInjectionDispatcher(
            native,
            (command, bounds, size) =>
            {
                int attempt = ++applyCount;
                throw new CursorPositionException(
                    101,
                    202,
                    nativeError: 4 + attempt);
            },
            () => { },
            command => { },
            button => { },
            cursorRetryLimit: 2);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => dispatcher.Apply(
                    RemoteInputCommand.MouseMove(1, 1),
                    new Rectangle(0, 0, 10, 10),
                    new Size(10, 10)));

        Assert.Equal(3, applyCount);
        Assert.Equal(1, native.SetThreadDesktopCount);
        Assert.Contains("2 次有界重试后停止", exception.Message);
        Assert.Contains("retryErrors=[6,7]", exception.Message);
        Assert.Contains("windowStation=WinSta0/UOI_IO=True", exception.Message);
        Assert.Contains("threadDesktop=WorkerDesktop/UOI_IO=False", exception.Message);
    }

    private static InputInjectionDispatcher CreateAlwaysFailingDispatcher(
        FakeInputDesktopNativeApi native,
        Action beforeFailure) =>
        new(
            native,
            (command, bounds, size) =>
            {
                beforeFailure();
                throw new CursorPositionException(101, 202, 5);
            },
            () => { },
            command => { },
            button => { });

    private sealed class FakeInputDesktopNativeApi : IInputDesktopNativeApi
    {
        public const nint InputDesktopHandle = 1234;

        public InputDesktopSessionSnapshot Session { get; set; } =
            new(
                SessionId: 1,
                InputDesktopWtsState.Active,
                SessionIdError: 0,
                WtsError: 0);

        public InputDesktopUserObjectSnapshot WindowStation { get; set; } =
            UserObject("WinSta0", isInput: true);

        public InputDesktopUserObjectSnapshot ThreadDesktop { get; set; } =
            UserObject("WorkerDesktop", isInput: false);

        public InputDesktopUserObjectSnapshot InputDesktop { get; set; } =
            UserObject("Default", isInput: true);

        public InputDesktopOpenResult OpenResult { get; set; } =
            new(InputDesktopHandle, Error: 0);

        public InputDesktopNativeCallResult SetThreadDesktopResult { get; set; } =
            new(Succeeded: true, Error: 0);

        public int OpenInputDesktopCount { get; private set; }

        public int SetThreadDesktopCount { get; private set; }

        public int SetThreadDesktopManagedThreadId { get; private set; }

        public ConcurrentBag<int> RecoveryManagedThreadIds { get; } = [];

        public ConcurrentQueue<nint> ClosedDesktopHandles { get; } = [];

        public uint GetCurrentThreadId()
        {
            RecoveryManagedThreadIds.Add(Environment.CurrentManagedThreadId);
            return 71;
        }

        public InputDesktopSessionSnapshot GetCurrentSession()
        {
            RecoveryManagedThreadIds.Add(Environment.CurrentManagedThreadId);
            return Session;
        }

        public InputDesktopUserObjectSnapshot GetProcessWindowStation()
        {
            RecoveryManagedThreadIds.Add(Environment.CurrentManagedThreadId);
            return WindowStation;
        }

        public InputDesktopUserObjectSnapshot GetThreadDesktop(uint threadId)
        {
            RecoveryManagedThreadIds.Add(Environment.CurrentManagedThreadId);
            return ThreadDesktop;
        }

        public InputDesktopOpenResult OpenInputDesktop()
        {
            RecoveryManagedThreadIds.Add(Environment.CurrentManagedThreadId);
            OpenInputDesktopCount++;
            return OpenResult;
        }

        public InputDesktopUserObjectSnapshot InspectUserObject(nint handle)
        {
            RecoveryManagedThreadIds.Add(Environment.CurrentManagedThreadId);
            return InputDesktop with { Handle = handle };
        }

        public InputDesktopNativeCallResult SetThreadDesktop(nint desktop)
        {
            SetThreadDesktopManagedThreadId = Environment.CurrentManagedThreadId;
            RecoveryManagedThreadIds.Add(SetThreadDesktopManagedThreadId);
            SetThreadDesktopCount++;
            return SetThreadDesktopResult;
        }

        public InputDesktopNativeCallResult CloseDesktop(nint desktop)
        {
            ClosedDesktopHandles.Enqueue(desktop);
            return new InputDesktopNativeCallResult(Succeeded: true, Error: 0);
        }

        public static InputDesktopUserObjectSnapshot UserObject(
            string name,
            bool isInput) =>
            new(
                Handle: 99,
                name,
                isInput,
                HandleError: 0,
                NameError: 0,
                InputError: 0);
    }

    private sealed class TestInputException(string message) :
        Exception(message);
}
