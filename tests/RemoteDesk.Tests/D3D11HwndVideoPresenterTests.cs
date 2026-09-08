using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class D3D11HwndVideoPresenterTests
{
    [Fact]
    public void PresentedSurfaceValidationAcceptsOpaqueBlackPixels()
    {
        const int width = 16;
        const int height = 16;
        const int rowPitch = width * 4;
        nint data = Marshal.AllocHGlobal(rowPitch * height);
        try
        {
            byte[] pixels = new byte[rowPitch * height];
            for (int index = 3; index < pixels.Length; index += 4)
            {
                pixels[index] = byte.MaxValue;
            }

            Marshal.Copy(pixels, 0, data, pixels.Length);

            D3D11HwndVideoValidationResult result =
                D3D11HwndVideoPresenter.ValidateBgraSurface(
                    data,
                    rowPitch,
                    width,
                    height);

            Assert.True(result.Attempted);
            Assert.True(result.IsValid, result.Detail);
            Assert.Contains("256 opaque", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [Fact]
    public void PresentedSurfaceValidationRejectsTransparentPixels()
    {
        const int width = 16;
        const int height = 16;
        const int rowPitch = width * 4;
        nint data = Marshal.AllocHGlobal(rowPitch * height);
        try
        {
            byte[] pixels = new byte[rowPitch * height];
            Marshal.Copy(pixels, 0, data, pixels.Length);

            D3D11HwndVideoValidationResult result =
                D3D11HwndVideoPresenter.ValidateBgraSurface(
                    data,
                    rowPitch,
                    width,
                    height);

            Assert.True(result.Attempted);
            Assert.False(result.IsValid);
            Assert.Contains("0/256", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [Theory]
    [InlineData(0, 64, 16, 16)]
    [InlineData(1, 63, 16, 16)]
    [InlineData(1, 64, 0, 16)]
    [InlineData(1, 64, 16, 0)]
    public void PresentedSurfaceValidationRejectsInvalidLayout(
        long pointerValue,
        int rowPitch,
        int width,
        int height)
    {
        D3D11HwndVideoValidationResult result =
            D3D11HwndVideoPresenter.ValidateBgraSurface(
                (nint)pointerValue,
                rowPitch,
                width,
                height);

        Assert.True(result.Attempted);
        Assert.False(result.IsValid);
        Assert.Contains("invalid", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionsAcceptLowLatencyNv12Bounds()
    {
        var options =
            new D3D11HwndVideoPresenterOptions(
                WindowHandle: 42,
                SourceWidth: 3840,
                SourceHeight: 2160,
                FramesPerSecond: 120,
                ScaleMode:
                    D3D11HwndVideoScaleMode.Fill);

        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData(0, 1920, 1080, 60, 0, "HWND")]
    [InlineData(42, 0, 1080, 60, 0, "dimensions")]
    [InlineData(42, 1919, 1080, 60, 0, "even")]
    [InlineData(42, 1920, 1079, 60, 0, "even")]
    [InlineData(42, 1920, 1080, 0, 0, "Frame rate")]
    [InlineData(42, 1920, 1080, 121, 0, "Frame rate")]
    [InlineData(42, 1920, 1080, 60, 99, "scale mode")]
    public void OptionsRejectUnsafeValues(
        long windowHandle,
        int sourceWidth,
        int sourceHeight,
        int framesPerSecond,
        int scaleMode,
        string expectedDetail)
    {
        var options =
            new D3D11HwndVideoPresenterOptions(
                (nint)windowHandle,
                sourceWidth,
                sourceHeight,
                framesPerSecond,
                (D3D11HwndVideoScaleMode)scaleMode);

        string? failure = options.Validate();

        Assert.NotNull(failure);
        Assert.Contains(
            expectedDetail,
            failure,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FitAddsCenteredBlackBarArea()
    {
        D3D11HwndVideoPresentationGeometry geometry =
            D3D11HwndVideoPresenter.CalculateGeometry(
                new Rectangle(0, 0, 1920, 1080),
                new Size(800, 600),
                D3D11HwndVideoScaleMode.Fit);

        Assert.Equal(
            new Rectangle(0, 0, 1920, 1080),
            geometry.Source);
        Assert.Equal(
            new Rectangle(0, 75, 800, 450),
            geometry.Destination);
        Assert.Equal(
            new Size(800, 600),
            geometry.Output);
    }

    [Fact]
    public void FillUsesCenteredEvenNv12Crop()
    {
        D3D11HwndVideoPresentationGeometry geometry =
            D3D11HwndVideoPresenter.CalculateGeometry(
                new Rectangle(0, 0, 1920, 1080),
                new Size(800, 600),
                D3D11HwndVideoScaleMode.Fill);

        Assert.Equal(
            new Rectangle(240, 0, 1440, 1080),
            geometry.Source);
        Assert.Equal(
            new Rectangle(0, 0, 800, 600),
            geometry.Destination);
    }

    [Fact]
    public void FillPreservesVisibleOriginAndCropsVertically()
    {
        D3D11HwndVideoPresentationGeometry geometry =
            D3D11HwndVideoPresenter.CalculateGeometry(
                new Rectangle(16, 8, 1280, 960),
                new Size(1920, 1080),
                D3D11HwndVideoScaleMode.Fill);

        Assert.Equal(
            new Rectangle(16, 128, 1280, 720),
            geometry.Source);
        Assert.Equal(
            new Rectangle(0, 0, 1920, 1080),
            geometry.Destination);
    }

    [Theory]
    [InlineData((int)D3D11HwndVideoScaleMode.Fit)]
    [InlineData((int)D3D11HwndVideoScaleMode.Fill)]
    public void ScaledModesUseEntireEqualAspectSourceAndOutput(
        int scaleMode)
    {
        D3D11HwndVideoPresentationGeometry geometry =
            D3D11HwndVideoPresenter.CalculateGeometry(
                new Rectangle(2, 4, 640, 360),
                new Size(1280, 720),
                (D3D11HwndVideoScaleMode)scaleMode);

        Assert.Equal(
            new Rectangle(2, 4, 640, 360),
            geometry.Source);
        Assert.Equal(
            new Rectangle(0, 0, 1280, 720),
            geometry.Destination);
    }

    [Fact]
    public void NativePixelModeCentersSmallerSourceWithoutUpscaling()
    {
        D3D11HwndVideoPresentationGeometry geometry =
            D3D11HwndVideoPresenter.CalculateGeometry(
                new Rectangle(2, 4, 640, 360),
                new Size(1280, 720),
                D3D11HwndVideoScaleMode
                    .FitWithoutUpscaling);

        Assert.Equal(
            new Rectangle(2, 4, 640, 360),
            geometry.Source);
        Assert.Equal(
            new Rectangle(320, 180, 640, 360),
            geometry.Destination);
    }

    [Fact]
    public void NativePixelModeStillDownscalesSourceToFit()
    {
        D3D11HwndVideoPresentationGeometry geometry =
            D3D11HwndVideoPresenter.CalculateGeometry(
                new Rectangle(0, 0, 1920, 1080),
                new Size(800, 600),
                D3D11HwndVideoScaleMode
                    .FitWithoutUpscaling);

        Assert.Equal(
            new Rectangle(0, 75, 800, 450),
            geometry.Destination);
    }

    [Theory]
    [InlineData(0, 100, 0, 20)]
    [InlineData(-100, 100, 0, 20)]
    [InlineData(0, 1, 0, 1)]
    [InlineData(0, 100, 100, null)]
    [InlineData(10, 0, 0, null)]
    public void EdgeEnhancementUsesAConservativeSupportedLevel(
        int minimum,
        int maximum,
        int defaultLevel,
        int? expected)
    {
        Assert.Equal(
            expected,
            D3D11HwndVideoPresenter
                .CalculateEdgeEnhancementLevel(
                    minimum,
                    maximum,
                defaultLevel));
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080, true)]
    [InlineData(1920, 1080, 2560, 1440, true)]
    [InlineData(1920, 1080, 1920, 1080, false)]
    public void EdgeEnhancementAppliesToAnyNonNativeScale(
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        bool expected)
    {
        Assert.Equal(
            expected,
            D3D11HwndVideoPresenter
                .ShouldEnableEdgeEnhancementForScaling(
                    new Size(
                        sourceWidth,
                        sourceHeight),
                    new Size(
                        destinationWidth,
                        destinationHeight)));
    }

    [Theory]
    [InlineData(true, true, 0, true)]
    [InlineData(false, true, 0, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(true, true, 1, false)]
    public void BlitRetryIsOneShotAndRequiresEnabledEdgeEnhancement(
        bool blitFailed,
        bool edgeEnhancementEnabled,
        int completedRetryCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            D3D11HwndVideoPresenter.ShouldRetryVideoProcessorBlt(
                blitFailed,
                edgeEnhancementEnabled,
                completedRetryCount));
    }

    [Fact]
    public void BlitRetryRejectsNegativeRetryState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                D3D11HwndVideoPresenter
                    .ShouldRetryVideoProcessorBlt(
                        blitFailed: true,
                        edgeEnhancementEnabled: true,
                        completedRetryCount: -1));
    }

    [Theory]
    [InlineData(
        unchecked((int)0x887A0005),
        null,
        (int)D3D11HwndVideoPresenterStatus.DeviceRemoved)]
    [InlineData(
        unchecked((int)0x887A0007),
        null,
        (int)D3D11HwndVideoPresenterStatus.DeviceReset)]
    [InlineData(
        unchecked((int)0x887A0006),
        null,
        (int)D3D11HwndVideoPresenterStatus.DeviceHung)]
    [InlineData(
        unchecked((int)0x887A0020),
        null,
        (int)D3D11HwndVideoPresenterStatus.DeviceLost)]
    [InlineData(
        unchecked((int)0x80004005),
        unchecked((int)0x887A0007),
        (int)D3D11HwndVideoPresenterStatus.DeviceReset)]
    [InlineData(
        unchecked((int)0x80070057),
        null,
        (int)D3D11HwndVideoPresenterStatus.Failed)]
    public void DeviceFailuresAreClassified(
        int hResult,
        int? deviceRemovedReason,
        int expected)
    {
        Assert.Equal(
            (D3D11HwndVideoPresenterStatus)expected,
            D3D11HwndVideoPresenter.ClassifyFailure(
                hResult,
                deviceRemovedReason));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwapChainDescriptionUsesFiveBuffersAndFrameLatencyWaitableFlag(
        bool allowTearing)
    {
        SwapChainDescription1 description =
            D3D11HwndVideoPresenter.CreateSwapChainDescription(
                1920,
                1080,
                SwapEffect.FlipDiscard,
                allowTearing);

        Assert.Equal(
            D3D11HwndVideoPresenter.LowLatencyBufferCount,
            checked((int)description.BufferCount));
        Assert.Equal(
            SwapChainFlags.FrameLatencyWaitableObject |
                (allowTearing
                    ? SwapChainFlags.AllowTearing
                    : SwapChainFlags.None),
            description.Flags);
        Assert.Equal(
            SwapEffect.FlipDiscard,
            description.SwapEffect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentationFlagsMatchSwapChainTearingMode(
        bool allowTearing)
    {
        Assert.Equal(
            SwapChainFlags.FrameLatencyWaitableObject |
                (allowTearing
                    ? SwapChainFlags.AllowTearing
                    : SwapChainFlags.None),
            D3D11HwndVideoPresenter.ResolveSwapChainFlags(
                allowTearing));
        Assert.Equal(
            allowTearing
                ? PresentFlags.AllowTearing
                : PresentFlags.None,
            D3D11HwndVideoPresenter.ResolvePresentFlags(
                allowTearing));
    }

    [Theory]
    [InlineData(120, 17)]
    [InlineData(60, 34)]
    [InlineData(30, 67)]
    [InlineData(15, 100)]
    [InlineData(1, 100)]
    public void FrameLatencyWaitIsBoundedToTwoFramePeriods(
        int framesPerSecond,
        uint expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            D3D11HwndVideoPresenter
                .CalculateFrameLatencyWaitTimeoutMilliseconds(
                    framesPerSecond));
    }

    [Fact]
    public void InvalidOptionsDoNotTouchBorrowedDevice()
    {
        bool created =
            D3D11HwndVideoPresenter.TryCreate(
                borrowedDevice: null,
                new(
                    WindowHandle: 0,
                    SourceWidth: 64,
                    SourceHeight: 64,
                    FramesPerSecond: 30),
                out D3D11HwndVideoPresenter? presenter,
                out D3D11HwndVideoPresenterCapability
                    capability);

        Assert.False(created);
        Assert.Null(presenter);
        Assert.Equal(
            D3D11HwndVideoPresenterCapabilityStatus
                .InvalidOptions,
            capability.Status);
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void Nv12TexturePresentsWhenHardwareSmokeIsEnabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(
                "REMOTEDESK_RUN_HARDWARE_SMOKE"),
            "1",
            StringComparison.Ordinal) ||
            !OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            return;
        }

        using var window =
            new Form
            {
                ClientSize = new Size(320, 240),
                ShowInTaskbar = false
            };
        window.CreateControl();
        nint handle = window.Handle;
        using ID3D11Device device =
            D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport |
                    DeviceCreationFlags.VideoSupport,
                [
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                ]);
        bool created =
            D3D11HwndVideoPresenter.TryCreate(
                device,
                new(
                    handle,
                    SourceWidth: 64,
                    SourceHeight: 64,
                    FramesPerSecond: 30),
                out D3D11HwndVideoPresenter? presenter,
                out D3D11HwndVideoPresenterCapability
                    capability);

        Assert.True(
            created,
            $"{capability.Status}: {capability.Detail}");
        Assert.NotNull(presenter);
        Assert.True(capability.IsAvailable);
        Assert.Equal(
            D3D11HwndVideoPresenter.LowLatencyBufferCount,
            capability.BufferCount);
        Assert.True(capability.MaximumFrameLatencyIsOne);
        Assert.True(
            capability.SwapEffect is
                SwapEffect.FlipDiscard or
                SwapEffect.FlipSequential,
            $"Unexpected swap effect: {capability.SwapEffect}");
        Assert.Equal(
            capability.AllowTearing
                ? PresentFlags.AllowTearing
                : PresentFlags.None,
            D3D11HwndVideoPresenter.ResolvePresentFlags(
                capability.AllowTearing));
        Assert.Contains(
            $"{D3D11HwndVideoPresenter.LowLatencyBufferCount}-buffer",
            capability.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "maximum frame latency is 1",
            capability.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            capability.AllowTearing,
            capability.Detail.Contains(
                "tearing enabled",
                StringComparison.OrdinalIgnoreCase));
        using (presenter)
        using (ID3D11Texture2D texture =
            device.CreateTexture2D(
                new Texture2DDescription(
                    Format.NV12,
                    64,
                    64,
                    1,
                    1,
                    BindFlags.Decoder,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    1,
                    0,
                    ResourceOptionFlags.None)))
        {
            for (int frameIndex = 0; frameIndex < 8; frameIndex++)
            {
                D3D11HwndVideoPresenterResult result =
                    presenter.Present(
                        texture,
                        subresourceIndex: 0,
                        new Rectangle(0, 0, 64, 64),
                        captureValidation: true);

                Assert.True(
                    result.IsSuccess ||
                        result.Status ==
                            D3D11HwndVideoPresenterStatus.Occluded,
                    $"Frame {frameIndex}: {result.Status}: " +
                        $"0x{result.HResult:X8}, {result.Detail}");
                if (result.Status ==
                    D3D11HwndVideoPresenterStatus.Presented)
                {
                    D3D11HwndVideoValidationResult validation =
                        presenter.ValidatePresentedFrame();
                    Assert.True(
                        validation.IsValid,
                        validation.Detail);
                    Assert.Equal(
                        D3D11HwndVideoValidationResult.NotAttempted,
                        presenter.ValidatePresentedFrame());
                }
            }

            D3D11HwndVideoPresenterResult ordinaryPresent =
                presenter.Present(
                    texture,
                    subresourceIndex: 0,
                    new Rectangle(0, 0, 64, 64));
            Assert.True(
                ordinaryPresent.IsSuccess ||
                    ordinaryPresent.Status ==
                        D3D11HwndVideoPresenterStatus.Occluded,
                $"Ordinary present: {ordinaryPresent.Status}: " +
                    $"0x{ordinaryPresent.HResult:X8}, " +
                    ordinaryPresent.Detail);
            Assert.Equal(
                D3D11HwndVideoValidationResult.NotAttempted,
                presenter.ValidatePresentedFrame());

            D3D11HwndVideoPresenterResult invalidPresent =
                presenter.Present(
                    (ID3D11Texture2D?)null,
                    subresourceIndex: 0,
                    new Rectangle(0, 0, 64, 64),
                    captureValidation: true);
            Assert.Equal(
                D3D11HwndVideoPresenterStatus.InvalidTexture,
                invalidPresent.Status);
            Assert.Equal(
                D3D11HwndVideoValidationResult.NotAttempted,
                presenter.ValidatePresentedFrame());

            D3D11HwndVideoPresenterResult resize =
                presenter.Resize(640, 360);
            Assert.True(
                resize.IsSuccess,
                $"{resize.Status}: 0x{resize.HResult:X8}, " +
                    $"{resize.Detail}");
            Assert.Equal(
                new Size(640, 360),
                presenter.OutputSize);

            D3D11HwndVideoPresenterResult resizedPresent =
                presenter.Present(
                    texture,
                    subresourceIndex: 0,
                    new Rectangle(0, 0, 64, 64),
                    captureValidation: true);
            if (resizedPresent.Status ==
                D3D11HwndVideoPresenterStatus.Presented)
            {
                D3D11HwndVideoValidationResult validation =
                    presenter.ValidatePresentedFrame();
                Assert.True(
                    validation.IsValid,
                    validation.Detail);
            }
            else
            {
                Assert.Equal(
                    D3D11HwndVideoValidationResult.NotAttempted,
                    presenter.ValidatePresentedFrame());
            }
        }
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public async Task WorkerPresentSerializesWithUiResizeMinimizeAndDispose()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(
                "REMOTEDESK_RUN_HARDWARE_SMOKE"),
            "1",
            StringComparison.Ordinal) ||
            !OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            return;
        }

        using var window =
            new Form
            {
                ClientSize = new Size(320, 240),
                ShowInTaskbar = false
            };
        window.CreateControl();
        using ID3D11Device device =
            D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport |
                    DeviceCreationFlags.VideoSupport,
                [
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                ]);
        Assert.True(
            D3D11HwndVideoPresenter.TryCreate(
                device,
                new(
                    window.Handle,
                    SourceWidth: 64,
                    SourceHeight: 64,
                    FramesPerSecond: 60),
                out D3D11HwndVideoPresenter? presenter,
                out D3D11HwndVideoPresenterCapability
                    capability),
            $"{capability.Status}: {capability.Detail}");
        Assert.NotNull(presenter);
        using ID3D11Texture2D texture =
            device.CreateTexture2D(
                new Texture2DDescription(
                    Format.NV12,
                    64,
                    64,
                    1,
                    1,
                    BindFlags.Decoder,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    1,
                    0,
                    ResourceOptionFlags.None));
        var statuses =
            new ConcurrentQueue<
                D3D11HwndVideoPresenterStatus>();
        using var started =
            new ManualResetEventSlim();
        Task presentTask =
            Task.Run(
                () =>
                {
                    started.Set();
                    for (int index = 0;
                        index < 128;
                        index++)
                    {
                        statuses.Enqueue(
                            presenter.Present(
                                    texture,
                                    subresourceIndex: 0,
                                    new Rectangle(
                                        0,
                                        0,
                                        64,
                                        64))
                                .Status);
                        Thread.Yield();
                    }
                });

        Assert.True(
            started.Wait(
                TimeSpan.FromSeconds(2)));
        for (int index = 0; index < 16; index++)
        {
            D3D11HwndVideoPresenterResult minimized =
                presenter.Resize(0, 0);
            Assert.Equal(
                D3D11HwndVideoPresenterStatus
                    .SkippedMinimized,
                minimized.Status);
            D3D11HwndVideoPresenterResult restored =
                presenter.Resize(
                    320 + index,
                    240 + index);
            Assert.True(
                restored.IsSuccess,
                $"{restored.Status}: {restored.Detail}");
        }

        presenter.Dispose();
        await presentTask.WaitAsync(
            TimeSpan.FromSeconds(10));

        Assert.NotEmpty(statuses);
        Assert.All(
            statuses,
            status =>
                Assert.Contains(
                    status,
                    new[]
                    {
                        D3D11HwndVideoPresenterStatus
                            .Presented,
                        D3D11HwndVideoPresenterStatus
                            .Occluded,
                        D3D11HwndVideoPresenterStatus
                            .SkippedMinimized,
                        D3D11HwndVideoPresenterStatus
                            .Disposed
                    }));
    }
}
