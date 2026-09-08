using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerImeTests
{
    [Theory]
    [InlineData(Keys.Enter, true, true)]
    [InlineData(Keys.Back, true, true)]
    [InlineData(Keys.Escape, true, true)]
    [InlineData(Keys.Left, true, true)]
    [InlineData(Keys.ProcessKey, false, true)]
    [InlineData(Keys.Packet, false, true)]
    [InlineData(Keys.ShiftKey, false, true)]
    [InlineData(Keys.Enter, false, false)]
    [InlineData(Keys.Back, false, false)]
    public void ImeOwnsCandidateKeysAndLanguageToggle(Keys key, bool composing, bool expected)
    {
        Assert.Equal(expected, RemoteViewerWindow.IsLocalImeKey(key, composing));
    }

    [Fact]
    public void SurfaceEnablesImeOnlyForLocalTextMode()
    {
        OnSta(() =>
        {
            using var surface = new RemoteViewerWindow.BufferedPictureBox();
            Assert.Equal(ImeMode.Disable, surface.ImeMode);
            surface.LocalImeEnabled = true;
            Assert.NotEqual(ImeMode.Disable, surface.ImeMode);
            surface.LocalImeEnabled = false;
            Assert.Equal(ImeMode.Disable, surface.ImeMode);
        });
    }

    [Fact]
    public void NativeImeCharactersCommitUnicodeOnceAndKeepPreeditLocal()
    {
        OnSta(() =>
        {
            long generation = 5;
            var commits = new List<(string Text, long Generation)>();
            using var surface = new RemoteViewerWindow.BufferedPictureBox
            {
                LocalImeEnabled = true,
                ReadInputGeneration = () => generation,
                TextCommitted = (text, epoch) => commits.Add((text, epoch))
            };
            _ = surface.Handle;
            SendMessage(surface.Handle, 0x010D, 0, 0); // WM_IME_STARTCOMPOSITION
            Assert.True(surface.IsImeComposing);
            surface.CommitCharacter('n');
            surface.CommitCharacter('i');
            Assert.Empty(commits);
            SendMessage(surface.Handle, 0x0286, '你', 0); // WM_IME_CHAR
            SendMessage(surface.Handle, 0x0286, '\ud83d', 0);
            SendMessage(surface.Handle, 0x0286, '\ude00', 0);
            generation = 6;
            SendMessage(surface.Handle, 0x0286, '好', 0);
            Assert.Equal(new[] { ("你", 5L), ("😀", 5L), ("好", 5L) }, commits);
            // The consumer rejects old epochs, even if reconnect occurred mid-composition.
            SendMessage(surface.Handle, 0x010E, 0, 0); // WM_IME_ENDCOMPOSITION
            Assert.False(surface.IsImeComposing);
            surface.CommitCharacter('文');
            Assert.Equal(("文", 6L), commits[^1]);
        });
    }

    [Fact]
    public void DisablingInputDropsCompositionAndPartialEmoji()
    {
        OnSta(() =>
        {
            var commits = new List<string>();
            using var surface = new RemoteViewerWindow.BufferedPictureBox
            {
                LocalImeEnabled = true,
                ReadInputGeneration = () => 5,
                TextCommitted = (text, _) => commits.Add(text)
            };
            surface.CommitCharacter('\ud83d');
            surface.LocalImeEnabled = false;
            surface.LocalImeEnabled = true;
            surface.CommitCharacter('\ude00');
            Assert.Empty(commits);
        });
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "IME test thread did not finish.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wparam, nint lparam);
}
