using System.Runtime.InteropServices;

namespace RemoteDesk;

internal readonly record struct NativeKeyboardInput(
    ushort VirtualKey,
    ushort ScanCode,
    uint Flags);

internal abstract class NativeInputInjectionException : InvalidOperationException
{
    protected NativeInputInjectionException(
        string message,
        string apiName,
        int nativeError,
        bool canRetry,
        string diagnosticContext)
        : base(message)
    {
        ApiName = apiName;
        NativeError = nativeError;
        CanRetry = canRetry;
        DiagnosticContext = diagnosticContext;
    }

    public string ApiName { get; }

    public int NativeError { get; }

    public bool CanRetry { get; }

    public string DiagnosticContext { get; }
}

internal sealed class CursorPositionException : NativeInputInjectionException
{
    public CursorPositionException(
        int x,
        int y,
        int nativeError,
        Exception? fallbackFailure = null)
        : base(
            nativeError == 0
                ? $"移动鼠标失败：SetCursorPos({x}, {y}) 未提供扩展错误，绝对输入回退也失败。"
                : $"移动鼠标失败：SetCursorPos({x}, {y})，Win32 错误 {nativeError}；绝对输入回退也失败。",
            apiName: "SetCursorPos",
            nativeError,
            canRetry: true,
            diagnosticContext: $"coordinate=({x},{y})")
    {
        X = x;
        Y = y;
        FallbackFailure = fallbackFailure;
    }

    public int X { get; }

    public int Y { get; }

    public Exception? FallbackFailure { get; }

}

internal sealed class SendInputException : NativeInputInjectionException
{
    public SendInputException(
        uint sentCount,
        uint expectedCount,
        int nativeError)
        : base(
            nativeError == 0
                ? $"发送输入失败：{sentCount}/{expectedCount}，未提供扩展错误。"
                : $"发送输入失败：{sentCount}/{expectedCount}，Win32 错误 {nativeError}。",
            apiName: "SendInput",
            nativeError,
            // SendInput returns the number of events it inserted. Retrying a
            // partial sequence could duplicate modifier/button transitions;
            // only an all-or-nothing zero result is safe to replay.
            canRetry: sentCount == 0,
            diagnosticContext: $"sent={sentCount}/{expectedCount}")
    {
        SentCount = sentCount;
        ExpectedCount = expectedCount;
    }

    public uint SentCount { get; }

    public uint ExpectedCount { get; }
}

internal static class InputInjector
{
    // Tag every SendInput event created by RemoteDesk. The viewer's global
    // keyboard hook uses this value to suppress only our own same-machine
    // feedback. Events injected by accessibility tools or another remote
    // control product must remain indistinguishable from physical input.
    internal static nuint InjectedInputMarker => 0x52444B31u;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const uint MouseEventAbsolute = 0x8000;

    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventScanCode = 0x0008;
    private const int MaxVirtualKey = 0xFE;

    public static void Apply(RemoteInputCommand command, Rectangle captureBounds, Size frameSize)
    {
        ValidateCommand(command, frameSize);

        switch (command.Kind)
        {
            case RemoteInputKind.MouseMove:
                MoveMouse(
                    GetPointerScreenPosition(
                        command,
                        captureBounds,
                        frameSize));
                break;
            case RemoteInputKind.MouseDown:
                Point downPosition =
                    GetPointerScreenPosition(
                        command,
                        captureBounds,
                        frameSize);
                WindowsInputIntegrityGuard
                    .ThrowIfPointerTargetRequiresElevation(
                        downPosition.X,
                        downPosition.Y);
                MoveMouse(downPosition);
                SendMouseButton(command.Button, isDown: true);
                break;
            case RemoteInputKind.MouseUp:
                MoveMouse(
                    GetPointerScreenPosition(
                        command,
                        captureBounds,
                        frameSize));
                SendMouseButton(command.Button, isDown: false);
                break;
            case RemoteInputKind.MouseWheel:
                Point wheelPosition =
                    GetPointerScreenPosition(
                        command,
                        captureBounds,
                        frameSize);
                WindowsInputIntegrityGuard
                    .ThrowIfPointerTargetRequiresElevation(
                        wheelPosition.X,
                        wheelPosition.Y);
                MoveMouse(wheelPosition);
                SendMouse(MouseEventWheel, command.Data);
                break;
            case RemoteInputKind.KeyDown:
                WindowsInputIntegrityGuard
                    .ThrowIfForegroundTargetRequiresElevation();
                SendKeyboard(command);
                break;
            case RemoteInputKind.KeyUp:
                SendKeyboard(command);
                break;
            case RemoteInputKind.TextInput:
                WindowsInputIntegrityGuard
                    .ThrowIfForegroundTargetRequiresElevation();
                SendUnicodeText(command.Data);
                break;
        }
    }

