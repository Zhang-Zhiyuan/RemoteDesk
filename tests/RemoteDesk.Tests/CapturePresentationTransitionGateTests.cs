using Xunit;

namespace RemoteDesk.Tests;

public sealed class CapturePresentationTransitionGateTests
{
    [Fact]
    public async Task AvailabilityClearWaitsForInFlightDirectPresentationAndWins()
    {
        var gate = new CapturePresentationTransitionGate();
        var enteredPresent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePresent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool pixelsVisible = false;

        Task present = Task.Run(() => gate.Run(() =>
        {
            enteredPresent.TrySetResult();
            releasePresent.Task.GetAwaiter().GetResult();
            pixelsVisible = true;
        }));
        await enteredPresent.Task.WaitAsync(
            TimeSpan.FromSeconds(1));

        Task clear = Task.Run(() => gate.Run(() =>
        {
            pixelsVisible = false;
        }));
        await Task.Delay(25);
        Assert.False(clear.IsCompleted);

        releasePresent.TrySetResult();
        await Task.WhenAll(present, clear)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(pixelsVisible);
    }

    [Fact]
    public void UnavailableOrOldFramesAreRejectedAndRecoveryFramesPass()
    {
        Assert.False(
            RemoteViewerWindow.CanPresentCaptureFrame(
                targetAvailable: false,
                framePresentationGeneration: 4,
                currentPresentationGeneration: 4));
        Assert.False(
            RemoteViewerWindow.CanPresentCaptureFrame(
                targetAvailable: true,
                framePresentationGeneration: 4,
                currentPresentationGeneration: 5));
        Assert.True(
            RemoteViewerWindow.CanPresentCaptureFrame(
                targetAvailable: true,
                framePresentationGeneration: 5,
                currentPresentationGeneration: 5));
    }

    [Fact]
    public void TransitionPendingRejectsPresentationUntilUiBlankCompletes()
    {
        const long generation = 14;

        Assert.False(
            RemoteViewerWindow.CanPresentCaptureFrame(
                targetAvailable: true,
                transitionPending: true,
                framePresentationGeneration: generation,
                currentPresentationGeneration: generation));
        Assert.True(
            RemoteViewerWindow.CanPresentCaptureFrame(
                targetAvailable: true,
                transitionPending: false,
                framePresentationGeneration: generation,
                currentPresentationGeneration: generation));
    }

    [Fact]
    public void SameIdNewHostGenerationAdvancesPresentationOnce()
    {
        var update = new CaptureTargetAvailabilityUpdate(
            ConnectionGeneration: 4,
            IsAvailable: true,
            new CaptureTargetInfo("display-a", "屏幕 A"),
            TargetGeneration: 9,
            DisplayMessage: "已恢复");

        Assert.True(
            RemoteViewerWindow
                .ShouldAdvanceCapturePresentationForStatus(
                    currentConnectionGeneration: 4,
                    currentTargetGeneration: 8,
                    currentTargetId: "display-a",
                    currentAvailability: true,
                    update));
        Assert.False(
            RemoteViewerWindow
                .ShouldAdvanceCapturePresentationForStatus(
                    currentConnectionGeneration: 4,
                    currentTargetGeneration: 9,
                    currentTargetId: "DISPLAY-A",
                    currentAvailability: true,
                    update));
    }

    [Fact]
    public void TargetIdentityChangeAdvancesButDuplicateDoesNot()
    {
        Assert.True(
            RemoteViewerWindow
                .ShouldAdvanceCapturePresentationForTarget(
                    "display-a",
                    "display-b"));
        Assert.False(
            RemoteViewerWindow
                .ShouldAdvanceCapturePresentationForTarget(
                    "display-a",
                    "DISPLAY-A"));
    }
}
