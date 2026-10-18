using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerRecoveryStatusTests
{
    [Fact]
    public async Task RetiredValidationSurfaceDoesNotMarkHealthyHardwareAsFailed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var client = new RemoteViewerClient();
                using var window = new RemoteViewerWindow(client, "validation-race", false, false, false, false, false, false);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object? decision = typeof(RemoteViewerWindow).GetMethod("QualifyDirectPresentation", flags)!
                    .Invoke(window, [D3D11HwndVideoPresenterStatus.Presented, 1L]);
                Assert.Equal("Pending", decision!.ToString());
                Assert.Equal(0, (int)typeof(RemoteViewerWindow).GetField("_mediaFoundationDirectPathDisabled", flags)!.GetValue(window)!);
                Assert.Equal(0L, (long)typeof(RemoteViewerWindow).GetField("_directHardwarePresentationFailureCount", flags)!.GetValue(window)!);
                done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task SameBackendRecoveryClearsOnlyItsOwnErrorAndRequestsOneDirectFrameNotification()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var client = new RemoteViewerClient();
                // No Show(), network, capture hook or system clipboard access.
                using var window = new RemoteViewerWindow(client, "status-test", false, false, false, false, false, false);
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                object? Call(string name, params object[] args) => typeof(RemoteViewerWindow).GetMethod(name, flags)!.Invoke(window, args);
                var bar = (Control)typeof(RemoteViewerWindow).GetField("_statusBar", flags)!.GetValue(window)!;
                string Status() => (string)bar.GetType().GetProperty("StatusText")!.GetValue(bar)!;
                void Frame() => Call("UpdateRenderedVideoStatus", RemoteFrameEncoding.H264AnnexB, "MF/D3D11", "H.264 硬解");
                Frame();
                Call("SetVideoRecoveryStatus", "解码失步", Color.Red);
                Assert.Equal("解码失步", Status());
                Assert.Equal(long.MinValue, (long)typeof(RemoteViewerWindow).GetField("_directFrameUiSignature", flags)!.GetValue(window)!);
                Frame();
                Assert.Equal("画面已恢复：H.264 硬解", Status());
                Call("SetStatus", "剪贴板已同步", Color.Green);
                Frame();
                Assert.Equal("剪贴板已同步", Status());
                Call("SetVideoRecoveryStatus", "等待关键帧", Color.Red);
                Call("SetStatus", "文件保存失败", Color.Red);
                Frame();
                Assert.Equal("文件保存失败", Status());
                Call("SetVideoRecoveryStatus", "等待关键帧", Color.Red);
                Frame();
                Assert.Equal("画面已恢复：H.264 硬解", Status());
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
