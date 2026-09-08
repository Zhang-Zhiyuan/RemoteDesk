using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardTextServiceTests
{
    [Fact]
    public void RunClipboardOperationWithRetriesReturnsAfterTransientExternalException()
    {
        int attempts = 0;

        string result = ClipboardTextService.RunClipboardOperationWithRetries(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new ExternalException("clipboard busy");
                }

                return "ready";
            },
            retryCount: 3,
            retryDelayMs: 0);

        Assert.Equal("ready", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void RunClipboardOperationWithRetriesRethrowsFinalExternalException()
    {
        int attempts = 0;

        ExternalException ex = Assert.Throws<ExternalException>(() =>
            ClipboardTextService.RunClipboardOperationWithRetries<object?>(
                () =>
                {
                    attempts++;
                    throw new ExternalException("clipboard still busy");
                },
                retryCount: 3,
                retryDelayMs: 0));

        Assert.Equal("clipboard still busy", ex.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void CreateFileDropDataObjectIncludesPreferredCopyDropEffect()
    {
        string[] paths = [@"C:\Temp\one.txt", @"C:\Temp\two.txt"];

        DataObject dataObject = ClipboardTextService.CreateFileDropDataObject(paths);

        Assert.True(dataObject.GetDataPresent(DataFormats.FileDrop));
        Assert.True(dataObject.GetDataPresent(ClipboardTextService.PreferredDropEffectFormat));
        Assert.Equal(paths, dataObject.GetFileDropList().Cast<string>().ToArray());
        var preferredDropEffect = Assert.IsType<MemoryStream>(
            dataObject.GetData(ClipboardTextService.PreferredDropEffectFormat));
        Assert.Equal((int)DragDropEffects.Copy, BitConverter.ToInt32(preferredDropEffect.ToArray(), 0));
    }

    [Fact]
    public void CreateFileDropDataObjectRejectsEmptyList()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClipboardTextService.CreateFileDropDataObject([" "]));
    }

    [Fact]
    public async Task ClipboardSequenceWaitReturnsAsSoonAsSequenceChanges()
    {
        uint[] sequences = [41, 42];
        int readIndex = 0;
        int delays = 0;

        bool changed = await ClipboardTextService.WaitForClipboardSequenceChangeAsync(
            baseline: 41,
            readSequence: () => sequences[Math.Min(readIndex++, sequences.Length - 1)],
            pollCount: 10,
            pollInterval: TimeSpan.FromMilliseconds(15),
            delayAsync: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.True(changed);
        Assert.Equal(1, delays);
        Assert.Equal(2, readIndex);
    }

    [Fact]
    public async Task ClipboardSequenceWaitUsesBoundedPollingWhenSequenceDoesNotChange()
    {
        int reads = 0;
        int delays = 0;

        bool changed = await ClipboardTextService.WaitForClipboardSequenceChangeAsync(
            baseline: 7,
            readSequence: () =>
            {
                reads++;
                return 7;
            },
            pollCount: 3,
            pollInterval: TimeSpan.FromMilliseconds(15),
            delayAsync: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.False(changed);
        Assert.Equal(4, reads);
        Assert.Equal(3, delays);
    }
}