    internal static void ValidateCommand(RemoteInputCommand command)
    {
        ValidateCommand(command, frameSize: null);
    }

    internal static void SendPasteShortcut()
    {
        WindowsInputIntegrityGuard
            .ThrowIfForegroundTargetRequiresElevation();
        // Submit the shortcut as one native batch. Besides reducing four
        // user32 transitions, a zero-result failure now proves that nothing
        // was injected, so the dedicated input worker can safely repair its
        // desktop and replay the whole shortcut once.
        INPUT[] inputs =
        [
            CreateKeyboardInput(
                RemoteInputCommand.KeyDown((int)Keys.ControlKey)),
            CreateKeyboardInput(
                RemoteInputCommand.KeyDown((int)Keys.V)),
            CreateKeyboardInput(
                RemoteInputCommand.KeyUp((int)Keys.V)),
            CreateKeyboardInput(
                RemoteInputCommand.KeyUp((int)Keys.ControlKey))
        ];
        ExecutePasteShortcutBatch(
            () => SendNativeInputs(inputs),
            () => TrySendKeyboard((ushort)Keys.V, keyUp: true),
            () => TrySendKeyboard(
                (ushort)Keys.ControlKey,
                keyUp: true));
    }

    internal static void ExecutePasteShortcutBatch(
        Action sendBatch,
        Action releaseV,
        Action releaseControl)
    {
        ArgumentNullException.ThrowIfNull(sendBatch);
        ArgumentNullException.ThrowIfNull(releaseV);
        ArgumentNullException.ThrowIfNull(releaseControl);
        try
        {
            sendBatch();
        }
        catch (SendInputException failure) when (
            failure.SentCount > 0)
        {
            releaseV();
            releaseControl();
            throw;
        }
    }

