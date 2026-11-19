using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerPresenterRecoveryHardwareTests
{
    [HardwareSmokeFact]
    [Trait("Category", "HardwareSmoke")]
    public async Task ResolutionChangeReprobesHardwareAfterSoftwareFallback()
    {
        Assert.NotNull(FfmpegH264Decoder.AvailablePath);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var client = new RemoteViewerClient();
                using var window = new RemoteViewerWindow(client, "size-change", false, false, false, false, false, false);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(RemoteViewerWindow);
                object? Field(string name) => type.GetField(name, flags)!.GetValue(window);
                _ = window.Handle;
                _ = ((Control)Field("_pictureBox")!).Handle;
                void Send(int size, string data)
                {
                    var bytes = Convert.FromBase64String(data);
                    var frame = new RemoteFrame(size, size, RemoteFrameEncoding.H264AnnexB,
                        RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig, bytes, 0, bytes.Length, 0, 0);
                    type.GetMethod("OnFrameReceived", flags)!.Invoke(window, [frame]);
                }
                void PumpUntil(Func<bool> done)
                {
                    var elapsed = Stopwatch.StartNew();
                    while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(8))
                    {
                        Application.DoEvents();
                        Thread.Sleep(5);
                    }
                    Assert.True(done());
                }
                // 16x16 is legal on the wire but below the MFT's 48-pixel minimum.
                Send(16, "AAAAAWdCwArd7ARAAAADAEAAAAMAo8SJ4AAAAAFozg/IAAABZYiEOhGKAAIGUcAAQlY4AAhj4A==");
                PumpUntil(() => Field("_currentImage") is Bitmap);
                Assert.True(window.CollectRenderTelemetrySnapshot().DirectHardwarePathDisabled);
                Send(64, "AAAAAQkQAAAAAWdCwArcQmwEQAAAAwBAAAAPI8SJ4AAAAAFozg/IAAABZYiEOhGKAAIxccAAQ8o4AAgFycnJ1111111111114A==");
                PumpUntil(() =>
                {
                    var telemetry = window.CollectRenderTelemetrySnapshot();
                    return telemetry.DirectHardwarePresentedFrames + telemetry.DirectHardwareOccludedFrames > 0;
                });
                Assert.False(window.CollectRenderTelemetrySnapshot().DirectHardwarePathDisabled);
                Assert.Null(Field("_h264Decoder"));
                complete.TrySetResult();
            }
            catch (Exception ex) { complete.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [HardwareSmokeFact]
    [Trait("Category", "HardwareSmoke")]
    public async Task SameWindowPresenterRetirementDropsOldOutputAndKeepsHardwareDecoder()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var client = new RemoteViewerClient();
                using var window = new RemoteViewerWindow(client, "presenter-race", false, false, false, false, false, false);
                object? Field(string name) => typeof(RemoteViewerWindow).GetField(name, flags)!.GetValue(window);
                object? Call(string name, params object[] args) => typeof(RemoteViewerWindow).GetMethod(name, flags)!.Invoke(window, args);
                var picture = (Control)Field("_pictureBox")!;
                // Real HWND, MFT and GPU resources, but never show/focus this
                // fixture or send input to the interactive desktop.
                _ = window.Handle;
                nint hwnd = picture.Handle;
                byte[] accessUnit = Convert.FromBase64String(
                    "AAAAAQkQAAAAAWdCwArcQmwEQAAAAwBAAAAPI8SJ4AAAAAFozg/" +
                    "IAAABZYiEOhGKAAIxccAAQ8o4AAgFycnJ1111111111114A==");
                var source = new RemoteFrame(64, 64, RemoteFrameEncoding.H264AnnexB,
                    RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig, accessUnit, 0, accessUnit.Length, 0, 0);
                long captureGeneration = (long)Field("_capturePresentationGeneration")!;
                long decoderGeneration = 0;
                MediaFoundationD3D11DecodedFrame Decode()
                {
                    object attempt = Call("TryDecodeWithMediaFoundationD3D11", source, Stopwatch.GetTimestamp())!;
                    object? Property(string name) => attempt.GetType().GetProperty(name)!.GetValue(attempt);
                    Assert.Equal("FrameReady", Property("Disposition")!.ToString());
                    long generation = (long)Property("DecoderGeneration")!;
                    if (decoderGeneration == 0) decoderGeneration = generation;
                    else Assert.Equal(decoderGeneration, generation);
                    return Assert.IsType<MediaFoundationD3D11DecodedFrame>(Property("Frame"));
                }
                D3D11HwndVideoPresenterResult Present(MediaFoundationD3D11DecodedFrame frame) =>
                    (D3D11HwndVideoPresenterResult)Call("PresentHardwareFrameFromRenderThread", frame,
                        decoderGeneration, new Size(64, 64), captureGeneration, RemoteFrameMetadata.FromFrame(source))!;

                long softwareEpoch = (long)Field("_softwarePresentationEpoch")!;
                using (var first = Decode())
                {
                    Assert.True((long)Field("_softwarePresentationEpoch")! > softwareEpoch);
                    // Deterministically reproduce the REDMI failure: a CPU
                    // preview/UI transition retires the presenter after decode,
                    // while the HWND and hardware decoder remain unchanged.
                    Call("DisposeD3D11VideoPresenter");
                    Assert.Equal(hwnd, picture.Handle);
                    Assert.Equal(D3D11HwndVideoPresenterStatus.SkippedStaleFrame, Present(first).Status);
                    Assert.Equal(0, (int)Field("_mediaFoundationDirectPathDisabled")!);
                }
                using (var next = Decode())
                {
                    var result = Present(next);
                    Assert.True(RemoteViewerWindow.IsNonFatalPresentationResult(result.Status), result.Detail);
                    Assert.Equal(0, (int)Field("_mediaFoundationDirectPathDisabled")!);
                    Assert.False(window.Visible);
                }
                // A window message to retire the old screen is already queued when
                // the decoder finishes. A worker-side DXGI Present can overtake it.
                using (var inFlight = Decode())
                using (var started = new ManualResetEventSlim())
                {
                    window.BeginInvoke((Action)(() => Call("DisposeD3D11VideoPresenter")));
                    var submission = Task.Run(() =>
                    {
                        started.Set();
                        return Present(inFlight);
                    });
                    Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
                    bool finishedAheadOfWindow = submission.Wait(TimeSpan.FromMilliseconds(250));
                    var deadline = Stopwatch.StartNew();
                    do { Application.DoEvents(); } while (!submission.IsCompleted && deadline.ElapsedMilliseconds < 3000);
                    Assert.True(submission.IsCompleted);
                    Assert.False(finishedAheadOfWindow, "GPU output overtook the pending screen change on the window thread.");
                    Assert.Equal(D3D11HwndVideoPresenterStatus.SkippedStaleFrame, submission.GetAwaiter().GetResult().Status);
                }
                complete.TrySetResult();
            }
            catch (Exception error) { complete.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
