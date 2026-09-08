using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class InputInjectorTests
{
    [Theory]
    [InlineData(-1920, -1920, 3840, 0)]
    [InlineData(1919, -1920, 3840, 65535)]
    [InlineData(-4000, -1920, 3840, 0)]
    [InlineData(4000, -1920, 3840, 65535)]
    [InlineData(8, 8, 1, 0)]
    public void AbsolutePointerCoordinateUsesVirtualDesktopEndpoints(
        int coordinate,
        int origin,
        int length,
        int expected)
    {
        Assert.Equal(
            expected,
            InputInjector.NormalizeAbsoluteCoordinate(
                coordinate,
                origin,
                length));
    }

    [Fact]
    public void InjectedInputMarkerIsStableAndNonZero()
    {
        Assert.Equal((nuint)0x52444B31u, InputInjector.InjectedInputMarker);
        Assert.NotEqual((nuint)0, InputInjector.InjectedInputMarker);
    }

    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;

    [Theory]
    [InlineData(0x25)] // Left
    [InlineData(0x26)] // Up
    [InlineData(0x27)] // Right
    [InlineData(0x28)] // Down
    [InlineData(0x2D)] // Insert
    [InlineData(0x2E)] // Delete
    [InlineData(0x21)] // PageUp
    [InlineData(0x22)] // PageDown
    [InlineData(0x23)] // End
    [InlineData(0x24)] // Home
    [InlineData(0x5B)] // Left Windows
    [InlineData(0x5C)] // Right Windows
    [InlineData(0xA3)] // Right Ctrl
    [InlineData(0xA5)] // Right Alt
    public void ExtendedKeysUseExtendedKeyboardFlag(int virtualKey)
    {
        Assert.Equal(KeyEventExtended, InputInjector.GetKeyboardFlags(virtualKey, keyUp: false));
        Assert.Equal(KeyEventExtended | KeyEventKeyUp, InputInjector.GetKeyboardFlags(virtualKey, keyUp: true));
    }

    [Theory]
    [InlineData(0x41)] // A
    [InlineData(0x30)] // 0
    [InlineData(0x20)] // Space
    [InlineData(0x10)] // Shift
    [InlineData(0x11)] // Ctrl
    public void NonExtendedKeysKeepOriginalKeyboardFlags(int virtualKey)
    {
        Assert.Equal(0u, InputInjector.GetKeyboardFlags(virtualKey, keyUp: false));
        Assert.Equal(KeyEventKeyUp, InputInjector.GetKeyboardFlags(virtualKey, keyUp: true));
    }

    [Fact]
    public void ValidateCommandAcceptsNormalWindowsInputCommands()
    {
        var frameSize = new Size(100, 80);

        InputInjector.ValidateCommand(RemoteInputCommand.MouseMove(10, 20), frameSize);
        InputInjector.ValidateCommand(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 20), frameSize);
        InputInjector.ValidateCommand(RemoteInputCommand.MouseUp(RemoteMouseButton.Right, 10, 20), frameSize);
        InputInjector.ValidateCommand(RemoteInputCommand.MouseWheel(120, 10, 20), frameSize);
        InputInjector.ValidateCommand(RemoteInputCommand.KeyDown(0x41));
        InputInjector.ValidateCommand(RemoteInputCommand.KeyUp(0x41));
        InputInjector.ValidateCommand(
            RemoteInputCommand.KeyDown(
                0x41,
                0x1E,
                RemoteKeyboardFlags.HasScanCode));
        InputInjector.ValidateCommand(RemoteInputCommand.TextInput('你'));
        InputInjector.ValidateCommand(RemoteInputCommand.TextInput('\n'));
        InputInjector.ValidateCommand(RemoteInputCommand.TextInput(0x1F600));
    }

    [Fact]
    public void ScanCodeKeyboardInputPreservesPhysicalAndExtendedFlags()
    {
        NativeKeyboardInput keyDown =
            InputInjector.CreateNativeKeyboardInput(
                RemoteInputCommand.KeyDown(
                    (int)Keys.RControlKey,
                    0x1D,
                    RemoteKeyboardFlags.HasScanCode |
                    RemoteKeyboardFlags.Extended));
        NativeKeyboardInput keyUp =
            InputInjector.CreateNativeKeyboardInput(
                RemoteInputCommand.KeyUp(
                    (int)Keys.RControlKey,
                    0x1D,
                    RemoteKeyboardFlags.HasScanCode |
                    RemoteKeyboardFlags.Extended));

        Assert.Equal((ushort)0, keyDown.VirtualKey);
        Assert.Equal((ushort)0x1D, keyDown.ScanCode);
        Assert.Equal(
            KeyEventScanCode | KeyEventExtended,
            keyDown.Flags);
        Assert.Equal(
            KeyEventScanCode |
            KeyEventExtended |
            KeyEventKeyUp,
            keyUp.Flags);
    }

    [Fact]
    public void LegacyKeyboardInputKeepsVirtualKeyFallback()
    {
        NativeKeyboardInput input =
            InputInjector.CreateNativeKeyboardInput(
                RemoteInputCommand.KeyDown(
                    (int)Keys.A));

        Assert.Equal((ushort)Keys.A, input.VirtualKey);
        Assert.Equal((ushort)0, input.ScanCode);
        Assert.Equal(0u, input.Flags);
    }

    [Theory]
    [InlineData(0, (int)RemoteKeyboardFlags.HasScanCode)]
    [InlineData(0x1E, (int)RemoteKeyboardFlags.None)]
    [InlineData(0, (int)RemoteKeyboardFlags.Extended)]
    [InlineData(0x1E, 0x40)]
    public void ValidateCommandRejectsInvalidScanCodeMetadata(
        int scanCode,
        int flags)
    {
        var command = new RemoteInputCommand(
            RemoteInputKind.KeyDown,
            RemoteMouseButton.None,
            scanCode,
            flags,
            (int)Keys.A);

        Assert.Throws<InvalidDataException>(
            () => InputInjector.ValidateCommand(
                command));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0xFF)]
    [InlineData(0x10000)]
    public void ValidateCommandRejectsInvalidVirtualKeys(int virtualKey)
    {
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.KeyDown(virtualKey)));
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.KeyUp(virtualKey)));
    }

    [Fact]
    public void ValidateCommandRejectsMouseButtonCommandsWithoutButton()
    {
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.MouseDown(RemoteMouseButton.None, 10, 20), new Size(100, 80)));
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.MouseUp(RemoteMouseButton.None, 10, 20), new Size(100, 80)));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(100, 10)]
    [InlineData(10, 80)]
    public void ValidateCommandRejectsPointerCoordinatesOutsideFrame(int x, int y)
    {
        var frameSize = new Size(100, 80);

        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.MouseMove(x, y), frameSize));
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, x, y), frameSize));
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.MouseWheel(120, x, y), frameSize));
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(100, 0)]
    [InlineData(-1, 80)]
    public void ValidateCommandRejectsPointerCommandsWithoutValidFrameSize(int width, int height)
    {
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.MouseMove(10, 20), new Size(width, height)));
    }

    [Fact]
    public void ValidateCommandRejectsAndroidOnlyPinchZoomOnWindowsHost()
    {
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.PinchZoom(120, 10, 20)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1F)]
    [InlineData(0x7F)]
    [InlineData(0xD800)]
    [InlineData(0xDFFF)]
    [InlineData(0x110000)]
    public void ValidateCommandRejectsInvalidTextCodePoints(int codePoint)
    {
        Assert.Throws<InvalidDataException>(() =>
            InputInjector.ValidateCommand(RemoteInputCommand.TextInput(codePoint)));
    }

    [Theory]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('A')]
    [InlineData(0x1F600)]
    public void TextCodePointPolicyAllowsSupportedText(int codePoint)
    {
        Assert.True(InputInjector.IsSupportedTextCodePoint(codePoint));
    }

    [Fact]
    public void ValidateSendInputResultAcceptsCompleteNativeSend()
    {
        InputInjector.ValidateSendInputResult(sentCount: 3, expectedCount: 3);
    }

    [Fact]
    public void ValidateSendInputResultRejectsPartialNativeSend()
    {
        SendInputException exception = Assert.Throws<SendInputException>(() =>
            InputInjector.ValidateSendInputResult(
                sentCount: 1,
                expectedCount: 3,
                nativeError: 5));

        Assert.Contains("发送输入失败：1/3", exception.Message);
        Assert.Contains("Win32 错误 5", exception.Message);
        Assert.False(exception.CanRetry);
    }

    [Fact]
    public void ValidateSendInputResultMarksZeroSendAsSafeToRetry()
    {
        SendInputException exception = Assert.Throws<SendInputException>(() =>
            InputInjector.ValidateSendInputResult(
                sentCount: 0,
                expectedCount: 1,
                nativeError: 0));

        Assert.True(exception.CanRetry);
        Assert.Equal("SendInput", exception.ApiName);
        Assert.Equal("sent=0/1", exception.DiagnosticContext);
    }

    [Fact]
    public void PartialPasteBatchReleasesPossibleKeysWithoutReplay()
    {
        int sends = 0;
        var releases = new List<string>();

        SendInputException error = Assert.Throws<SendInputException>(
            () => InputInjector.ExecutePasteShortcutBatch(
                () =>
                {
                    sends++;
                    throw new SendInputException(
                        sentCount: 2,
                        expectedCount: 4,
                        nativeError: 5);
                },
                () => releases.Add("V"),
                () => releases.Add("Control")));

        Assert.Equal(2u, error.SentCount);
        Assert.Equal(1, sends);
        Assert.Equal(["V", "Control"], releases);
    }

    [Fact]
    public void ZeroPasteBatchFailureDoesNotReleaseOrHideSafeRetry()
    {
        int releases = 0;

        SendInputException error = Assert.Throws<SendInputException>(
            () => InputInjector.ExecutePasteShortcutBatch(
                () => throw new SendInputException(
                    sentCount: 0,
                    expectedCount: 4,
                    nativeError: 0),
                () => releases++,
                () => releases++));

        Assert.True(error.CanRetry);
        Assert.Equal(0, releases);
    }
}
