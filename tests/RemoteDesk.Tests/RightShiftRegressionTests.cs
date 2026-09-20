using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RightShiftRegressionTests
{
    [Theory]
    [InlineData(0x10, 0x100, 0x01u)]
    [InlineData(0xA1, 0x100, 0x01u)]
    [InlineData(0x10, 0x101, 0x81u)]
    [InlineData(0xA1, 0x101, 0x91u)]
    [InlineData(0xA1, 0x100, 0u)]
    [InlineData(0xA1, 0x101, 0x80u)]
    public void HookRightShiftNeverEmitsAnE0Prefix(int virtualKey, int message, uint flags)
    {
        Assert.True(RemoteViewerWindow.TryCreateLowLevelKeyboardCommand(
            message, virtualKey, 0x36, flags, out var command));
        Assert.Equal(virtualKey, command.Data);
        Assert.Equal(0x36, command.X);
        Assert.Equal((int)RemoteKeyboardFlags.HasScanCode, command.Y);
        Assert.Equal(message == 0x100 ? RemoteInputKind.KeyDown : RemoteInputKind.KeyUp, command.Kind);
    }

    [Theory]
    [InlineData(0x10, 0x100, 0x01360000L)]
    [InlineData(0xA1, 0x100, 0x01360000L)]
    [InlineData(0x10, 0x101, 0xC1360000L)]
    [InlineData(0xA1, 0x101, 0xC1360000L)]
    public void WindowMessageRightShiftNeverEmitsAnE0Prefix(int virtualKey, int message, long flags)
    {
        Assert.True(RemoteViewerWindow.TryCreateRawKeyboardCommand(message, virtualKey, (nint)flags, out var command));
        Assert.Equal(virtualKey, command.Data);
        Assert.Equal(0x36, command.X);
        Assert.Equal((int)RemoteKeyboardFlags.HasScanCode, command.Y);
    }

    [Theory]
    [InlineData(0x10, false)]
    [InlineData(0xA1, false)]
    [InlineData(0x10, true)]
    [InlineData(0xA1, true)]
    public void HostAlsoRepairsRightShiftMetadataFromAnOlderViewer(int virtualKey, bool keyUp)
    {
        const RemoteKeyboardFlags flags = RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended;
        var command = keyUp ? RemoteInputCommand.KeyUp(virtualKey, 0x36, flags)
            : RemoteInputCommand.KeyDown(virtualKey, 0x36, flags);
        var input = InputInjector.CreateNativeKeyboardInput(command);
        Assert.Equal((ushort)0, input.VirtualKey);
        Assert.Equal((ushort)0x36, input.ScanCode);
        Assert.Equal(keyUp ? 0xAu : 0x8u, input.Flags);
    }

    [Theory]
    [InlineData(Keys.RControlKey, 0x1D)]
    [InlineData(Keys.RMenu, 0x38)]
    [InlineData(Keys.Enter, 0x1C)]
    [InlineData(Keys.Insert, 0x52)]
    [InlineData(Keys.PrintScreen, 0x37)]
    public void GenuineExtendedKeysRemainExtended(Keys key, int scan)
    {
        foreach (int message in new[] { 0x100, 0x101 })
        {
            Assert.True(RemoteViewerWindow.TryCreateLowLevelKeyboardCommand(message, (int)key, scan, 1, out var command));
            Assert.Equal((int)(RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended), command.Y);
            var input = InputInjector.CreateNativeKeyboardInput(command);
            Assert.Equal(message == 0x100 ? 0x9u : 0xBu, input.Flags);
        }
    }

    [Theory]
    [InlineData(Keys.LShiftKey, 0x2A)]
    [InlineData(Keys.RShiftKey, 0x36)]
    public void OrdinaryShiftPreservesItsSideAndRelease(Keys key, int scan)
    {
        foreach (int message in new[] { 0x100, 0x101 })
        {
            Assert.True(RemoteViewerWindow.TryCreateLowLevelKeyboardCommand(message, (int)key, scan, 0, out var command));
            var input = InputInjector.CreateNativeKeyboardInput(command);
            Assert.Equal((ushort)scan, input.ScanCode);
            Assert.Equal(message == 0x100 ? 0x8u : 0xAu, input.Flags);
        }
    }
}