    internal static void TryReleaseKey(
        RemoteInputCommand pressedCommand)
    {
        try
        {
            ReleaseKey(pressedCommand);
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static void ReleaseKey(
        RemoteInputCommand pressedCommand)
    {
        if (pressedCommand.Kind !=
                RemoteInputKind.KeyDown ||
            pressedCommand.Data is <= 0 or
                > MaxVirtualKey)
        {
            return;
        }

        SendKeyboard(
            RemoteInputCommand.KeyUp(
                pressedCommand.Data,
                pressedCommand.X,
                (RemoteKeyboardFlags)
                    pressedCommand.Y));
    }

    internal static void TryReleaseMouseButton(RemoteMouseButton button)
    {
        try
        {
            ReleaseMouseButton(button);
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static void ReleaseMouseButton(RemoteMouseButton button)
    {
        if (button is not (RemoteMouseButton.Left or RemoteMouseButton.Right or RemoteMouseButton.Middle))
        {
            return;
        }

        SendMouseButton(button, isDown: false);
    }

    internal static void ValidateCommand(RemoteInputCommand command, Size? frameSize)
    {
        switch (command.Kind)
        {
            case RemoteInputKind.MouseMove:
            case RemoteInputKind.MouseWheel:
                ValidatePointerCoordinate(command, frameSize);
                return;
            case RemoteInputKind.MouseDown:
            case RemoteInputKind.MouseUp:
                if (command.Button is not (RemoteMouseButton.Left or RemoteMouseButton.Right or RemoteMouseButton.Middle))
                {
                    throw new InvalidDataException("鼠标按键命令缺少有效按钮。");
                }

                ValidatePointerCoordinate(command, frameSize);
                return;
            case RemoteInputKind.KeyDown:
            case RemoteInputKind.KeyUp:
                if (command.Data is <= 0 or > MaxVirtualKey)
                {
                    throw new InvalidDataException("键盘虚拟键值异常。");
                }

                var keyboardFlags =
                    (RemoteKeyboardFlags)command.Y;
                const RemoteKeyboardFlags validFlags =
                    RemoteKeyboardFlags.HasScanCode |
                    RemoteKeyboardFlags.Extended;
                if (command.X is < 0 or > byte.MaxValue ||
                    (keyboardFlags & ~validFlags) != 0 ||
                    (keyboardFlags.HasFlag(
                            RemoteKeyboardFlags
                                .HasScanCode)
                        ? command.X == 0
                        : command.X != 0 ||
                          keyboardFlags.HasFlag(
                              RemoteKeyboardFlags
                                  .Extended)))
                {
                    throw new InvalidDataException(
                        "键盘扫描码或扩展标志异常。");
                }

                return;
            case RemoteInputKind.TextInput:
                if (!IsSupportedTextCodePoint(command.Data))
                {
                    throw new InvalidDataException("文本输入码点异常。");
                }

                return;
            case RemoteInputKind.PinchZoom:
                throw new InvalidDataException("Windows 被控端不支持双指缩放输入。");
            default:
                throw new InvalidDataException("输入命令类型异常。");
        }
    }

    private static void ValidatePointerCoordinate(RemoteInputCommand command, Size? frameSize)
    {
        if (frameSize is null)
        {
            return;
        }

        Size size = frameSize.Value;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidDataException("输入坐标缺少有效画面尺寸。");
        }

        if (command.X < 0 ||
            command.Y < 0 ||
            command.X >= size.Width ||
            command.Y >= size.Height)
        {
            throw new InvalidDataException("输入坐标超出远程画面范围。");
        }
    }

    internal static Point GetPointerScreenPosition(
        RemoteInputCommand command,
        Rectangle captureBounds,
        Size frameSize)
    {
        int x = captureBounds.Left + ScaleCoordinate(command.X, frameSize.Width, captureBounds.Width);
        int y = captureBounds.Top + ScaleCoordinate(command.Y, frameSize.Height, captureBounds.Height);
        return new Point(x, y);
    }

    private static void MoveMouse(Point position)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetCursorPos(position.X, position.Y))
        {
            return;
        }

        // SetCursorPos can return false with no extended error while another
        // desktop or a remote-control hook is transitioning. Absolute
        // SendInput uses an independent user32 path and is safe to retry when
        // it inserts zero events.
        int setCursorError = Marshal.GetLastPInvokeError();
        try
        {
            SendAbsoluteMouseMove(position.X, position.Y);
        }
        catch (SendInputException fallbackFailure)
        {
            throw new CursorPositionException(
                position.X,
                position.Y,
                setCursorError,
                fallbackFailure);
        }
    }

    private static void SendAbsoluteMouseMove(
        int screenX,
        int screenY)
    {
        Rectangle virtualDesktop =
            SystemInformation.VirtualScreen;
        int normalizedX = NormalizeAbsoluteCoordinate(
            screenX,
            virtualDesktop.Left,
            virtualDesktop.Width);
        int normalizedY = NormalizeAbsoluteCoordinate(
            screenY,
            virtualDesktop.Top,
            virtualDesktop.Height);
        SendMouse(
            MouseEventMove |
                MouseEventVirtualDesk |
                MouseEventAbsolute,
            mouseData: 0,
            normalizedX,
            normalizedY);
    }

    internal static int NormalizeAbsoluteCoordinate(
        int coordinate,
        int origin,
        int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        long relative = Math.Clamp(
            (long)coordinate - origin,
            0,
            length - 1L);
        return (int)Math.Round(
            relative * 65535d / (length - 1),
            MidpointRounding.AwayFromZero);
    }

    private static int ScaleCoordinate(int coordinate, int sourceLength, int targetLength)
    {
        if (sourceLength <= 1 || targetLength <= 1)
        {
            return 0;
        }

        int clamped = Math.Clamp(coordinate, 0, sourceLength - 1);
        return Math.Clamp((int)Math.Round(clamped * (targetLength - 1) / (double)(sourceLength - 1)), 0, targetLength - 1);
    }

    private static void SendMouseButton(RemoteMouseButton button, bool isDown)
    {
        uint flags = button switch
        {
            RemoteMouseButton.Left => isDown ? MouseEventLeftDown : MouseEventLeftUp,
            RemoteMouseButton.Right => isDown ? MouseEventRightDown : MouseEventRightUp,
            RemoteMouseButton.Middle => isDown ? MouseEventMiddleDown : MouseEventMiddleUp,
            _ => 0
        };

        if (flags != 0)
        {
            SendMouse(flags, 0);
        }
    }

    private static void SendMouse(
        uint flags,
        int mouseData,
        int dx = 0,
        int dy = 0)
    {
        INPUT[] inputs =
        [
            new()
            {
                type = InputMouse,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        mouseData = mouseData,
                        dwFlags = flags,
                        dwExtraInfo =
                            unchecked((nint)InjectedInputMarker)
                    }
                }
            }
        ];

