using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerKeyboardBoundaryTests
{
    [Theory]
    [InlineData(Keys.Control | Keys.V, Keys.LControlKey, true)]
    [InlineData(Keys.Control | Keys.V, Keys.RControlKey, true)]
    [InlineData(Keys.Control | Keys.V, Keys.LShiftKey, false)]
    [InlineData(Keys.Control | Keys.V, Keys.RMenu, false)]
    [InlineData(Keys.Control | Keys.V, Keys.LWin, false)]
    [InlineData(Keys.Control | Keys.V, Keys.A, false)]
    [InlineData(Keys.Control | Keys.Shift | Keys.V, Keys.RShiftKey, true)]
    [InlineData(Keys.Shift | Keys.Insert, Keys.RShiftKey, true)]
    [InlineData(Keys.Shift | Keys.Insert, Keys.RControlKey, false)]
    public void DelayedPasteCannotBeModifiedByANewUnrelatedHeldKey(Keys shortcut, Keys held, bool allowed) =>
        Assert.Equal(allowed, RemoteViewerWindow.IsCompatiblePasteHeldKey(new KeyEventArgs(shortcut), (int)held));

    [Theory]
    [InlineData(Keys.Home | Keys.Shift)]
    [InlineData(Keys.Tab | Keys.Shift)]
    [InlineData(Keys.C | Keys.Control | Keys.Shift)]
    [InlineData(Keys.V | Keys.Control | Keys.Shift)]
    [InlineData(Keys.Enter | Keys.Control)]
    [InlineData(Keys.Enter | Keys.Alt)]
    [InlineData(Keys.Back | Keys.Control)]
    [InlineData(Keys.F12 | Keys.Shift)]
    public void AndroidUnsupportedModifiedActionsAreNotSilentlyReduced(Keys chord) =>
        Assert.True(RemoteViewerWindow.IsUnsupportedAndroidKeyChord(new KeyEventArgs(chord)));

    [Theory]
    [InlineData(Keys.A | Keys.Shift)]
    [InlineData(Keys.OemQuestion | Keys.Shift)]
    [InlineData(Keys.Enter)]
    [InlineData(Keys.Home)]
    [InlineData(Keys.C | Keys.Control)]
    [InlineData(Keys.X | Keys.Control)]
    [InlineData(Keys.A | Keys.Control)]
    [InlineData(Keys.V | Keys.Control)]
    [InlineData(Keys.ControlKey | Keys.Control)]
    [InlineData(Keys.RMenu | Keys.Alt)]
    public void AndroidTextAndSupportedControlChordsKeepTheirExistingPaths(Keys chord) =>
        Assert.False(RemoteViewerWindow.IsUnsupportedAndroidKeyChord(new KeyEventArgs(chord)));

    [Theory]
    [InlineData(Keys.C | Keys.Control | Keys.Alt)]
    [InlineData(Keys.X | Keys.Control | Keys.Alt)]
    [InlineData(Keys.Insert | Keys.Control | Keys.Alt)]
    [InlineData(Keys.Delete | Keys.Shift | Keys.Alt)]
    [InlineData(Keys.Delete | Keys.Shift | Keys.Control)]
    [InlineData(Keys.X | Keys.Control | Keys.Shift)]
    public void UnrelatedChordsDoNotScheduleClipboardReads(Keys chord) =>
        Assert.False(RemoteViewerWindow.TryGetClipboardPullReason(new KeyEventArgs(chord), out _));

    [Theory]
    [InlineData(Keys.C | Keys.Control)]
    [InlineData(Keys.C | Keys.Control | Keys.Shift)]
    [InlineData(Keys.X | Keys.Control)]
    [InlineData(Keys.Insert | Keys.Control)]
    [InlineData(Keys.Delete | Keys.Shift)]
    public void StandardCopyCutChordsWorkButWindowsModifiedVersionsDoNot(Keys chord)
    {
        Assert.True(RemoteViewerWindow.TryGetClipboardPullReason(new KeyEventArgs(chord), out _));
        Assert.False(RemoteViewerWindow.TryGetClipboardPullReason(new KeyEventArgs(chord), out _, windowsKeyHeld: true));
    }
}
