using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerKeyboardChordQueueTests
{
    private static readonly RemoteInputCommand[] PasteChord =
    [
        RemoteInputCommand.KeyDown(0x11),
        RemoteInputCommand.KeyDown(0x56),
        RemoteInputCommand.KeyUp(0x56),
        RemoteInputCommand.KeyUp(0x11)
    ];

    [Fact]
    public async Task LegacyBatchDemonstratesPartialAdmissionAtNormalCapacityBoundary()
    {
        using var fixture = new MemoryClient();
        RemoteInputCommand[] prefix = fixture.Fill(RemoteViewerClient.MaxQueuedInputs - 1);

        await fixture.Client.SendInputsAsync(PasteChord, fixture.Generation);

        // The existing API admits Ctrl down, refuses V down, then uses release
        // reserve for V/Ctrl up. Keep this evidence; existing callers are not
        // silently given different semantics by the new opt-in chord API.
        RemoteInputCommand[] queued = fixture.Drain();
        Assert.Equal(prefix.Concat(new[] { PasteChord[0], PasteChord[2], PasteChord[3] }), queued);
        Assert.DoesNotContain(PasteChord[1], queued);
    }

    [Theory]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    public void RejectedChordLeavesExistingQueueAndNativeRevisionUntouched(int count)
    {
        using var fixture = new MemoryClient();
        RemoteInputCommand[] prefix = fixture.Fill(count);
        long nativeRevision = fixture.Client.NativeInputRevision;

        Assert.False(fixture.Client.TryQueueKeyboardChord(PasteChord, fixture.Generation));

        Assert.Equal(prefix, fixture.Drain());
        Assert.Equal(nativeRevision, fixture.Client.NativeInputRevision);
        Assert.Equal(0, fixture.Signal.CurrentCount);
    }

    [Fact]
    public void ExactNormalCapacityAcceptsWholeChordInOrderAndWakesOnce()
    {
        using var fixture = new MemoryClient();
        RemoteInputCommand[] prefix = fixture.Fill(RemoteViewerClient.MaxQueuedInputs - PasteChord.Length);
        long nativeRevision = fixture.Client.NativeInputRevision;

        Assert.True(fixture.Client.TryQueueKeyboardChord(PasteChord, fixture.Generation));

        Assert.Equal(RemoteViewerClient.MaxQueuedInputs, fixture.Queue.Count);
        Assert.Equal(prefix.Concat(PasteChord), fixture.Drain());
        Assert.Equal(nativeRevision + 1, fixture.Client.NativeInputRevision);
        Assert.Equal(1, fixture.Signal.CurrentCount);
        Assert.True(fixture.Signal.Wait(0));
        Assert.False(fixture.Signal.Wait(0));
    }

    [Fact]
    public void RejectionDoesNotEvictQueuedMouseMoveToMakeRoom()
    {
        using var fixture = new MemoryClient();
        RemoteInputCommand[] prefix = fixture.Fill(252);
        RemoteInputCommand move = RemoteInputCommand.MouseMove(123, 456);
        Assert.True(fixture.Queue.Enqueue(move, RemoteViewerClient.MaxQueuedInputs));

        Assert.False(fixture.Client.TryQueueKeyboardChord(PasteChord, fixture.Generation));

        Assert.Equal(prefix.Append(move), fixture.Drain());
        Assert.Equal(0, fixture.Signal.CurrentCount);
    }

    [Fact]
    public void ChordNeverUsesReserveButOwnedPhysicalReleaseCanStillUseIt()
    {
        using var fixture = new MemoryClient();
        fixture.Fill(RemoteViewerClient.MaxQueuedInputs);
        RemoteInputCommand release = RemoteInputCommand.KeyUp(0xa1);

        Assert.False(fixture.Client.TryQueueKeyboardChord([release], fixture.Generation));
        Assert.Equal(RemoteViewerClient.MaxQueuedInputs, fixture.Queue.Count);
        Assert.True(fixture.Client.TryQueueOwnedInput(release, fixture.Generation));
        Assert.Equal(RemoteViewerClient.MaxQueuedInputs + 1, fixture.Queue.Count);
        Assert.Equal(release, fixture.Drain()[^1]);
    }

    [Fact]
    public void StaleGenerationNeverChangesTheReplacementQueue()
    {
        using var fixture = new MemoryClient();
        RemoteInputCommand[] prefix = fixture.Fill(2);

        Assert.False(fixture.Client.TryQueueKeyboardChord(PasteChord, fixture.Generation - 1));

        Assert.Equal(prefix, fixture.Drain());
        Assert.Equal(0, fixture.Signal.CurrentCount);
    }

    [Fact]
    public void MissingOrCancelledConnectionCannotQueueChord()
    {
        using var disconnected = new RemoteViewerClient();
        Assert.False(disconnected.TryQueueKeyboardChord(PasteChord, disconnected.InputConnectionGeneration));
        using var fixture = new MemoryClient();
        fixture.Connection.Cancel();
        Assert.False(fixture.Client.TryQueueKeyboardChord(PasteChord, fixture.Generation));
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Equal(0, fixture.Signal.CurrentCount);
    }

    [Fact]
    public void ExistingClipboardInputSuppressionRejectsChord()
    {
        using var fixture = new MemoryClient();
        SetField(fixture.Client, "_suppressInputUntilReturnedClipboardRequestDrained", 1);
        Assert.False(fixture.Client.TryQueueKeyboardChord(PasteChord, fixture.Generation));
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Equal(0, fixture.Signal.CurrentCount);
    }

    [Fact]
    public void NullEmptyOversizedAndMixedKindsAreRejectedWithoutPartialPrefix()
    {
        using var fixture = new MemoryClient();
        RemoteInputCommand[] prefix = fixture.Fill(2);
        Assert.False(fixture.Client.TryQueueKeyboardChord(null!, fixture.Generation));
        Assert.False(fixture.Client.TryQueueKeyboardChord([], fixture.Generation));
        Assert.False(fixture.Client.TryQueueKeyboardChord(
            Enumerable.Repeat(PasteChord[0], RemoteViewerClient.MaxQueuedInputs + 1).ToArray(), fixture.Generation));
        foreach (RemoteInputCommand invalid in new[]
        {
            RemoteInputCommand.TextInput('中'),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 1, 2),
            RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 1, 2),
            RemoteInputCommand.MouseMove(1, 2),
            RemoteInputCommand.MouseWheel(120, 1, 2),
            RemoteInputCommand.PinchZoom(120, 1, 2)
        })
        {
            Assert.False(fixture.Client.TryQueueKeyboardChord([PasteChord[0], invalid, PasteChord[3]], fixture.Generation));
        }
        Assert.Equal(prefix, fixture.Drain());
        Assert.Equal(0, fixture.Signal.CurrentCount);
    }

    [Fact]
    public void EmptyQueueCanAcceptMaximumBoundedKeyboardGroup()
    {
        using var fixture = new MemoryClient();
        RemoteInputCommand[] commands = Enumerable.Range(0, RemoteViewerClient.MaxQueuedInputs / 2)
            .SelectMany(_ => new[] { RemoteInputCommand.KeyDown(0x09), RemoteInputCommand.KeyUp(0x09) }).ToArray();
        Assert.True(fixture.Client.TryQueueKeyboardChord(commands, fixture.Generation));
        Assert.Equal(commands, fixture.Drain());
        Assert.Equal(1, fixture.Signal.CurrentCount);
    }

    private static void SetField(RemoteViewerClient client, string name, object? value) =>
        typeof(RemoteViewerClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, value);

    private static T Field<T>(RemoteViewerClient client, string name) =>
        (T)typeof(RemoteViewerClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;

    /// <summary>
    /// Queue-only client fixture: no sockets, authentication, send workers,
    /// clipboard access or native input. The stream is a non-null sentinel only
    /// for the existing admission predicate; it is never read or written and
    /// is detached before normal client disposal.
    /// </summary>
    private sealed class MemoryClient : IDisposable
    {
        internal RemoteViewerClient Client { get; } = new();
        internal CancellationTokenSource Connection { get; } = new();
        private readonly SecureSession _session = new(new byte[32], new byte[32], isServer: false);
        internal long Generation => Client.InputConnectionGeneration;
        internal RemoteInputQueue Queue => Field<RemoteInputQueue>(Client, "_inputQueue");
        internal SemaphoreSlim Signal => Field<SemaphoreSlim>(Client, "_inputSignal");

        internal MemoryClient()
        {
            var streamSentinel = (NetworkStream)RuntimeHelpers.GetUninitializedObject(typeof(NetworkStream));
            GC.SuppressFinalize(streamSentinel);
            SetField(Client, "_stream", streamSentinel);
            SetField(Client, "_session", _session);
            SetField(Client, "_cancellationTokenSource", Connection);
            SetField(Client, "_inputConnectionGeneration", 7L);
        }

        internal RemoteInputCommand[] Fill(int count)
        {
            RemoteInputCommand[] prefix = Enumerable.Range(0, count)
                .Select(_ => RemoteInputCommand.KeyDown(0x41)).ToArray();
            foreach (RemoteInputCommand command in prefix)
                Assert.True(Queue.Enqueue(command, RemoteViewerClient.MaxQueuedInputs));
            return prefix;
        }

        internal RemoteInputCommand[] Drain()
        {
            var result = new List<RemoteInputCommand>();
            while (Queue.TryDequeue(out RemoteInputCommand command)) result.Add(command);
            return result.ToArray();
        }

        public void Dispose()
        {
            SetField(Client, "_stream", null);
            SetField(Client, "_session", null);
            SetField(Client, "_cancellationTokenSource", null);
            Client.Dispose();
            _session.Dispose();
            Connection.Dispose();
        }
    }
}