        SendNativeInputs(inputs);
    }

    private static void SendKeyboard(ushort virtualKey, bool keyUp)
    {
        SendKeyboard(
            keyUp
                ? RemoteInputCommand.KeyUp(virtualKey)
                : RemoteInputCommand.KeyDown(virtualKey));
    }

    private static void SendKeyboard(
        RemoteInputCommand command)
    {
        INPUT[] inputs =
        [
            CreateKeyboardInput(command)
        ];

        SendNativeInputs(inputs);
    }

    private static INPUT CreateKeyboardInput(
        RemoteInputCommand command)
    {
        NativeKeyboardInput keyboard =
            CreateNativeKeyboardInput(command);
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = keyboard.VirtualKey,
                    wScan = keyboard.ScanCode,
                    dwFlags = keyboard.Flags,
                    dwExtraInfo =
                        unchecked((nint)InjectedInputMarker)
                }
            }
        };
    }

    internal static NativeKeyboardInput
        CreateNativeKeyboardInput(
            RemoteInputCommand command)
    {
        if (command.Kind is not (
                RemoteInputKind.KeyDown or
                RemoteInputKind.KeyUp))
        {
            throw new ArgumentException(
                "命令不是键盘按键事件。",
                nameof(command));
        }

        ValidateCommand(command);
        bool keyUp =
            command.Kind == RemoteInputKind.KeyUp;
        var remoteFlags =
            (RemoteKeyboardFlags)command.Y;
        if (remoteFlags.HasFlag(
                RemoteKeyboardFlags.HasScanCode))
        {
            uint nativeFlags =
                KeyEventScanCode;
            if (remoteFlags.HasFlag(
                    RemoteKeyboardFlags.Extended))
            {
                nativeFlags |= KeyEventExtended;
            }

            if (keyUp)
            {
                nativeFlags |= KeyEventKeyUp;
            }

            return new NativeKeyboardInput(
                VirtualKey: 0,
                ScanCode: (ushort)command.X,
                nativeFlags);
        }

        return new NativeKeyboardInput(
            (ushort)command.Data,
            ScanCode: 0,
            GetKeyboardFlags(
                command.Data,
                keyUp));
    }

    private static void TrySendKeyboard(ushort virtualKey, bool keyUp)
    {
        try
        {
            SendKeyboard(virtualKey, keyUp);
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static uint GetKeyboardFlags(int virtualKey, bool keyUp)
    {
        uint flags = IsExtendedVirtualKey(virtualKey) ? KeyEventExtended : 0;
        if (keyUp)
        {
            flags |= KeyEventKeyUp;
        }

        return flags;
    }

    private static bool IsExtendedVirtualKey(int virtualKey)
    {
        return (Keys)virtualKey switch
        {
            Keys.Insert or
            Keys.Delete or
            Keys.Home or
            Keys.End or
            Keys.PageUp or
            Keys.PageDown or
            Keys.LWin or
            Keys.RWin or
            Keys.Up or
            Keys.Down or
            Keys.Left or
            Keys.Right or
            Keys.NumLock or
            Keys.Divide or
            Keys.PrintScreen or
            Keys.RControlKey or
            Keys.RMenu => true,
            _ => false
        };
    }

    private static void SendUnicodeText(int codePoint)
    {
        if (!IsSupportedTextCodePoint(codePoint))
        {
            return;
        }

        string text;
        try
        {
            text = char.ConvertFromUtf32(codePoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char character in text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }

        if (inputs.Count > 0)
        {
            SendNativeInputs(inputs.ToArray());
        }
    }

    private static void SendNativeInputs(INPUT[] inputs)
    {
        uint expectedCount = (uint)inputs.Length;
        Marshal.SetLastPInvokeError(0);
        uint sentCount = SendInput(expectedCount, inputs, Marshal.SizeOf<INPUT>());
        int nativeError = sentCount == expectedCount
            ? 0
            : Marshal.GetLastPInvokeError();
        ValidateSendInputResult(sentCount, expectedCount, nativeError);
    }

    internal static void ValidateSendInputResult(
        uint sentCount,
        uint expectedCount,
        int nativeError = 0)
    {
        if (sentCount == expectedCount)
        {
            return;
        }

        throw new SendInputException(
            sentCount,
            expectedCount,
            nativeError);
    }

    internal static bool IsSupportedTextCodePoint(int codePoint)
    {
        return codePoint switch
        {
            '\t' or '\n' => true,
            < 0x20 => false,
            0x7F => false,
            >= 0xD800 and <= 0xDFFF => false,
            _ => codePoint <= 0x10FFFF
        };
    }

    private static INPUT CreateUnicodeInput(char character, bool keyUp)
    {
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wScan = (ushort)character,
                    dwFlags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                    dwExtraInfo =
                        unchecked((nint)InjectedInputMarker)
                }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
