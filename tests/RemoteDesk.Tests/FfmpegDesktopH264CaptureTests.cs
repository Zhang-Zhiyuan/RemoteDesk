using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class FfmpegDesktopH264CaptureTests
{
    [Fact]
    public void CaptureProcessLeavesInteractionThreadsWithPriorityHeadroom()
    {
        Assert.Equal(
            ProcessPriorityClass.Normal,
            FfmpegDesktopH264Capture
                .ProductionProcessPriority);
        Assert.Equal(
            ThreadPriority.Highest,
            FfmpegDesktopH264Capture
                .RtpReaderThreadPriority);
    }

    [Fact]
    public void GfxCaptureArgumentsBindMonitorAndAdapterWithoutScaling()
    {
        Rectangle nativeBounds = new(1920, 0, 3840, 2160);
        var target = new WindowsGraphicsCaptureTarget(
            MonitorIndex: 4,
            AdapterIndex: 1,
            AdapterVendorId:
                FfmpegDesktopH264Capture.AmdVendorId,
            AdapterDescription: "AMD Radeon",
            DeviceName: @"\\.\DISPLAY5",
            Bounds: nativeBounds);
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor,
            nativeBounds,
            nativeBounds.Size,
            FramesPerSecond: 30,
            GraphicsCaptureTarget: target);

        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.AmdAmf);

        AssertArgumentValue(
            arguments,
            "-init_hw_device",
            "d3d11va=wgc:1");
        AssertArgumentValue(
            arguments,
            "-filter_hw_device",
            "wgc");
        AssertArgumentValue(
            arguments,
            "-filter_complex",
            "gfxcapture=monitor_idx=4:capture_cursor=0:" +
                "display_border=0:max_framerate=30:" +
                "output_fmt=bgra:resize_mode=crop");
        AssertArgumentValue(arguments, "-c:v", "h264_amf");
        Assert.DoesNotContain("-vf", arguments);
        Assert.DoesNotContain("-i", arguments);
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(
                "scale",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(
                "hwdownload",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(
                "gdigrab",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(
                "ddagrab",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GfxCaptureRejectsUnresolvedOrUpscaledTarget()
    {
        Rectangle nativeBounds = new(0, 0, 3840, 2160);
        var unresolved = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor,
            nativeBounds,
            nativeBounds.Size,
            FramesPerSecond: 30);
        ArgumentException unresolvedError =
            Assert.Throws<ArgumentException>(
                () => FfmpegDesktopH264Capture.BuildArguments(
                    unresolved,
                    FfmpegH264Encoder.AmdAmf));
        Assert.Contains(
            "resolved monitor and DXGI adapter",
            unresolvedError.Message);

        WindowsGraphicsCaptureTarget target =
            CreateGraphicsCaptureTarget(nativeBounds);
        ArgumentException scaledError =
            Assert.Throws<ArgumentException>(
                () => FfmpegDesktopH264Capture.BuildArguments(
                    unresolved with
                    {
                        GraphicsCaptureTarget = target,
                        OutputSize = new Size(7680, 4320)
                    },
                    FfmpegH264Encoder.AmdAmf));
        Assert.Contains(
            "cannot upscale",
            scaledError.Message);
    }

    [Fact]
    public void GfxCaptureUsesInternalGpuScaleForExactQhd()
    {
        Rectangle nativeBounds = new(0, 0, 3840, 2160);
        WindowsGraphicsCaptureTarget target =
            CreateGraphicsCaptureTarget(nativeBounds);
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor,
            nativeBounds,
            new Size(2560, 1440),
            FramesPerSecond: 60,
            GraphicsCaptureTarget: target);

        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.NvidiaNvenc);

        AssertArgumentValue(
            arguments,
            "-filter_complex",
            "gfxcapture=monitor_idx=0:capture_cursor=0:" +
                "display_border=0:max_framerate=60:" +
                "width=2560:height=1440:output_fmt=bgra:" +
                "resize_mode=scale_aspect:scale_mode=bicubic");
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(
                "hwdownload",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("-vf", arguments);
    }

    [Fact]
    public void GfxCaptureRecoversNativeLandscapeUltraHdSixtyFpsCadence()
    {
        Rectangle nativeBounds = new(0, 0, 3840, 2160);
        WindowsGraphicsCaptureTarget target =
            CreateGraphicsCaptureTarget(nativeBounds);
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor,
            nativeBounds,
            nativeBounds.Size,
            FramesPerSecond: 60,
            GraphicsCaptureTarget: target);

        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.MediaFoundation);

        AssertArgumentValue(
            arguments,
            "-filter_complex",
            "gfxcapture=monitor_idx=0:capture_cursor=0:" +
                "display_border=0:max_framerate=240:" +
                "output_fmt=bgra:resize_mode=crop," +
                "setpts=PTS-STARTPTS," +
                "select='isnan(prev_selected_t)+" +
                "gt(floor(t*60),floor(prev_selected_t*60))'," +
                "settb=expr=1/60000,setpts=N*1000," +
                "fps=fps=60:start_time=0:round=near");
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(
                "hwdownload",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GfxCaptureKeepsNativePortraitSixtyFpsOnDirectPath()
    {
        Rectangle nativeBounds = new(0, 0, 2160, 3840);
        WindowsGraphicsCaptureTarget target =
            CreateGraphicsCaptureTarget(nativeBounds);
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor,
            nativeBounds,
            nativeBounds.Size,
            FramesPerSecond: 60,
            GraphicsCaptureTarget: target);

        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.MediaFoundation);

        AssertArgumentValue(
            arguments,
            "-filter_complex",
            "gfxcapture=monitor_idx=0:capture_cursor=0:" +
                "display_border=0:max_framerate=60:" +
                "output_fmt=bgra:resize_mode=crop");
    }

    [Fact]
    public void GfxCaptureUsesOnlyTheDirectEncoderForEachAdapterCandidate()
    {
        Rectangle bounds = new(0, 0, 3840, 2160);
        FfmpegDesktopH264CaptureOptions amdOptions =
            CreateGraphicsCaptureOptions(
                bounds,
                FfmpegDesktopH264Capture.AmdVendorId);
        FfmpegDesktopH264CaptureOptions nvidiaOptions =
            CreateGraphicsCaptureOptions(
                bounds,
                FfmpegDesktopH264Capture.NvidiaVendorId);
        FfmpegDesktopH264CaptureOptions intelOptions =
            CreateGraphicsCaptureOptions(
                bounds,
                FfmpegDesktopH264Capture.IntelVendorId);

        Assert.Equal(
            [FfmpegH264Encoder.AmdAmf],
            FfmpegDesktopH264Capture.GetEncoderCandidates(
                amdOptions));
        Assert.Equal(
            [FfmpegH264Encoder.NvidiaNvenc],
            FfmpegDesktopH264Capture.GetEncoderCandidates(
                nvidiaOptions));
        Assert.Equal(
            [FfmpegH264Encoder.IntelQuickSync],
            FfmpegDesktopH264Capture.GetEncoderCandidates(
                intelOptions));
    }

    [Theory]
    [InlineData(" .. gfxcapture       |->V Capture graphics/screen content", true)]
    [InlineData(" .. ddagrab           |->V Grab Windows Desktop", false)]
    [InlineData("description mentions gfxcapture-based capture", false)]
    [InlineData("", false)]
    public void GfxCaptureCapabilityParserRequiresExactFilterName(
        string listing,
        bool expected)
    {
        Assert.Equal(
            expected,
            FfmpegDesktopH264Capture
                .ContainsGraphicsCaptureFilter(listing));
    }

    [Fact]
    public void
        GfxCaptureResolverPrioritizesNvidiaEncodeAdapterBeforeMonitorOwner()
    {
        Rectangle selectedBounds = new(1920, 0, 3840, 2160);
        nint selectedHandle = new(222);
        WindowsGraphicsMonitorIdentity[] monitors =
        [
            new(
                MonitorIndex: 0,
                MonitorHandle: new nint(111),
                DeviceName: @"\\.\DISPLAY1",
                Bounds: new Rectangle(0, 0, 1920, 1080)),
            new(
                MonitorIndex: 1,
                MonitorHandle: selectedHandle,
                DeviceName: @"\\.\DISPLAY5",
                Bounds: selectedBounds)
        ];
        WindowsGraphicsAdapterIdentity[] adapters =
        [
            new(
                AdapterIndex: 0,
                VendorId: FfmpegDesktopH264Capture.NvidiaVendorId,
                Description: "NVIDIA",
                Outputs:
                [
                    new(
                        OutputIndex: 0,
                        MonitorHandle: new nint(111))
                ]),
            new(
                AdapterIndex: 1,
                VendorId: FfmpegDesktopH264Capture.AmdVendorId,
                Description: "AMD",
                Outputs:
                [
                    new(
                        OutputIndex: 0,
                        MonitorHandle: selectedHandle)
                ])
        ];

        Assert.True(
            WindowsGraphicsCaptureTargetResolver
                .TryResolveCandidatesFromInventory(
                    @"\\.\DISPLAY5",
                    selectedBounds,
                    monitors,
                    adapters,
                    out IReadOnlyList<
                        WindowsGraphicsCaptureTarget> targets,
                    out WindowsDesktopDuplicationTarget?
                        desktopDuplicationTarget,
                    out string failureDetail),
            failureDetail);
        Assert.Equal(2, targets.Count);
        Assert.Equal(1, targets[0].MonitorIndex);
        Assert.Equal(0, targets[0].AdapterIndex);
        Assert.Equal(
            FfmpegDesktopH264Capture.NvidiaVendorId,
            targets[0].AdapterVendorId);
        Assert.False(targets[0].IsMonitorOwningAdapter);
        Assert.Equal(1, targets[1].AdapterIndex);
        Assert.Equal(
            FfmpegDesktopH264Capture.AmdVendorId,
            targets[1].AdapterVendorId);
        Assert.True(targets[1].IsMonitorOwningAdapter);
        Assert.All(
            targets,
            target =>
                Assert.Equal(selectedBounds, target.Bounds));
        Assert.NotNull(desktopDuplicationTarget);
        Assert.Equal(1, desktopDuplicationTarget.AdapterIndex);
        Assert.Equal(0, desktopDuplicationTarget.OutputIndex);
    }

    [Fact]
    public void
        DxgiResolverMapsGlobalDisplayTwoToOwningAdapterOutputZero()
    {
        Rectangle selectedBounds = new(3840, 0, 3840, 2160);
        nint primaryHandle = new(111);
        nint selectedHandle = new(222);
        WindowsGraphicsMonitorIdentity[] monitors =
        [
            new(
                MonitorIndex: 0,
                MonitorHandle: primaryHandle,
                DeviceName: @"\\.\DISPLAY1",
                Bounds: new Rectangle(0, 0, 3840, 2160)),
            new(
                MonitorIndex: 1,
                MonitorHandle: selectedHandle,
                DeviceName: @"\\.\DISPLAY2",
                Bounds: selectedBounds)
        ];
        WindowsGraphicsAdapterIdentity[] adapters =
        [
            new(
                AdapterIndex: 0,
                VendorId: FfmpegDesktopH264Capture.AmdVendorId,
                Description: "AMD display owner",
                Outputs:
                [
                    new(
                        OutputIndex: 0,
                        MonitorHandle: selectedHandle)
                ]),
            new(
                AdapterIndex: 1,
                VendorId: FfmpegDesktopH264Capture.NvidiaVendorId,
                Description: "NVIDIA render adapter",
                Outputs:
                [
                    new(
                        OutputIndex: 0,
                        MonitorHandle: primaryHandle)
                ])
        ];

        Assert.True(
            WindowsGraphicsCaptureTargetResolver
                .TryResolveCandidatesFromInventory(
                    @"\\.\DISPLAY2",
                    selectedBounds,
                    monitors,
                    adapters,
                    out IReadOnlyList<
                        WindowsGraphicsCaptureTarget> targets,
                    out WindowsDesktopDuplicationTarget?
                        desktopDuplicationTarget,
                    out string failureDetail),
            failureDetail);

        Assert.Equal(1, targets[0].MonitorIndex);
        Assert.Equal(1, targets[0].AdapterIndex);
        Assert.False(targets[0].IsMonitorOwningAdapter);
        Assert.NotNull(desktopDuplicationTarget);
        Assert.Equal(0, desktopDuplicationTarget.AdapterIndex);
        Assert.Equal(0, desktopDuplicationTarget.OutputIndex);
        Assert.Equal(@"\\.\DISPLAY2", desktopDuplicationTarget.DeviceName);
        Assert.Equal(selectedBounds, desktopDuplicationTarget.Bounds);
    }

    [Fact]
    public void DxgiResolverUsesPerAdapterOutputIndex()
    {
        Rectangle selectedBounds = new(1920, 0, 1920, 1080);
        nint selectedHandle = new(222);
        WindowsGraphicsMonitorIdentity[] monitors =
        [
            new(
                MonitorIndex: 1,
                MonitorHandle: selectedHandle,
                DeviceName: @"\\.\DISPLAY2",
                Bounds: selectedBounds)
        ];
        WindowsGraphicsAdapterIdentity[] adapters =
        [
            new(
                AdapterIndex: 3,
                VendorId: FfmpegDesktopH264Capture.IntelVendorId,
                Description: "Intel",
                Outputs:
                [
                    new(
                        OutputIndex: 0,
                        MonitorHandle: new nint(111)),
                    new(
                        OutputIndex: 1,
                        MonitorHandle: selectedHandle)
                ])
        ];

        Assert.True(
            WindowsGraphicsCaptureTargetResolver
                .TryResolveCandidatesFromInventory(
                    @"\\.\DISPLAY2",
                    selectedBounds,
                    monitors,
                    adapters,
                    out _,
                    out WindowsDesktopDuplicationTarget?
                        desktopDuplicationTarget,
                    out string failureDetail),
            failureDetail);
        Assert.NotNull(desktopDuplicationTarget);
        Assert.Equal(3, desktopDuplicationTarget.AdapterIndex);
        Assert.Equal(1, desktopDuplicationTarget.OutputIndex);
    }

    [Fact]
    public void DxgiResolverRejectsAmbiguousOutputOwnership()
    {
        Rectangle bounds = new(0, 0, 1920, 1080);
        nint monitorHandle = new(111);
        WindowsGraphicsMonitorIdentity[] monitors =
        [
            new(
                MonitorIndex: 0,
                MonitorHandle: monitorHandle,
                DeviceName: @"\\.\DISPLAY1",
                Bounds: bounds)
        ];
        WindowsGraphicsAdapterIdentity[] adapters =
        [
            new(
                AdapterIndex: 0,
                VendorId: FfmpegDesktopH264Capture.AmdVendorId,
                Description: "AMD",
                Outputs:
                [
                    new(0, monitorHandle)
                ]),
            new(
                AdapterIndex: 1,
                VendorId: FfmpegDesktopH264Capture.NvidiaVendorId,
                Description: "NVIDIA",
                Outputs:
                [
                    new(0, monitorHandle)
                ])
        ];

        Assert.False(
            WindowsGraphicsCaptureTargetResolver
                .TryResolveCandidatesFromInventory(
                    @"\\.\DISPLAY1",
                    bounds,
                    monitors,
                    adapters,
                    out IReadOnlyList<
                        WindowsGraphicsCaptureTarget> targets,
                    out WindowsDesktopDuplicationTarget?
                        desktopDuplicationTarget,
                    out string failureDetail));
        Assert.Empty(targets);
        Assert.Null(desktopDuplicationTarget);
        Assert.Contains("Multiple DXGI outputs", failureDetail);
    }

    [Fact]
    public void GfxCaptureTargetResolverRejectsStaleBounds()
    {
        WindowsGraphicsMonitorIdentity[] monitors =
        [
            new(
                MonitorIndex: 0,
                MonitorHandle: new nint(111),
                DeviceName: @"\\.\DISPLAY5",
                Bounds: new Rectangle(0, 0, 3840, 2160))
        ];

        Assert.False(
            WindowsGraphicsCaptureTargetResolver
                .TryResolveFromInventory(
                    @"\\.\DISPLAY5",
                    new Rectangle(0, 0, 1920, 1080),
                    monitors,
                    [],
                    out WindowsGraphicsCaptureTarget? target,
                    out string failureDetail));
        Assert.Null(target);
        Assert.Contains("changed bounds", failureDetail);
    }

    [Fact]
    public void DdaNvencArgumentsBindOwningAdapterAndOutput()
    {
        FfmpegDesktopH264CaptureOptions options = CreateOptions(
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0) with
        {
            DesktopDuplicationTarget =
                CreateDesktopDuplicationTarget(
                    new Rectangle(0, 0, 3840, 2160),
                    adapterIndex: 2,
                    outputIndex: 1)
        };

        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.NvidiaNvenc);

        AssertArgumentValue(
            arguments,
            "-init_hw_device",
            "d3d11va=dda:2");
        AssertArgumentValue(
            arguments,
            "-filter_hw_device",
            "dda");
        AssertArgumentValue(
            arguments,
            "-filter_complex",
            "ddagrab=output_idx=1:draw_mouse=0:framerate=30:" +
                "dup_frames=1,scale_d3d11=width=1920:" +
                "height=1080:format=nv12");
        AssertArgumentValue(arguments, "-c:v", "h264_nvenc");
        AssertArgumentValue(arguments, "-preset", "p4");
        AssertArgumentValue(arguments, "-tune", "ull");
        AssertArgumentValue(arguments, "-rc", "vbr");
        AssertArgumentValue(arguments, "-spatial-aq", "1");
        AssertArgumentValue(arguments, "-aq-strength", "1");
        Assert.DoesNotContain("-cq", arguments);
        AssertArgumentValue(arguments, "-g", "1");
        AssertArgumentValue(arguments, "-bf", "0");
        AssertArgumentValue(arguments, "-rc-lookahead", "0");
        AssertArgumentValue(arguments, "-surfaces", "1");
        AssertArgumentValue(arguments, "-delay", "0");
        AssertArgumentValue(arguments, "-zerolatency", "1");
        AssertArgumentValue(arguments, "-forced-idr", "1");
        Assert.DoesNotContain("-nostdin", arguments);
        AssertArgumentValue(
            arguments,
            "-fps_mode",
            "passthrough");
        AssertArgumentValue(
            arguments,
            "-avioflags",
            "direct");
        AssertArgumentValue(
            arguments,
            "-bsf:v",
            "dump_extra=freq=keyframe," +
                "h264_metadata=aud=insert");
        AssertArgumentValue(arguments, "-flush_packets", "1");
        Assert.Equal("pipe:1", arguments[^1]);
        Assert.DoesNotContain("-i", arguments);
        Assert.All(
            arguments,
            argument => Assert.DoesNotContain('"', argument));
    }

    [Fact]
    public void DdaRejectsUnresolvedOrStaleOutputMapping()
    {
        FfmpegDesktopH264CaptureOptions options = CreateOptions(
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0);

        ArgumentException unresolved = Assert.Throws<ArgumentException>(
            () => FfmpegDesktopH264Capture.BuildArguments(
                options with
                {
                    DesktopDuplicationTarget = null
                },
                FfmpegH264Encoder.MediaFoundation));
        Assert.Contains(
            "resolved adapter and per-adapter output",
            unresolved.Message);

        ArgumentException negativeIndex =
            Assert.Throws<ArgumentException>(
                () => FfmpegDesktopH264Capture.BuildArguments(
                    options with
                    {
                        DesktopDuplicationTarget =
                            options.DesktopDuplicationTarget! with
                            {
                                OutputIndex = -1
                            }
                    },
                    FfmpegH264Encoder.MediaFoundation));
        Assert.Contains("non-negative", negativeIndex.Message);

        ArgumentException staleBounds =
            Assert.Throws<ArgumentException>(
                () => FfmpegDesktopH264Capture.BuildArguments(
                    options with
                    {
                        DesktopDuplicationTarget =
                            options.DesktopDuplicationTarget! with
                            {
                                Bounds = new Rectangle(
                                    1920,
                                    0,
                                    3840,
                                    2160)
                            }
                    },
                    FfmpegH264Encoder.MediaFoundation));
        Assert.Contains(
            "no longer match",
            staleBounds.Message);
    }

    [Fact]
    public void KillOnCloseJobCoreReturnsLeaseOnlyAfterAssignment()
    {
        var createdJob = new SafeFileHandle(
            new nint(123),
            ownsHandle: false);
        bool configured = false;
        nint assignedProcess = nint.Zero;

        SafeFileHandle? assignedJob =
            WindowsKillOnCloseJob.TryCreateAndAssignCore(
                new nint(456),
                () => createdJob,
                job =>
                {
                    configured = ReferenceEquals(job, createdJob);
                    return true;
                },
                (job, processHandle) =>
                {
                    Assert.Same(createdJob, job);
                    assignedProcess = processHandle;
                    return true;
                });

        Assert.Same(createdJob, assignedJob);
        Assert.True(configured);
        Assert.Equal(new nint(456), assignedProcess);
        Assert.False(createdJob.IsClosed);
        assignedJob!.Dispose();
        Assert.True(createdJob.IsClosed);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void KillOnCloseJobCoreClosesRejectedLease(
        bool configurationSucceeds,
        bool assignmentSucceeds)
    {
        var createdJob = new SafeFileHandle(
            new nint(789),
            ownsHandle: false);
        int assignmentCalls = 0;

        SafeFileHandle? assignedJob =
            WindowsKillOnCloseJob.TryCreateAndAssignCore(
                new nint(987),
                () => createdJob,
                _ => configurationSucceeds,
                (_, _) =>
                {
                    assignmentCalls++;
                    return assignmentSucceeds;
                });

        Assert.Null(assignedJob);
        Assert.True(createdJob.IsClosed);
        Assert.Equal(
            configurationSucceeds ? 1 : 0,
            assignmentCalls);
    }

    [Fact]
    public void LoopbackRtpArgumentsUsePt96MarkerDatagramsWithoutRtcp()
    {
        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildLoopbackRtpArguments(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                FfmpegH264Encoder.NvidiaNvenc,
                rtpPort: 54321);

        AssertArgumentValue(arguments, "-f", "tee");
        AssertArgumentValue(arguments, "-flush_packets", "1");
        Assert.Equal(
            "[f=rtp:payload_type=96:rtpflags=skip_rtcp:" +
                "onfail=ignore]rtp://127.0.0.1:54321?" +
                "pkt_size=16384&connect=1&localaddr=127.0.0.1|" +
                "[f=h264]pipe:1",
            arguments[^1]);
        Assert.DoesNotContain("-payload_type", arguments);
        Assert.DoesNotContain("-rtpflags", arguments);
        Assert.Equal(
            16 * 1024,
            FfmpegDesktopH264Capture
                .LoopbackRtpPacketSizeBytes);
        Assert.Equal(
            1200,
            LowLatencyVideoProtocol.DefaultMaxDatagramBytes);
        Assert.True(
            FfmpegDesktopH264Capture
                .LoopbackRtpPacketSizeBytes >
            LowLatencyVideoProtocol.DefaultMaxDatagramBytes);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegDesktopH264Capture
                .BuildLoopbackRtpArguments(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    FfmpegH264Encoder.NvidiaNvenc,
                    rtpPort: 0));

        IReadOnlyList<string> gdiArguments =
            FfmpegDesktopH264Capture.BuildLoopbackRtpArguments(
                CreateOptions(
                    FfmpegDesktopCaptureBackend.GdiGrabBounds),
                FfmpegH264Encoder.NvidiaNvenc,
                rtpPort: 54322);
        AssertArgumentValue(gdiArguments, "-map", "0:v:0");
    }

    [Fact]
    public void HardwareCandidateMustProduceARealFrameWithinLatencyBudget()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(750),
            FfmpegDesktopH264Capture.DefaultStartupTimeout);
        Assert.Equal(
            TimeSpan.FromMilliseconds(1500),
            FfmpegDesktopH264Capture
                .GetStartupTimeout(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0,
                    FfmpegH264Encoder.AmdAmf,
                    FfmpegDesktopH264Capture
                        .DefaultStartupTimeout));
        Assert.Equal(
            TimeSpan.FromMilliseconds(6000),
            FfmpegDesktopH264Capture
                .GetStartupTimeout(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0,
                    FfmpegH264Encoder.MediaFoundation,
                    FfmpegDesktopH264Capture
                        .DefaultStartupTimeout));
        Assert.Equal(
            TimeSpan.FromMilliseconds(1500),
            FfmpegDesktopH264Capture
                .GetStartupTimeout(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0,
                    FfmpegH264Encoder.MediaFoundation,
                    FfmpegDesktopH264Capture
                        .DefaultStartupTimeout,
                    gopLength: 1));
        Assert.Equal(
            TimeSpan.FromMilliseconds(400),
            FfmpegDesktopH264Capture
                .GetStartupTimeout(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0,
                    FfmpegH264Encoder.NvidiaNvenc,
                    FfmpegDesktopH264Capture
                        .DefaultStartupTimeout));
        Assert.Equal(
            TimeSpan.FromMilliseconds(2500),
            FfmpegDesktopH264Capture
                .GetStartupTimeout(
                    FfmpegDesktopCaptureBackend
                        .WindowsGraphicsCaptureMonitor,
                    FfmpegH264Encoder.NvidiaNvenc,
                    FfmpegDesktopH264Capture
                        .DefaultStartupTimeout));
        Assert.Equal(
            FfmpegDesktopH264Capture.DefaultStartupTimeout,
            FfmpegDesktopH264Capture
                .GetStartupTimeout(
                    FfmpegDesktopCaptureBackend.GdiGrabBounds,
                    FfmpegH264Encoder.MediaFoundation,
                    FfmpegDesktopH264Capture
                        .DefaultStartupTimeout));
        Assert.Equal(
            FfmpegDesktopH264Capture.DefaultStartupTimeout,
            FfmpegDesktopH264Capture
                .GetStartupTimeout(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0,
                    FfmpegH264Encoder.MediaFoundation,
                    FfmpegDesktopH264Capture
                        .DefaultStartupTimeout,
                    useEncoderSpecificStartupTimeouts: false));
        Assert.Equal(
            TimeSpan.FromMilliseconds(1500),
            FfmpegDesktopH264Capture
                .CalculateVerifiedReconnectStartupTimeout(
                    TimeSpan.FromMilliseconds(400)));
        Assert.Equal(
            TimeSpan.FromMilliseconds(2000),
            FfmpegDesktopH264Capture
                .CalculateVerifiedReconnectStartupTimeout(
                    TimeSpan.FromMilliseconds(800)));
        Assert.Equal(
            TimeSpan.FromMilliseconds(2500),
            FfmpegDesktopH264Capture
                .CalculateVerifiedReconnectStartupTimeout(
                    TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void WgcStaticDesktopGetsAnIndependentSteadyStateStallBudget()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(6),
            FfmpegDesktopH264Capture
                .WindowsGraphicsCaptureStallTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(6),
            FfmpegDesktopH264Capture
                .GetProductionStallTimeout(
                    FfmpegDesktopCaptureBackend
                        .WindowsGraphicsCaptureMonitor));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            FfmpegDesktopH264Capture
                .GetProductionStallTimeout(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            FfmpegDesktopH264Capture
                .GetProductionStallTimeout(
                    FfmpegDesktopCaptureBackend
                        .GdiGrabBounds));
    }

    [Theory]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend
            .DesktopDuplicationOutput0,
        true,
        true,
        true)]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend
            .DesktopDuplicationOutput0,
        true,
        false,
        false)]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend
            .GdiGrabBounds,
        true,
        true,
        false)]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend
            .DesktopDuplicationOutput0,
        false,
        true,
        false)]
    public void
        DdaDiscoveryStopsOnlyForProductionStartupDeadline(
            int backendValue,
            bool useProductionStartupPolicy,
            bool startupDeadlineExpired,
            bool expected)
    {
        Assert.Equal(
            expected,
            FfmpegDesktopH264Capture
                .ShouldStopDdaDiscoveryAfterStartupDeadline(
                    (FfmpegDesktopCaptureBackend)
                        backendValue,
                    useProductionStartupPolicy,
                    startupDeadlineExpired));
    }

    [Theory]
    [InlineData(30, 66.6666667)]
    [InlineData(60, 50)]
    [InlineData(15, 133.3333333)]
    public void RtpFallbackWaitsForTwoFramePeriods(
        int framesPerSecond,
        double expectedMilliseconds)
    {
        double actualMilliseconds =
            FfmpegDesktopH264Capture
                .CalculateRtpFallbackSilence(framesPerSecond)
                .TotalMilliseconds;
        Assert.InRange(
            actualMilliseconds,
            expectedMilliseconds - 0.001d,
            expectedMilliseconds + 0.001d);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegDesktopH264Capture
                .CalculateRtpFallbackSilence(0));
    }

    [Fact]
    public void RawFallbackParserUsesRtpSilenceBoundary()
    {
        TimeSpan silence =
            FfmpegDesktopH264Capture
                .CalculateRtpFallbackSilence(30);
        long silenceTicks = checked(
            (long)Math.Ceiling(
                silence.TotalSeconds *
                Stopwatch.Frequency));
        long oneMillisecondTicks =
            Math.Max(
                1,
                Stopwatch.Frequency / 1_000);
        const long lastRtpFrameAt = 10_000;

        Assert.True(
            FfmpegDesktopH264Capture
                .ShouldParseRawAnnexBFallback(
                    lastRtpFrameAt: 0,
                    observedAt: lastRtpFrameAt,
                    framesPerSecond: 30));
        Assert.False(
            FfmpegDesktopH264Capture
                .ShouldParseRawAnnexBFallback(
                    lastRtpFrameAt,
                    observedAt:
                        lastRtpFrameAt +
                        silenceTicks -
                        oneMillisecondTicks,
                    framesPerSecond: 30));
        Assert.True(
            FfmpegDesktopH264Capture
                .ShouldParseRawAnnexBFallback(
                    lastRtpFrameAt,
                    observedAt:
                        lastRtpFrameAt +
                        silenceTicks +
                        oneMillisecondTicks,
                    framesPerSecond: 30));
        Assert.False(
            FfmpegDesktopH264Capture
                .ShouldParseRawAnnexBFallback(
                    lastRtpFrameAt:
                        lastRtpFrameAt + 1,
                    observedAt:
                        lastRtpFrameAt,
                    framesPerSecond: 30));
    }

    [Fact]
    public void MediaFoundationArgumentsForceHardwareLowDelayDisplayRemoting()
    {
        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                CreateOptions(
                    FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0),
                FfmpegH264Encoder.MediaFoundation);

        AssertArgumentValue(arguments, "-c:v", "h264_mf");
        AssertArgumentValue(arguments, "-hw_encoding", "1");
        AssertArgumentValue(arguments, "-rate_control", "ld_vbr");
        AssertArgumentValue(
            arguments,
            "-scenario",
            "display_remoting");
        AssertArgumentValue(arguments, "-g", "1");
        AssertArgumentValue(arguments, "-bf", "0");
        AssertArgumentValue(arguments, "-flags", "+low_delay");
    }

    [Fact]
    public void GdiGrabArgumentsUseExactVirtualDesktopBounds()
    {
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend.GdiGrabBounds,
            new Rectangle(-1920, 120, 1600, 900),
            new Size(1280, 720),
            FramesPerSecond: 45);

        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.AmdAmf);

        AssertArgumentValue(arguments, "-f", "gdigrab");
        AssertArgumentValue(arguments, "-draw_mouse", "0");
        AssertArgumentValue(arguments, "-offset_x", "-1920");
        AssertArgumentValue(arguments, "-offset_y", "120");
        AssertArgumentValue(arguments, "-video_size", "1600x900");
        AssertArgumentValue(arguments, "-i", "desktop");
        AssertArgumentValue(
            arguments,
            "-vf",
            "scale=1280:720:flags=bicubic+accurate_rnd,format=nv12");
        AssertArgumentValue(arguments, "-c:v", "h264_amf");
        AssertArgumentValue(
            arguments,
            "-usage",
            "ultralowlatency");
        AssertArgumentValue(arguments, "-quality", "balanced");
        AssertArgumentValue(arguments, "-async_depth", "1");
        AssertArgumentValue(arguments, "-g", "1");
    }

    [Fact]
    public void NativeSizeGdiCaptureConvertsWithoutSpatialResampling()
    {
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend.GdiGrabBounds,
            new Rectangle(0, 0, 3840, 2160),
            new Size(3840, 2160),
            FramesPerSecond: 30);

        IReadOnlyList<string> arguments =
            FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.NvidiaNvenc);

        AssertArgumentValue(
            arguments,
            "-vf",
            "format=nv12");
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(
                "scale=",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DdaPrefersProvenMediaFoundationPathBeforeVendorFallbacks()
    {
        Assert.Equal(
            [
                FfmpegH264Encoder.MediaFoundation,
                FfmpegH264Encoder.AmdAmf,
                FfmpegH264Encoder.NvidiaNvenc,
                FfmpegH264Encoder.IntelQuickSync,
            ],
            FfmpegDesktopH264Capture.GetEncoderCandidates(
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0));
        Assert.Equal(
            [
                FfmpegH264Encoder.NvidiaNvenc,
                FfmpegH264Encoder.MediaFoundation,
                FfmpegH264Encoder.IntelQuickSync,
                FfmpegH264Encoder.AmdAmf
            ],
            FfmpegDesktopH264Capture.GetEncoderCandidates(
                FfmpegDesktopCaptureBackend.GdiGrabBounds));
    }

    [Fact]
    public void EveryWindowsCandidateNamesAnExplicitHardwareEncoder()
    {
        var expectedNames =
            new Dictionary<FfmpegH264Encoder, string>
            {
                [FfmpegH264Encoder.NvidiaNvenc] = "h264_nvenc",
                [FfmpegH264Encoder.MediaFoundation] = "h264_mf",
                [FfmpegH264Encoder.IntelQuickSync] = "h264_qsv",
                [FfmpegH264Encoder.AmdAmf] = "h264_amf"
            };

        Assert.Equal(
            expectedNames.Keys.OrderBy(value => value),
            Enum.GetValues<FfmpegH264Encoder>()
                .OrderBy(value => value));
        foreach ((FfmpegH264Encoder encoder, string name) in
                 expectedNames)
        {
            IReadOnlyList<string> arguments =
                FfmpegDesktopH264Capture.BuildArguments(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .GdiGrabBounds),
                    encoder);

            AssertArgumentValue(arguments, "-c:v", name);
            Assert.DoesNotContain("libx264", arguments);
            Assert.DoesNotContain("libopenh264", arguments);
        }

        IReadOnlyList<string> mediaFoundation =
            FfmpegDesktopH264Capture.BuildArguments(
                CreateOptions(
                    FfmpegDesktopCaptureBackend.GdiGrabBounds),
                FfmpegH264Encoder.MediaFoundation);
        AssertArgumentValue(
            mediaFoundation,
            "-hw_encoding",
            "1");
    }

    [Fact]
    public void NegotiatedShortGopAppliesToEveryHardwareEncoder()
    {
        const int sourceFramesPerSecond = 60;
        const int bitrateFramesPerSecond = 30;
        int expectedBitrate =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(1920, 1080),
                bitrateFramesPerSecond);
        int expectedVbv =
            FfmpegDesktopH264Capture.CalculateVbvBufferBits(
                expectedBitrate,
                bitrateFramesPerSecond);

        foreach (FfmpegH264Encoder encoder in
                 Enum.GetValues<FfmpegH264Encoder>())
        {
            IReadOnlyList<string> arguments =
                FfmpegDesktopH264Capture.BuildArguments(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0) with
                    {
                        GopLength =
                            FfmpegDesktopH264Capture
                                .ShortGopLength,
                        FramesPerSecond = sourceFramesPerSecond,
                        BitrateFramesPerSecond =
                            bitrateFramesPerSecond
                    },
                    encoder);

            AssertArgumentValue(arguments, "-g", "2");
            AssertArgumentValue(arguments, "-bf", "0");
            AssertArgumentValue(
                arguments,
                "-b:v",
                expectedBitrate.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AssertArgumentValue(
                arguments,
                "-bufsize",
                expectedVbv.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void BitrateAndDesktopRefreshVbvAreBounded()
    {
        Assert.Equal(
            FfmpegDesktopH264Capture.MinimumBitrateBitsPerSecond,
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(320, 240),
                1));
        Assert.Equal(
            FfmpegDesktopH264Capture.MaximumBitrateBitsPerSecond,
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(8192, 8192),
                120));

        int bitrate =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(1920, 1080),
                30);
        int ultraHd30Bitrate =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(3840, 2160),
                30);
        int ultraHd60Bitrate =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(3840, 2160),
                60);
        int vbv =
            FfmpegDesktopH264Capture.CalculateVbvBufferBits(
                bitrate,
                30);

        Assert.InRange(bitrate, 11_000_000, 11_300_000);
        Assert.Equal(59_700_000, ultraHd30Bitrate);
        Assert.Equal(79_600_000, ultraHd60Bitrate);
        Assert.True(
            ultraHd60Bitrate < 100_000_000,
            "Native 4K60 must leave practical Wi-Fi airtime for encrypted " +
            "UDP framing, feedback, and input instead of saturating the link.");
        Assert.Equal(
            (int)Math.Ceiling(
                bitrate / 30d *
                FfmpegDesktopH264Capture
                    .DesktopVbvFrameCapacity),
            vbv);
    }

    [Fact]
    public void UltraHdBitrateIsMonotonicAcrossFrameRateAndResolutionBoundaries()
    {
        int previousFrameRateBitrate = 0;
        for (int framesPerSecond = 1;
             framesPerSecond <= 120;
             framesPerSecond++)
        {
            int current =
                FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                    new Size(3840, 2160),
                    framesPerSecond);
            Assert.True(
                current >= previousFrameRateBitrate,
                $"4K bitrate fell at {framesPerSecond} FPS: " +
                $"{previousFrameRateBitrate} -> {current}.");
            previousFrameRateBitrate = current;
        }

        int previousResolutionBitrate = 0;
        for (int width = 1600; width <= 4200; width += 2)
        {
            int current =
                FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                    new Size(width, 2160),
                    60);
            Assert.True(
                current >= previousResolutionBitrate,
                $"60 FPS bitrate fell at {width}x2160: " +
                $"{previousResolutionBitrate} -> {current}.");
            previousResolutionBitrate = current;
        }

        int ultraHd30 =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(3840, 2160),
                30);
        int ultraHd31 =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(3840, 2160),
                31);
        int almostUltraHd60 =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(3838, 2160),
                60);
        int ultraHd60 =
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(
                new Size(3840, 2160),
                60);

        Assert.True(ultraHd31 >= ultraHd30);
        Assert.True(ultraHd60 >= almostUltraHd60);
    }

    [Fact]
    public void LoopbackReceiveBufferCoversMeasuredMfStartupBurstWithinBounds()
    {
        const int measuredMfStartupBurstBytes = 1415 * 1024;
        int fullHdBuffer =
            FfmpegDesktopH264Capture
                .CalculateLoopbackRtpReceiveBufferBytes(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0));
        int measuredRemoteBuffer =
            FfmpegDesktopH264Capture
                .CalculateLoopbackRtpReceiveBufferBytes(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0) with
                    {
                        OutputSize = new Size(2880, 1620)
                    });
        int lowFpsEightKBuffer =
            FfmpegDesktopH264Capture
                .CalculateLoopbackRtpReceiveBufferBytes(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0) with
                    {
                        OutputSize = new Size(8192, 8192),
                        FramesPerSecond = 1
                    });

        Assert.Equal(
            2 * 1024 * 1024,
            FfmpegDesktopH264Capture.MinimumRtpReceiveBufferBytes);
        Assert.Equal(
            4 * 1024 * 1024,
            FfmpegDesktopH264Capture.MaximumRtpReceiveBufferBytes);
        Assert.Equal(
            FfmpegDesktopH264Capture.MinimumRtpReceiveBufferBytes,
            fullHdBuffer);
        Assert.Equal(
            FfmpegDesktopH264Capture.MinimumRtpReceiveBufferBytes,
            measuredRemoteBuffer);
        Assert.True(
            measuredRemoteBuffer > measuredMfStartupBurstBytes);
        Assert.Equal(
            FfmpegDesktopH264Capture.MaximumRtpReceiveBufferBytes,
            lowFpsEightKBuffer);
        Assert.InRange(
            lowFpsEightKBuffer,
            FfmpegDesktopH264Capture.MinimumRtpReceiveBufferBytes,
            FfmpegDesktopH264Capture.MaximumRtpReceiveBufferBytes);
    }

    [Fact]
    public async Task StartupFallsBackAcrossNativeDdaEncodersOnlyAfterCompleteAu()
    {
        var mediaFoundation = FakeFfmpegProcess.Exited(
            "Media Foundation encoder unavailable");
        var amf = FakeFfmpegProcess.Exited(
            "AMF encoder unavailable");
        var nvenc = FakeFfmpegProcess.Streaming(
            FragmentEveryByte(BuildProbeStream(0xB2)));
        var processes = new Queue<FakeFfmpegProcess>(
            [mediaFoundation, amf, nvenc]);
        var seenArguments =
            new List<IReadOnlyList<string>>();

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, arguments) =>
                {
                    seenArguments.Add(arguments);
                    return processes.Dequeue();
                },
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromSeconds(2),
                CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(result.Capture);
        Assert.True(result.Started);
        Assert.Equal("h264_nvenc", capture.EncoderName);
        Assert.Equal(
            "ddagrab/adapter0/output0",
            capture.BackendName);
        Assert.True(capture.IsRunning);
        Assert.Equal(3, seenArguments.Count);
        AssertArgumentValue(
            seenArguments[0],
            "-c:v",
            "h264_mf");
        AssertArgumentValue(
            seenArguments[1],
            "-c:v",
            "h264_amf");
        AssertArgumentValue(
            seenArguments[2],
            "-c:v",
            "h264_nvenc");

        Assert.True(capture.TryReadLatestFrame(out
            FfmpegDesktopH264Frame? frame));
        Assert.NotNull(frame);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            frame.Flags);
        Assert.Equal(0xB2, frame.AnnexBBytes.Span[^1]);
    }

    [Fact]
    public async Task
        ProductionDdaDiscoveryStopsAfterFirstTimedOutCandidate()
    {
        var encoders = new List<string>();
        var process =
            FakeFfmpegProcess.Streaming([]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, arguments) =>
                    {
                        encoders.Add(
                            GetArgumentValue(
                                arguments,
                                "-c:v"));
                        return process;
                    },
                    () => new FakeRtpReceiver(
                        port: 52019,
                        datagrams: []),
                    startupTimeout:
                        TimeSpan.FromMilliseconds(50),
                    stallTimeout:
                        TimeSpan.FromMilliseconds(200),
                    CancellationToken.None,
                    useEncoderPreferenceCache: false,
                    useEncoderSpecificStartupTimeouts: true);

        Assert.False(result.Started);
        Assert.Equal(
            ["h264_mf"],
            encoders);
        Assert.Contains(
            "No independently decodable H.264 access unit",
            result.FailureDetail);
        Assert.True(result.StartupDeadlineExpired);
        Assert.DoesNotContain(
            "h264_amf",
            result.FailureDetail);
    }

    [Fact]
    public async Task
        ProductionGop1DdaDiscoveryContinuesAfterFastUnsupportedEncoder()
    {
        var encoders = new List<string>();

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, arguments) =>
                    {
                        string encoder =
                            GetArgumentValue(
                                arguments,
                                "-c:v");
                        encoders.Add(encoder);
                        return FakeFfmpegProcess.Exited(
                            $"{encoder} unsupported");
                    },
                    () => new FakeRtpReceiver(
                        port: 52020,
                        datagrams: []),
                    startupTimeout:
                        TimeSpan.FromMilliseconds(50),
                    stallTimeout:
                        TimeSpan.FromMilliseconds(200),
                    CancellationToken.None,
                    useEncoderPreferenceCache: false,
                    useEncoderSpecificStartupTimeouts: true);

        Assert.False(result.Started);
        Assert.False(result.StartupDeadlineExpired);
        Assert.Equal(
            [
                "h264_mf",
                "h264_amf",
                "h264_nvenc",
                "h264_qsv"
            ],
            encoders);
    }

    [Fact]
    public async Task ExitedCandidateFailsImmediatelyWithStderr()
    {
        var processes = new Queue<FakeFfmpegProcess>(
        [
            FakeFfmpegProcess.Exited(
                "AMF failed: no encode device"),
            FakeFfmpegProcess.Exited(
                "Quick Sync failed: no encode device"),
            FakeFfmpegProcess.Exited(
                "OpenEncodeSessionEx failed: no encode device"),
            FakeFfmpegProcess.Exited(
                "Media Foundation encoder unavailable")
        ]);
        var stopwatch = Stopwatch.StartNew();

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0) with
                    {
                        GopLength =
                            FfmpegDesktopH264Capture
                                .ShortGopLength
                    },
                    "fake-ffmpeg.exe",
                    (_, _) => processes.Dequeue(),
                    () => new FakeRtpReceiver(
                        port: 52008,
                        datagrams: []),
                    startupTimeout: TimeSpan.FromSeconds(2),
                    stallTimeout: TimeSpan.FromSeconds(3),
                    CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.Started);
        Assert.Contains(
            "OpenEncodeSessionEx failed: no encode device",
            result.FailureDetail);
        Assert.Contains(
            "Media Foundation encoder unavailable",
            result.FailureDetail);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Exited candidates consumed {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task DdaMfFallbackGetsItsMeasuredStartupWindow()
    {
        const uint timestamp = 0x20304050;
        var processes = new Queue<FakeFfmpegProcess>(
        [
            FakeFfmpegProcess.Streaming([])
        ]);
        var receivers = new Queue<FakeRtpReceiver>(
        [
            new FakeRtpReceiver(
                52013,
                [
                    RtpPacket(
                        sequence: 200,
                        timestamp,
                        marker: false,
                        H264Nal(7, 0x64, 0x00, 0x1F)),
                    RtpPacket(
                        sequence: 201,
                        timestamp,
                        marker: false,
                        H264Nal(8, 0xEE, 0x06)),
                    RtpPacket(
                        sequence: 202,
                        timestamp,
                        marker: true,
                        H264Nal(5, 0xC8))
                ],
                initialReceiveDelay:
                    TimeSpan.FromMilliseconds(1600))
        ]);
        var encoders = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        FfmpegDesktopH264CaptureOptions options =
            CreateOptions(
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0) with
            {
                GopLength =
                    FfmpegDesktopH264Capture
                        .ShortGopLength
            };

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    options,
                    "fake-ffmpeg.exe",
                    (_, arguments) =>
                    {
                        encoders.Add(GetArgumentValue(
                            arguments,
                            "-c:v"));
                        return processes.Dequeue();
                    },
                    () => receivers.Dequeue(),
                    startupTimeout: TimeSpan.FromMilliseconds(50),
                    stallTimeout: TimeSpan.FromSeconds(3),
                    CancellationToken.None,
                    useEncoderSpecificStartupTimeouts: true);

        stopwatch.Stop();
        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        Assert.True(result.Started, result.FailureDetail);
        Assert.Equal("h264_mf", capture.EncoderName);
        Assert.Equal(
            [
                "h264_mf"
            ],
            encoders);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(1450));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(1950),
            $"DDA/MF startup consumed {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task StartupRecoversWhenNextIdrRepeatsLostConfiguration()
    {
        const uint firstTimestamp = 0x30405060;
        const uint secondTimestamp = firstTimestamp + 3000;
        var receiver = new FakeRtpReceiver(
            52011,
            [
                // Model joining the startup burst after its SPS/PPS datagrams
                // were already dropped by the bounded loopback socket.
                RtpPacket(
                    sequence: 300,
                    firstTimestamp,
                    marker: true,
                    H264Nal(5, 0xC9)),
                RtpPacket(
                    sequence: 301,
                    secondTimestamp,
                    marker: false,
                    H264Nal(7, 0x64, 0x00, 0x1F)),
                RtpPacket(
                    sequence: 302,
                    secondTimestamp,
                    marker: false,
                    H264Nal(8, 0xEE, 0x06)),
                RtpPacket(
                    sequence: 303,
                    secondTimestamp,
                    marker: true,
                    H264Nal(5, 0xCA))
            ]);
        var process = FakeFfmpegProcess.Streaming([]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        Assert.True(result.Started, result.FailureDetail);
        Assert.Equal("h264_mf", capture.EncoderName);
        Assert.True(capture.TryReadLatestFrame(
            out FfmpegDesktopH264Frame? frame));
        Assert.NotNull(frame);
        Assert.Equal(0xCA, frame.AnnexBBytes.Span[^1]);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            frame.Flags);
    }

    [Fact]
    public async Task VerifiedDdaReconnectTriesOnlyLastKnownGoodEncoder()
    {
        FfmpegDesktopH264Capture
            .ResetEncoderPreferenceCacheForTests();
        try
        {
            async Task<(
                FfmpegDesktopH264CaptureStartResult Result,
                string[] Encoders)> StartAsync(
                    params FakeFfmpegProcess[] candidates)
            {
                var pending = new Queue<FakeFfmpegProcess>(
                    candidates);
                var encoders = new List<string>();
                FfmpegDesktopH264CaptureStartResult result =
                    await FfmpegDesktopH264Capture.TryStartAsync(
                        CreateOptions(
                            FfmpegDesktopCaptureBackend
                                .DesktopDuplicationOutput0),
                        "fake-ffmpeg.exe",
                        (_, arguments) =>
                        {
                            encoders.Add(GetArgumentValue(
                                arguments,
                                "-c:v"));
                            return pending.Dequeue();
                        },
                        startupTimeout:
                            TimeSpan.FromMilliseconds(500),
                        stallTimeout: TimeSpan.FromSeconds(2),
                        CancellationToken.None,
                        useEncoderPreferenceCache: true);

                return (
                    result,
                    encoders.ToArray());
            }

            var first = await StartAsync(
                FakeFfmpegProcess.Streaming(
                    [BuildProbeStream(0xB3)]));
            using (FfmpegDesktopH264Capture firstCapture =
                Assert.IsType<FfmpegDesktopH264Capture>(
                    first.Result.Capture))
            {
                Assert.True(
                    first.Result.Started,
                    first.Result.FailureDetail);
                Assert.False(
                    first.Result
                        .UsedVerifiedEncoderFastPath);
                Assert.Equal(
                    ["h264_mf"],
                    first.Encoders);
                Assert.Equal(
                    "h264_mf",
                    firstCapture.EncoderName);
            }

            var second = await StartAsync(
                FakeFfmpegProcess.Exited(
                    "MF restarted badly"));
            Assert.False(second.Result.Started);
            Assert.True(
                second.Result
                    .UsedVerifiedEncoderFastPath);
            Assert.Equal(
                ["h264_mf"],
                second.Encoders);
            Assert.Contains(
                "MF restarted badly",
                second.Result.FailureDetail);

            var third = await StartAsync(
                FakeFfmpegProcess.Streaming(
                    [BuildProbeStream(0xB5)]));
            using (FfmpegDesktopH264Capture thirdCapture =
                Assert.IsType<FfmpegDesktopH264Capture>(
                    third.Result.Capture))
            {
                Assert.True(
                    third.Result.Started,
                    third.Result.FailureDetail);
                Assert.True(
                    third.Result
                        .UsedVerifiedEncoderFastPath);
                Assert.Equal(
                    ["h264_mf"],
                    third.Encoders);
                Assert.Equal(
                    "h264_mf",
                    thirdCapture.EncoderName);
            }
        }
        finally
        {
            FfmpegDesktopH264Capture
                .ResetEncoderPreferenceCacheForTests();
        }
    }

    [Fact]
    public async Task ChangedDdaProfileUsesPreferenceOnlyAsOrderingHint()
    {
        FfmpegDesktopH264Capture
            .ResetEncoderPreferenceCacheForTests();
        try
        {
            FfmpegDesktopH264CaptureOptions originalOptions =
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0);
            FfmpegDesktopH264CaptureStartResult first =
                await FfmpegDesktopH264Capture.TryStartAsync(
                    originalOptions,
                    "fake-ffmpeg.exe",
                    (_, _) => FakeFfmpegProcess.Streaming(
                        [BuildProbeStream(0xB6)]),
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None,
                    useEncoderPreferenceCache: true);
            using (FfmpegDesktopH264Capture firstCapture =
                Assert.IsType<FfmpegDesktopH264Capture>(
                    first.Capture))
            {
                Assert.Equal(
                    "h264_mf",
                    firstCapture.EncoderName);
            }

            var pending = new Queue<FakeFfmpegProcess>(
            [
                FakeFfmpegProcess.Exited(
                    "MF rejected changed profile"),
                FakeFfmpegProcess.Streaming(
                    [BuildProbeStream(0xB7)])
            ]);
            var encoders = new List<string>();
            FfmpegDesktopH264CaptureStartResult changed =
                await FfmpegDesktopH264Capture.TryStartAsync(
                    originalOptions with
                    {
                        OutputSize = new Size(1280, 720)
                    },
                    "fake-ffmpeg.exe",
                    (_, arguments) =>
                    {
                        encoders.Add(GetArgumentValue(
                            arguments,
                            "-c:v"));
                        return pending.Dequeue();
                    },
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None,
                    useEncoderPreferenceCache: true);

            using FfmpegDesktopH264Capture changedCapture =
                Assert.IsType<FfmpegDesktopH264Capture>(
                    changed.Capture);
            Assert.True(changed.Started, changed.FailureDetail);
            Assert.False(
                changed.UsedVerifiedEncoderFastPath);
            Assert.Equal(
                ["h264_mf", "h264_amf"],
                encoders);
            Assert.Equal(
                "h264_amf",
                changedCapture.EncoderName);
        }
        finally
        {
            FfmpegDesktopH264Capture
                .ResetEncoderPreferenceCacheForTests();
        }
    }

    [Fact]
    public async Task
        TimedOutChangedProfileClearsStaleDdaEncoderPreference()
    {
        FfmpegDesktopH264Capture
            .ResetEncoderPreferenceCacheForTests();
        try
        {
            FfmpegDesktopH264CaptureOptions originalOptions =
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0);
            var seedProcesses =
                new Queue<FakeFfmpegProcess>(
                [
                    FakeFfmpegProcess.Exited(
                        "MF unsupported"),
                    FakeFfmpegProcess.Streaming(
                        [BuildProbeStream(0xB8)])
                ]);
            FfmpegDesktopH264CaptureStartResult seed =
                await FfmpegDesktopH264Capture
                    .TryStartAsync(
                        originalOptions,
                        "fake-ffmpeg.exe",
                        (_, _) =>
                            seedProcesses.Dequeue(),
                        startupTimeout:
                            TimeSpan.FromMilliseconds(
                                500),
                        stallTimeout:
                            TimeSpan.FromSeconds(2),
                        CancellationToken.None,
                        useEncoderPreferenceCache:
                            true);
            using (FfmpegDesktopH264Capture
                   seedCapture =
                   Assert.IsType<
                       FfmpegDesktopH264Capture>(
                       seed.Capture))
            {
                Assert.Equal(
                    "h264_amf",
                    seedCapture.EncoderName);
            }

            FfmpegDesktopH264CaptureOptions
                changedOptions =
                    originalOptions with
                    {
                        OutputSize =
                            new Size(1280, 720)
                    };
            var timedOutEncoders =
                new List<string>();
            FfmpegDesktopH264CaptureStartResult
                timedOut =
                    await FfmpegDesktopH264Capture
                        .TryStartLoopbackRtpAsync(
                            changedOptions,
                            "fake-ffmpeg.exe",
                            (_, arguments) =>
                            {
                                timedOutEncoders.Add(
                                    GetArgumentValue(
                                        arguments,
                                        "-c:v"));
                                return
                                    FakeFfmpegProcess
                                        .Streaming([]);
                            },
                            () =>
                                new FakeRtpReceiver(
                                    port: 52021,
                                    datagrams: []),
                            startupTimeout:
                                TimeSpan
                                    .FromMilliseconds(
                                        50),
                            stallTimeout:
                                TimeSpan
                                    .FromMilliseconds(
                                        200),
                            CancellationToken.None,
                            useEncoderPreferenceCache:
                                true,
                            useEncoderSpecificStartupTimeouts:
                                true);

            Assert.False(timedOut.Started);
            Assert.Equal(
                ["h264_amf"],
                timedOutEncoders);
            Assert.Contains(
                "No independently decodable H.264 access unit",
                timedOut.FailureDetail);

            var retryEncoders =
                new List<string>();
            FfmpegDesktopH264CaptureStartResult retry =
                await FfmpegDesktopH264Capture
                    .TryStartAsync(
                        changedOptions,
                        "fake-ffmpeg.exe",
                        (_, arguments) =>
                        {
                            retryEncoders.Add(
                                GetArgumentValue(
                                    arguments,
                                    "-c:v"));
                            return FakeFfmpegProcess
                                .Streaming(
                                    [BuildProbeStream(
                                        0xB9)]);
                        },
                        startupTimeout:
                            TimeSpan.FromMilliseconds(
                                500),
                        stallTimeout:
                            TimeSpan.FromSeconds(2),
                        CancellationToken.None,
                        useEncoderPreferenceCache:
                            true);
            using FfmpegDesktopH264Capture
                retryCapture =
                    Assert.IsType<
                        FfmpegDesktopH264Capture>(
                        retry.Capture);
            Assert.Equal(
                ["h264_mf"],
                retryEncoders);
            Assert.Equal(
                "h264_mf",
                retryCapture.EncoderName);
        }
        finally
        {
            FfmpegDesktopH264Capture
                .ResetEncoderPreferenceCacheForTests();
        }
    }

    [Fact]
    public async Task LoopbackRtpPublishesAtMarkerWithoutFollowingPacket()
    {
        const uint timestamp = 0x10203040;
        var receiver = new FakeRtpReceiver(
            port: 52000,
            [
                RtpPacket(
                    sequence: 100,
                    timestamp,
                    marker: false,
                    H264Nal(7, 0x64, 0x00, 0x1F)),
                RtpPacket(
                    sequence: 101,
                    timestamp,
                    marker: false,
                    H264Nal(8, 0xEE, 0x06)),
                RtpPacket(
                    sequence: 102,
                    timestamp,
                    marker: true,
                    H264Nal(5, 0xA7))
            ]);
        var process = FakeFfmpegProcess.Streaming(
            ["ignored stdout"u8.ToArray()]);
        IReadOnlyList<string>? seenArguments = null;

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, arguments) =>
                    {
                        seenArguments = arguments;
                        return process;
                    },
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        FfmpegDesktopH264Frame? frame =
            await capture.ReadLatestFrameAsync(
                CancellationToken.None);

        Assert.True(result.Started, result.FailureDetail);
        Assert.NotNull(frame);
        Assert.Equal(3, receiver.DatagramsReceived);
        Assert.False(receiver.ReceiveStartedOnThreadPool);
        Assert.Equal(0xA7, frame.AnnexBBytes.Span[^1]);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            frame.Flags);
        Assert.True(frame.ProducedAtTimestamp > 0);
        Assert.InRange(
            frame.Age,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));
        Assert.NotNull(seenArguments);
        AssertArgumentValue(seenArguments, "-f", "tee");
        Assert.Equal(
            "[f=rtp:payload_type=96:rtpflags=skip_rtcp:" +
                "onfail=ignore]rtp://127.0.0.1:52000?" +
                "pkt_size=16384&connect=1&localaddr=127.0.0.1|" +
                "[f=h264]pipe:1",
            seenArguments[^1]);
        Assert.True(
            SpinWait.SpinUntil(
                () => process.StandardOutputBytesRead ==
                    "ignored stdout"u8.Length,
                TimeSpan.FromSeconds(1)),
            "RTP mode must continue draining redirected stdout.");
    }

    [Fact]
    public async Task LoopbackRtpReaderOwnsLargePooledFrameUntilDisposed()
    {
        var pool = new TrackingByteArrayPool();
        const uint timestamp = 0x11223344;
        ushort sequence = 300;
        var datagrams = new List<byte[]>
        {
            RtpPacket(
                sequence++,
                timestamp,
                marker: false,
                H264Nal(7, 0x64, 0x00, 0x1F)),
            RtpPacket(
                sequence++,
                timestamp,
                marker: false,
                H264Nal(8, 0xEE, 0x06))
        };
        byte[] idr = H264Nal(
            5,
            Enumerable.Repeat(
                (byte)0xA5,
                H264RtpAccessUnitAssembler
                    .PooledAccessUnitMinimumBytes +
                8192)
                .ToArray());
        datagrams.AddRange(FragmentRtpNal(
            ref sequence,
            timestamp,
            idr));
        var receiver = new FakeRtpReceiver(
            port: 52015,
            datagrams);
        var process = FakeFfmpegProcess.Streaming([]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None,
                    accessUnitPool: pool);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        FfmpegDesktopH264Frame frame =
            Assert.IsType<FfmpegDesktopH264Frame>(
                await capture.ReadLatestFrameAsync(
                    CancellationToken.None));

        Assert.True(result.Started, result.FailureDetail);
        Assert.True(frame.OwnsPooledBuffer);
        Assert.Equal(idr[^1], frame.AnnexBBytes.Span[^1]);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);

        frame.Dispose();
        Assert.True(frame.IsDisposed);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
        frame.Dispose();
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public async Task LoopbackRtpReturnsReplacedAndClosedMailboxFramesToPool()
    {
        var pool = new TrackingByteArrayPool();
        ushort sequence = 500;
        const uint firstTimestamp = 0x22334455;
        var firstDatagrams = new List<byte[]>
        {
            RtpPacket(
                sequence++,
                firstTimestamp,
                marker: false,
                H264Nal(7, 0x64, 0x00, 0x1F)),
            RtpPacket(
                sequence++,
                firstTimestamp,
                marker: false,
                H264Nal(8, 0xEE, 0x06))
        };
        byte[] firstIdr = H264Nal(
            5,
            Enumerable.Repeat(
                (byte)0xB5,
                H264RtpAccessUnitAssembler
                    .PooledAccessUnitMinimumBytes +
                4096)
                .ToArray());
        firstDatagrams.AddRange(FragmentRtpNal(
            ref sequence,
            firstTimestamp,
            firstIdr));
        var receiver = new FakeRtpReceiver(
            port: 52016,
            firstDatagrams);
        var process = FakeFfmpegProcess.Streaming([]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None,
                    accessUnitPool: pool);

        FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        Assert.True(result.Started, result.FailureDetail);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);

        byte[] secondIdr = H264Nal(
            5,
            Enumerable.Repeat(
                (byte)0xC5,
                H264RtpAccessUnitAssembler
                    .PooledAccessUnitMinimumBytes +
                6144)
                .ToArray());
        byte[][] secondDatagrams = FragmentRtpNal(
            ref sequence,
            firstTimestamp + 3000,
            secondIdr);
        int expectedDatagrams =
            receiver.DatagramsReceived +
            secondDatagrams.Length;
        receiver.Enqueue(secondDatagrams);

        Assert.True(
            SpinWait.SpinUntil(
                () =>
                    receiver.DatagramsReceived ==
                        expectedDatagrams &&
                    pool.RentCount == 2 &&
                    pool.ReturnCount == 1,
                TimeSpan.FromSeconds(1)),
            "The latest-only mailbox did not return its replaced pooled frame.");
        Assert.Equal(1, pool.ActiveCount);

        capture.Dispose();
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
        capture.Dispose();
        Assert.Equal(2, pool.ReturnCount);
    }

    [Fact]
    public async Task LoopbackRtpReturnsLargeUnsafeStartupUnitBeforeRecovery()
    {
        var pool = new TrackingByteArrayPool();
        ushort sequence = 700;
        const uint unsafeTimestamp = 0x33445566;
        byte[] unsafeDependent = H264Nal(
            1,
            Enumerable.Repeat(
                (byte)0xD5,
                H264RtpAccessUnitAssembler
                    .PooledAccessUnitMinimumBytes +
                2048)
                .ToArray());
        var datagrams = new List<byte[]>(
            FragmentRtpNal(
                ref sequence,
                unsafeTimestamp,
                unsafeDependent));

        const uint recoveryTimestamp =
            unsafeTimestamp + 3000;
        datagrams.Add(RtpPacket(
            sequence++,
            recoveryTimestamp,
            marker: false,
            H264Nal(7, 0x64, 0x00, 0x1F)));
        datagrams.Add(RtpPacket(
            sequence++,
            recoveryTimestamp,
            marker: false,
            H264Nal(8, 0xEE, 0x06)));
        byte[] recoveryIdr = H264Nal(
            5,
            Enumerable.Repeat(
                (byte)0xE5,
                H264RtpAccessUnitAssembler
                    .PooledAccessUnitMinimumBytes +
                3072)
                .ToArray());
        datagrams.AddRange(FragmentRtpNal(
            ref sequence,
            recoveryTimestamp,
            recoveryIdr));
        var receiver = new FakeRtpReceiver(
            port: 52017,
            datagrams);
        var process = FakeFfmpegProcess.Streaming([]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None,
                    accessUnitPool: pool);

        FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        Assert.True(result.Started, result.FailureDetail);
        Assert.Equal(2, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(1, pool.ActiveCount);

        capture.Dispose();
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public async Task LoopbackRtpStartsFromRawTeeWhenRtpIsSilent()
    {
        var receiver = new FakeRtpReceiver(
            port: 52012,
            datagrams: []);
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xA9)]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        FfmpegDesktopH264Frame? frame =
            await capture.ReadLatestFrameAsync(
                CancellationToken.None);

        Assert.True(result.Started, result.FailureDetail);
        Assert.NotNull(frame);
        Assert.Equal(0xA9, frame.AnnexBBytes.Span[^1]);
        Assert.Equal(0, receiver.DatagramsReceived);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            frame.Flags);
    }

    [Fact]
    public async Task LoopbackRtpHealthyStdoutDrainSkipsRawParser()
    {
        const uint timestamp = 0x20304050;
        var receiver = new FakeRtpReceiver(
            port: 52013,
            [
                RtpPacket(
                    sequence: 100,
                    timestamp,
                    marker: false,
                    H264Nal(7, 0x64, 0x00, 0x1F)),
                RtpPacket(
                    sequence: 101,
                    timestamp,
                    marker: false,
                    H264Nal(8, 0xEE, 0x06)),
                RtpPacket(
                    sequence: 102,
                    timestamp,
                    marker: true,
                    H264Nal(5, 0xB1))
            ]);
        var process = FakeFfmpegProcess.Streaming([]);
        FfmpegDesktopH264CaptureOptions options =
            CreateOptions(
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0) with
            {
                FramesPerSecond = 1
            };

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    options,
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(3),
                    CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        FfmpegDesktopH264Frame? rtpFrame =
            await capture.ReadLatestFrameAsync(
                CancellationToken.None);
        Assert.NotNull(rtpFrame);
        Assert.Equal(0xB1, rtpFrame.AnnexBBytes.Span[^1]);
        Assert.False(capture.RawFallbackParserActive);
        Assert.Equal(0, capture.RawFallbackParserGeneration);

        byte[] duplicateRaw = BuildProbeStream(0xB2);
        process.EnqueueStandardOutput(duplicateRaw);
        Assert.True(
            SpinWait.SpinUntil(
                () => process.StandardOutputBytesRead ==
                    duplicateRaw.Length,
                TimeSpan.FromSeconds(1)),
            "The healthy RTP path stopped draining stdout.");

        Assert.True(result.Started, result.FailureDetail);
        Assert.True(capture.IsRunning, capture.FailureDetail);
        Assert.False(capture.RawFallbackParserActive);
        Assert.Equal(0, capture.RawFallbackParserGeneration);
        Assert.False(capture.TryReadLatestFrame(out _));
    }

    [Fact]
    public async Task LoopbackRtpSilenceRebuildsRawParserAndRecoveryReleasesIt()
    {
        const uint firstTimestamp = 0x30405060;
        var receiver = new FakeRtpReceiver(
            port: 52014,
            [
                RtpPacket(
                    sequence: 200,
                    firstTimestamp,
                    marker: false,
                    H264Nal(7, 0x64, 0x00, 0x1F)),
                RtpPacket(
                    sequence: 201,
                    firstTimestamp,
                    marker: false,
                    H264Nal(8, 0xEE, 0x06)),
                RtpPacket(
                    sequence: 202,
                    firstTimestamp,
                    marker: true,
                    H264Nal(5, 0xC1))
            ]);
        var process = FakeFfmpegProcess.Streaming([]);
        FfmpegDesktopH264CaptureOptions options =
            CreateOptions(
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0) with
            {
                FramesPerSecond = 5
            };

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    options,
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(3),
                    CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        FfmpegDesktopH264Frame? firstRtpFrame =
            await capture.ReadLatestFrameAsync(
                CancellationToken.None);
        Assert.NotNull(firstRtpFrame);
        Assert.Equal(0xC1, firstRtpFrame.AnnexBBytes.Span[^1]);
        Assert.False(capture.RawFallbackParserActive);

        // This unterminated AU is drained while RTP is healthy. It must not
        // survive into the parser generation created after RTP goes silent.
        byte[] discardedPartial = BuildIndependentAu(0xC2);
        process.EnqueueStandardOutput(discardedPartial);
        Assert.True(
            SpinWait.SpinUntil(
                () => process.StandardOutputBytesRead ==
                    discardedPartial.Length,
                TimeSpan.FromSeconds(1)),
            "The healthy RTP path did not drain the partial stdout AU.");
        Assert.False(capture.RawFallbackParserActive);

        await Task.Delay(
            FfmpegDesktopH264Capture
                .CalculateRtpFallbackSilence(
                    options.FramesPerSecond) +
                TimeSpan.FromMilliseconds(100));
        byte[] resyncAud = Nal(9, 0xF0);
        process.EnqueueStandardOutput(resyncAud);
        long resyncBytes =
            discardedPartial.Length +
            resyncAud.Length;
        Assert.True(
            SpinWait.SpinUntil(
                () => process.StandardOutputBytesRead ==
                    resyncBytes &&
                    capture.RawFallbackParserActive,
                TimeSpan.FromSeconds(1)),
            "RTP silence did not create a fresh stdout parser.");
        Assert.Equal(1, capture.RawFallbackParserGeneration);
        Assert.False(capture.TryReadLatestFrame(out _));

        byte[] rawRecovery = BuildProbeStream(0xC3);
        process.EnqueueStandardOutput(rawRecovery);
        FfmpegDesktopH264Frame? fallbackFrame =
            await capture.ReadLatestFrameAsync(
                    CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(fallbackFrame);
        Assert.Equal(0xC3, fallbackFrame.AnnexBBytes.Span[^1]);
        Assert.DoesNotContain(
            (byte)0xC2,
            fallbackFrame.AnnexBBytes.ToArray());
        Assert.True(capture.RawFallbackParserActive);

        const uint recoveredTimestamp =
            firstTimestamp + 3000;
        receiver.Enqueue(
            RtpPacket(
                sequence: 203,
                recoveredTimestamp,
                marker: false,
                H264Nal(7, 0x64, 0x00, 0x1F)),
            RtpPacket(
                sequence: 204,
                recoveredTimestamp,
                marker: false,
                H264Nal(8, 0xEE, 0x06)),
            RtpPacket(
                sequence: 205,
                recoveredTimestamp,
                marker: true,
                H264Nal(5, 0xC4)));
        FfmpegDesktopH264Frame? recoveredRtpFrame =
            await capture.ReadLatestFrameAsync(
                    CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotNull(recoveredRtpFrame);
        Assert.Equal(
            0xC4,
            recoveredRtpFrame.AnnexBBytes.Span[^1]);
        Assert.True(
            SpinWait.SpinUntil(
                () => !capture.RawFallbackParserActive,
                TimeSpan.FromSeconds(1)),
            "RTP recovery did not release the fallback parser.");
        Assert.Equal(1, capture.RawFallbackParserGeneration);
        Assert.True(result.Started, result.FailureDetail);
        Assert.True(capture.IsRunning, capture.FailureDetail);
    }

    [Fact]
    public async Task LoopbackRtpDrainsMfStartupBurstBeforePublishingLatest()
    {
        const uint firstTimestamp = 0x10203040;
        var datagrams = new List<byte[]>
        {
            RtpPacket(
                sequence: 100,
                firstTimestamp,
                marker: false,
                H264Nal(7, 0x64, 0x00, 0x1F)),
            RtpPacket(
                sequence: 101,
                firstTimestamp,
                marker: false,
                H264Nal(8, 0xEE, 0x06)),
            RtpPacket(
                sequence: 102,
                firstTimestamp,
                marker: true,
                H264Nal(5, 0xA0))
        };
        for (int frameIndex = 1; frameIndex < 9; frameIndex++)
        {
            datagrams.Add(
                RtpPacket(
                    sequence: checked((ushort)(102 + frameIndex)),
                    timestamp: checked(
                        firstTimestamp +
                        (uint)(frameIndex * 3000)),
                    marker: true,
                    H264Nal(
                        5,
                        checked((byte)(0xA0 + frameIndex)))));
        }

        var receiver = new FakeRtpReceiver(
            port: 52003,
            datagrams);
        var process = FakeFfmpegProcess.Streaming([]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, _) => process,
                    () => receiver,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        FfmpegDesktopH264Frame? frame =
            await capture.ReadLatestFrameAsync(
                CancellationToken.None);

        Assert.True(result.Started, result.FailureDetail);
        Assert.NotNull(frame);
        Assert.Equal(11, receiver.DatagramsReceived);
        Assert.Equal(0xA8, frame.AnnexBBytes.Span[^1]);
        Assert.DoesNotContain(
            (byte)0xA0,
            frame.AnnexBBytes.ToArray());
        Assert.False(capture.TryReadLatestFrame(out _));
    }

    [Fact]
    public async Task LoopbackRtpRejectsDatagramsAbovePrivateLoopbackLimit()
    {
        var receivers = new Queue<FakeRtpReceiver>(
        [
            new FakeRtpReceiver(
                52001,
                [
                    new byte[
                        FfmpegDesktopH264Capture
                            .LoopbackRtpPacketSizeBytes +
                        1]
                ]),
            new FakeRtpReceiver(
                52002,
                [
                    new byte[
                        FfmpegDesktopH264Capture
                            .LoopbackRtpPacketSizeBytes +
                        1]
                ])
        ]);
        FakeRtpReceiver[] allReceivers = receivers.ToArray();
        var processes = new Queue<FakeFfmpegProcess>(
        [
            FakeFfmpegProcess.Streaming([]),
            FakeFfmpegProcess.Streaming([])
        ]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture
                .TryStartLoopbackRtpAsync(
                    CreateOptions(
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0),
                    "fake-ffmpeg.exe",
                    (_, _) => processes.Dequeue(),
                    () => receivers.Dequeue(),
                    startupTimeout:
                        TimeSpan.FromMilliseconds(500),
                    stallTimeout: TimeSpan.FromSeconds(2),
                    CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("16384-byte", result.FailureDetail);
        Assert.Contains("private loopback", result.FailureDetail);
        Assert.Contains("h264_nvenc", result.FailureDetail);
        Assert.Contains("h264_mf", result.FailureDetail);
        Assert.All(
            allReceivers,
            receiver => Assert.True(receiver.IsDisposed));
    }

    [Fact]
    public async Task StartupLeavesOnlyLatestCompletedFrameInMailbox()
    {
        byte[] stream = Join(
            BuildIndependentAu(0xC1),
            BuildIndependentAu(0xC2),
            BuildIndependentAu(0xC3),
            Nal(9, 0xF0));
        var process = FakeFfmpegProcess.Streaming([stream]);

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromSeconds(2),
                CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(result.Capture);
        FfmpegDesktopH264Frame? frame =
            await capture.ReadLatestFrameAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(0xC3, frame.AnnexBBytes.Span[^1]);
        Assert.False(capture.TryReadLatestFrame(out _));
    }

    [Fact]
    public async Task StallCompletesCaptureAndKillsProcessTree()
    {
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xD1)]);
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromMilliseconds(80),
                CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(result.Capture);
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(capture.IsStalled);
        Assert.False(capture.IsRunning);
        Assert.True(process.KillCalled);
        Assert.Contains(
            "stalled",
            capture.FailureDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(capture.TryReadLatestFrame(out _));
        Assert.Null(await capture.ReadLatestFrameAsync(
            CancellationToken.None));
        capture.Dispose();
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task StaticWgcSilenceStaysAliveButStillDetectsProcessExit()
    {
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xD7)]);
        FfmpegDesktopH264CaptureOptions options =
            CreateGraphicsCaptureOptions(
                new Rectangle(0, 0, 3840, 2160),
                FfmpegDesktopH264Capture.NvidiaVendorId) with
            {
                AllowStaticFrameSilence = true
            };
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                options,
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromMilliseconds(60),
                CancellationToken.None);

        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);
        await Task.Delay(TimeSpan.FromMilliseconds(240));

        Assert.True(capture.IsStalled);
        Assert.True(capture.IsRunning);
        Assert.False(capture.Completion.IsCompleted);
        Assert.False(process.KillCalled);

        process.CompleteExit();
        await capture.Completion.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.False(capture.IsRunning);
        Assert.Contains(
            "process stopped",
            capture.FailureDetail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaticFrameSilenceCannotDisableOtherBackendWatchdogs()
    {
        FfmpegDesktopH264CaptureOptions options = CreateOptions(
            FfmpegDesktopCaptureBackend.GdiGrabBounds) with
        {
            AllowStaticFrameSilence = true
        };

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => FfmpegDesktopH264Capture.BuildArguments(
                options,
                FfmpegH264Encoder.NvidiaNvenc));

        Assert.Contains(
            "only by Windows Graphics Capture",
            error.Message);
    }

    [Fact]
    public async Task FailedCaptureSkipsGracefulExitAndUsesShortReaperHandoff()
    {
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xD5)],
            exitOnKill: false);
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout:
                    TimeSpan.FromMilliseconds(500),
                stallTimeout:
                    TimeSpan.FromMilliseconds(60),
                CancellationToken.None);
        FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);

        await capture.Completion.WaitAsync(
            TimeSpan.FromSeconds(2));
        capture.Dispose();

        Assert.True(process.KillCalled);
        Assert.False(process.GracefulExitRequested);
        Assert.Equal(
            [
                FfmpegDesktopH264Capture
                    .FailedCaptureExitWaitMilliseconds
            ],
            process.WaitForExitCalls);
        Assert.False(process.DisposeCalled);

        process.CompleteExit();
        await process.Disposed.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task CancelledStartupUsesFailedCaptureFastShutdown()
    {
        var process = FakeFfmpegProcess.Streaming(
            [],
            exitOnKill: false);
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromSeconds(5),
                stallTimeout: TimeSpan.FromSeconds(2),
                timeout.Token));

        Assert.True(process.KillCalled);
        Assert.False(process.GracefulExitRequested);
        Assert.Contains(
            FfmpegDesktopH264Capture
                .FailedCaptureExitWaitMilliseconds,
            process.WaitForExitCalls);
        Assert.DoesNotContain(
            FfmpegDesktopH264Capture
                .GracefulExitWaitMilliseconds,
            process.WaitForExitCalls);

        process.CompleteExit();
        await process.Disposed.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task DisposeRequestsGracefulExitBeforeForcedTermination()
    {
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xD2)],
            readerCancellationLag: TimeSpan.FromSeconds(2),
            waitForExitDelay: TimeSpan.FromMilliseconds(600));
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromSeconds(2),
                CancellationToken.None);
        FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);

        var stopwatch = Stopwatch.StartNew();
        capture.Dispose();
        stopwatch.Stop();

        Assert.True(process.GracefulExitRequested);
        Assert.False(process.KillCalled);
        Assert.Contains(
            process.WaitForExitCalls,
            milliseconds =>
                milliseconds ==
                    FfmpegDesktopH264Capture
                        .GracefulExitWaitMilliseconds);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(1800),
            $"Dispose took {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
    }

    [Fact]
    public async Task
        GraphicsCaptureDisposeSkipsUnstablePluginGracefulUnload()
    {
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xD6)]);
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateGraphicsCaptureOptions(
                    new Rectangle(0, 0, 3840, 2160),
                    FfmpegDesktopH264Capture.NvidiaVendorId),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromSeconds(2),
                CancellationToken.None);
        FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);

        capture.Dispose();

        Assert.False(process.GracefulExitRequested);
        Assert.True(process.KillCalled);
        Assert.Equal(
            [
                FfmpegDesktopH264Capture
                    .ForcedExitWaitMilliseconds
            ],
            process.WaitForExitCalls);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task GracefulExitTimeoutForcesKillAndConfirmsExit()
    {
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xD3)],
            waitForExitDelay: TimeSpan.FromMilliseconds(20),
            exitOnGracefulRequest: false);
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromSeconds(2),
                CancellationToken.None);
        FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);

        capture.Dispose();

        Assert.True(process.GracefulExitRequested);
        Assert.True(process.KillCalled);
        Assert.Equal(
            [
                FfmpegDesktopH264Capture
                    .GracefulExitWaitMilliseconds,
                FfmpegDesktopH264Capture
                    .ForcedExitWaitMilliseconds
            ],
            process.WaitForExitCalls);
        Assert.True(process.DisposeCalled);
    }

    [Fact]
    public async Task UnconfirmedForcedExitRetainsHandleUntilReaperObservesExit()
    {
        var process = FakeFfmpegProcess.Streaming(
            [BuildProbeStream(0xD4)],
            exitOnGracefulRequest: false,
            exitOnKill: false);
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                "fake-ffmpeg.exe",
                (_, _) => process,
                startupTimeout: TimeSpan.FromMilliseconds(500),
                stallTimeout: TimeSpan.FromSeconds(2),
                CancellationToken.None);
        FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(
                result.Capture);

        capture.Dispose();

        Assert.True(process.GracefulExitRequested);
        Assert.True(process.KillCalled);
        Assert.False(process.DisposeCalled);

        process.CompleteExit();
        await process.Disposed.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.True(process.DisposeCalled);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task AllCandidateFailuresReturnDiagnosticsWithoutGpuDependency()
    {
        int starts = 0;
        var processes = new List<FakeFfmpegProcess>();

        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend.GdiGrabBounds),
                "fake-ffmpeg.exe",
                (_, _) =>
                {
                    starts++;
                    FakeFfmpegProcess process =
                        FakeFfmpegProcess.Exited(
                        $"failure-{starts}");
                    processes.Add(process);
                    return process;
                },
                startupTimeout: TimeSpan.FromMilliseconds(100),
                stallTimeout: TimeSpan.FromMilliseconds(200),
                CancellationToken.None);

        Assert.False(result.Started);
        Assert.Null(result.Capture);
        Assert.Equal(4, starts);
        Assert.Contains(
            "hardware encoder",
            result.FailureDetail);
        Assert.Contains("h264_nvenc", result.FailureDetail);
        Assert.Contains("h264_mf", result.FailureDetail);
        Assert.Contains("h264_qsv", result.FailureDetail);
        Assert.Contains("h264_amf", result.FailureDetail);
        Assert.All(
            processes,
            process => Assert.True(process.DisposeCalled));
    }

    [Fact]
    public async Task MissingFfmpegAndInvalidOptionsFailWithoutStartingProcess()
    {
        int starts = 0;
        FfmpegDesktopH264CaptureStartResult missing =
            await FfmpegDesktopH264Capture.TryStartAsync(
                CreateOptions(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0),
                ffmpegPath: null,
                (_, _) =>
                {
                    starts++;
                    return FakeFfmpegProcess.Exited("");
                },
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
        var invalidOptions = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .DesktopDuplicationOutput0,
            new Rectangle(0, 0, 1920, 1080),
            new Size(1279, 720),
            30,
            DesktopDuplicationTarget:
                CreateDesktopDuplicationTarget(
                    new Rectangle(0, 0, 1920, 1080)));
        FfmpegDesktopH264CaptureStartResult invalid =
            await FfmpegDesktopH264Capture.TryStartAsync(
                invalidOptions,
                "fake-ffmpeg.exe",
                (_, _) =>
                {
                    starts++;
                    return FakeFfmpegProcess.Exited("");
                },
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);

        Assert.False(missing.Started);
        Assert.Contains("unavailable", missing.FailureDetail);
        Assert.False(invalid.Started);
        Assert.Contains("even", invalid.FailureDetail);
        Assert.Equal(0, starts);
    }

    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public async Task OptionalLocalDdaHardwareSmoke()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "REMOTEDESK_FFMPEG_CAPTURE_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(
            RemoteHostServer.HardwareH264ProbeDeadline +
                TimeSpan.FromSeconds(1));
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .DesktopDuplicationOutput0,
            new Rectangle(0, 0, 3840, 2160),
            new Size(1280, 720),
            FramesPerSecond: 30,
            GopLength:
                FfmpegDesktopH264Capture.ShortGopLength,
            DesktopDuplicationTarget:
                CreateDesktopDuplicationTarget(
                    new Rectangle(0, 0, 3840, 2160)));
        FfmpegDesktopH264CaptureStartResult result =
            await FfmpegDesktopH264Capture.TryStartAsync(
                options,
                timeout.Token);

        Assert.True(result.Started, result.FailureDetail);
        using FfmpegDesktopH264Capture capture =
            Assert.IsType<FfmpegDesktopH264Capture>(result.Capture);
        Assert.Contains(
            capture.EncoderName,
            new[]
            {
                "h264_amf",
                "h264_qsv",
                "h264_nvenc",
                "h264_mf"
            });

        long previousProducedAt = 0;
        bool dependentFrameAllowed = false;
        int dependentFrames = 0;
        for (int frameIndex = 0; frameIndex < 5; frameIndex++)
        {
            FfmpegDesktopH264Frame? frame =
                await capture.ReadLatestFrameAsync(timeout.Token);

            Assert.NotNull(frame);
            Assert.Equal(1280, frame.Width);
            Assert.Equal(720, frame.Height);
            bool recovery =
                frame.Flags.HasFlag(
                    RemoteFrameFlags.KeyFrame) &&
                frame.Flags.HasFlag(
                    RemoteFrameFlags.CodecConfig);
            if (recovery)
            {
                dependentFrameAllowed = true;
            }
            else
            {
                Assert.True(
                    dependentFrameAllowed,
                    "GOP=2 emitted two dependent access units.");
                dependentFrameAllowed = false;
                dependentFrames++;
            }
            Assert.True(
                frame.ProducedAtTimestamp > previousProducedAt);
            Assert.InRange(
                frame.Age,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2));
            Assert.InRange(
                frame.AnnexBBytes.Length,
                1,
                H264RtpAccessUnitAssembler
                    .DefaultMaxAccessUnitBytes);
            previousProducedAt = frame.ProducedAtTimestamp;
        }

        Assert.True(
            dependentFrames > 0,
            "The hardware encoder never emitted a negotiated dependent P frame.");
    }

    private static FfmpegDesktopH264CaptureOptions CreateOptions(
        FfmpegDesktopCaptureBackend backend)
    {
        Rectangle bounds = new(0, 0, 3840, 2160);
        return new FfmpegDesktopH264CaptureOptions(
            backend,
            bounds,
            new Size(1920, 1080),
            FramesPerSecond: 30,
            DesktopDuplicationTarget:
                backend == FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0
                    ? CreateDesktopDuplicationTarget(bounds)
                    : null);
    }

    private static FfmpegDesktopH264CaptureOptions
        CreateGraphicsCaptureOptions(
            Rectangle bounds,
            uint vendorId)
    {
        WindowsGraphicsCaptureTarget target =
            CreateGraphicsCaptureTarget(
                bounds,
                vendorId);
        return new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor,
            bounds,
            bounds.Size,
            FramesPerSecond: 30,
            GraphicsCaptureTarget: target);
    }

    private static WindowsGraphicsCaptureTarget
        CreateGraphicsCaptureTarget(
            Rectangle bounds,
            uint vendorId =
                FfmpegDesktopH264Capture.AmdVendorId)
    {
        return new WindowsGraphicsCaptureTarget(
            MonitorIndex: 0,
            AdapterIndex: 0,
            AdapterVendorId: vendorId,
            AdapterDescription: "Test adapter",
            DeviceName: @"\\.\DISPLAY5",
            Bounds: bounds);
    }

    private static WindowsDesktopDuplicationTarget
        CreateDesktopDuplicationTarget(
            Rectangle bounds,
            int adapterIndex = 0,
            int outputIndex = 0)
    {
        return new WindowsDesktopDuplicationTarget(
            AdapterIndex: adapterIndex,
            OutputIndex: outputIndex,
            AdapterVendorId:
                FfmpegDesktopH264Capture.AmdVendorId,
            AdapterDescription: "Test display adapter",
            DeviceName: @"\\.\DISPLAY2",
            Bounds: bounds);
    }

    private static void AssertArgumentValue(
        IReadOnlyList<string> arguments,
        string name,
        string expected)
    {
        Assert.Equal(
            expected,
            GetArgumentValue(arguments, name));
    }

    private static string GetArgumentValue(
        IReadOnlyList<string> arguments,
        string name)
    {
        int index = arguments.IndexOf(name);
        Assert.True(index >= 0, $"Missing argument {name}.");
        Assert.True(
            index + 1 < arguments.Count,
            $"Missing value for argument {name}.");
        return arguments[index + 1];
    }

    private static IEnumerable<byte[]> FragmentEveryByte(byte[] bytes)
    {
        return bytes.Select(value => new[] { value });
    }

    private static byte[] BuildProbeStream(byte marker)
    {
        return Join(
            BuildIndependentAu(marker),
            Nal(9, 0xF0));
    }

    private static byte[] BuildIndependentAu(byte marker)
    {
        return Join(
            Nal(9, 0xF0),
            Nal(7, 0x64, 0x00, 0x1F),
            Nal(8, 0xEE, 0x06),
            Nal(5, marker));
    }

    private static byte[] RtpPacket(
        ushort sequence,
        uint timestamp,
        bool marker,
        byte[] payload)
    {
        byte[] packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)(
            FfmpegDesktopH264Capture.RtpPayloadType |
            (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(2, 2),
            sequence);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(4, 4),
            timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(8, 4),
            0x55667788);
        payload.CopyTo(packet, 12);
        return packet;
    }

    private static byte[] H264Nal(
        int type,
        params byte[] payload)
    {
        byte[] nal = new byte[1 + payload.Length];
        nal[0] = (byte)(
            type is 5 or 7 or 8
                ? 0x60 | type
                : type);
        payload.CopyTo(nal, 1);
        return nal;
    }

    private static byte[][] FragmentRtpNal(
        ref ushort sequence,
        uint timestamp,
        byte[] nal)
    {
        const int fragmentBytes = 12 * 1024;
        var datagrams = new List<byte[]>();
        int offset = 1;
        while (offset < nal.Length)
        {
            int length = Math.Min(
                fragmentBytes,
                nal.Length - offset);
            bool startsNal = offset == 1;
            bool endsNal = offset + length == nal.Length;
            byte[] payload = new byte[
                2 +
                length];
            payload[0] = (byte)(
                (nal[0] & 0xE0) |
                28);
            payload[1] = (byte)(
                (startsNal ? 0x80 : 0) |
                (endsNal ? 0x40 : 0) |
                (nal[0] & 0x1F));
            nal.AsSpan(offset, length)
                .CopyTo(payload.AsSpan(2));
            datagrams.Add(
                RtpPacket(
                    sequence++,
                    timestamp,
                    marker: endsNal,
                    payload));
            offset += length;
        }

        return datagrams.ToArray();
    }

    private static byte[] Nal(int type, params byte[] payload)
    {
        byte[] result = new byte[4 + 1 + payload.Length];
        result[2] = 1;
        result[3] = (byte)(
            type switch
            {
                5 or 7 or 8 => 0x60 | type,
                _ => type
            });
        payload.CopyTo(result, 4);
        return result;
    }

    private static byte[] Join(params byte[][] arrays)
    {
        byte[] result = new byte[arrays.Sum(array => array.Length)];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            array.CopyTo(result, offset);
            offset += array.Length;
        }

        return result;
    }

    private sealed class FakeRtpReceiver :
        IFfmpegDesktopH264RtpReceiver
    {
        private readonly ConcurrentQueue<byte[]> _datagrams;
        private readonly SemaphoreSlim _datagramSignal;
        private readonly TimeSpan _initialReceiveDelay;
        private int _initialReceiveDelayPending;
        private int _disposed;

        public FakeRtpReceiver(
            int port,
            IEnumerable<byte[]> datagrams,
            TimeSpan initialReceiveDelay = default)
        {
            Port = port;
            byte[][] initialDatagrams =
                datagrams
                    .Select(datagram => datagram.ToArray())
                    .ToArray();
            _datagrams =
                new ConcurrentQueue<byte[]>(
                    initialDatagrams);
            _datagramSignal =
                new SemaphoreSlim(
                    initialDatagrams.Length);
            _initialReceiveDelay = initialReceiveDelay;
            _initialReceiveDelayPending =
                initialReceiveDelay > TimeSpan.Zero ? 1 : 0;
        }

        public int Port { get; }

        public bool HasPendingDatagrams =>
            !_datagrams.IsEmpty;

        public int DatagramsReceived { get; private set; }

        public bool ReceiveStartedOnThreadPool { get; private set; }

        public bool IsDisposed =>
            Volatile.Read(ref _disposed) != 0;

        public void Enqueue(
            params byte[][] datagrams)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            foreach (byte[] datagram in datagrams)
            {
                _datagrams.Enqueue(
                    datagram.ToArray());
                _datagramSignal.Release();
            }
        }

        public async ValueTask<int> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            ReceiveStartedOnThreadPool =
                Thread.CurrentThread.IsThreadPoolThread;
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Interlocked.Exchange(
                    ref _initialReceiveDelayPending,
                    0) != 0)
            {
                await Task.Delay(
                    _initialReceiveDelay,
                    cancellationToken);
            }

            while (true)
            {
                await _datagramSignal.WaitAsync(
                    cancellationToken);
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (!_datagrams.TryDequeue(
                        out byte[]? datagram))
                {
                    continue;
                }

                datagram.AsMemory().CopyTo(buffer);
                DatagramsReceived++;
                return datagram.Length;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }

    private sealed class FakeFfmpegProcess :
        IFfmpegDesktopH264Process
    {
        private readonly Stream _standardOutput;
        private readonly TextReader _standardError;
        private readonly TimeSpan _waitForExitDelay;
        private readonly bool _exitOnGracefulRequest;
        private readonly bool _exitOnKill;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasExited;

        private FakeFfmpegProcess(
            Stream standardOutput,
            string standardError,
            bool hasExited,
            TimeSpan waitForExitDelay,
            bool exitOnGracefulRequest,
            bool exitOnKill)
        {
            _standardOutput = standardOutput;
            _standardError = new StringReader(standardError);
            _hasExited = hasExited;
            _waitForExitDelay = waitForExitDelay;
            _exitOnGracefulRequest =
                exitOnGracefulRequest;
            _exitOnKill = exitOnKill;
            if (hasExited)
            {
                _completion.TrySetResult();
            }
        }

        public Stream StandardOutput => _standardOutput;

        public TextReader StandardError => _standardError;

        public Task Completion => _completion.Task;

        public bool HasExited => _hasExited;

        public int? ExitCode => _hasExited ? 1 : null;

        public bool KillCalled { get; private set; }

        public bool GracefulExitRequested { get; private set; }

        public bool DisposeCalled { get; private set; }

        public Task Disposed => _disposed.Task;

        public int WaitForExitMilliseconds { get; private set; }

        public List<int> WaitForExitCalls { get; } = [];

        public long StandardOutputBytesRead =>
            _standardOutput is ScriptedBlockingStream stream
                ? stream.TotalBytesRead
                : 0;

        public void EnqueueStandardOutput(
            params byte[][] chunks)
        {
            if (_standardOutput is not
                ScriptedBlockingStream stream)
            {
                throw new InvalidOperationException(
                    "The fake process stdout is not streaming.");
            }

            stream.Enqueue(chunks);
        }

        public static FakeFfmpegProcess Streaming(
            IEnumerable<byte[]> chunks,
            TimeSpan readerCancellationLag = default,
            TimeSpan waitForExitDelay = default,
            bool exitOnGracefulRequest = true,
            bool exitOnKill = true)
        {
            return new FakeFfmpegProcess(
                new ScriptedBlockingStream(
                    chunks,
                    readerCancellationLag),
                standardError: string.Empty,
                hasExited: false,
                waitForExitDelay: waitForExitDelay,
                exitOnGracefulRequest:
                    exitOnGracefulRequest,
                exitOnKill: exitOnKill);
        }

        public static FakeFfmpegProcess Exited(string standardError)
        {
            return new FakeFfmpegProcess(
                new MemoryStream(),
                standardError,
                hasExited: true,
                waitForExitDelay: default,
                exitOnGracefulRequest: true,
                exitOnKill: true);
        }

        public bool TryRequestGracefulExit()
        {
            GracefulExitRequested = true;
            if (_exitOnGracefulRequest)
            {
                _hasExited = true;
                _completion.TrySetResult();
            }

            return true;
        }

        public void KillEntireProcessTree()
        {
            KillCalled = true;
            if (_exitOnKill)
            {
                CompleteExit();
            }
        }

        public void CompleteExit()
        {
            _hasExited = true;
            _completion.TrySetResult();
        }

        public bool WaitForExit(int milliseconds)
        {
            WaitForExitMilliseconds = milliseconds;
            WaitForExitCalls.Add(milliseconds);
            int delay = Math.Min(
                milliseconds,
                (int)Math.Ceiling(
                    _waitForExitDelay.TotalMilliseconds));
            if (delay > 0)
            {
                Thread.Sleep(delay);
            }

            return _hasExited;
        }

        public void Dispose()
        {
            DisposeCalled = true;
            _standardOutput.Dispose();
            _standardError.Dispose();
            _disposed.TrySetResult();
        }
    }

    private sealed class ScriptedBlockingStream : Stream
    {
        private readonly ConcurrentQueue<byte[]> _chunks;
        private readonly SemaphoreSlim _chunkSignal;
        private readonly TimeSpan _readerCancellationLag;
        private byte[]? _current;
        private int _currentOffset;
        private long _totalBytesRead;

        public ScriptedBlockingStream(
            IEnumerable<byte[]> chunks,
            TimeSpan readerCancellationLag = default)
        {
            byte[][] initialChunks =
                chunks
                    .Select(chunk => chunk.ToArray())
                    .ToArray();
            _chunks =
                new ConcurrentQueue<byte[]>(
                    initialChunks);
            _chunkSignal =
                new SemaphoreSlim(
                    initialChunks.Length);
            _readerCancellationLag = readerCancellationLag;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public long TotalBytesRead =>
            Volatile.Read(ref _totalBytesRead);

        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Enqueue(
            params byte[][] chunks)
        {
            foreach (byte[] chunk in chunks)
            {
                _chunks.Enqueue(chunk.ToArray());
                _chunkSignal.Release();
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null ||
                _currentOffset >= _current.Length)
            {
                if (_chunks.TryDequeue(out byte[]? next))
                {
                    Assert.True(
                        _chunkSignal.Wait(0),
                        "The scripted stdout signal and queue diverged.");
                    _current = next;
                    _currentOffset = 0;
                    continue;
                }

                try
                {
                    await _chunkSignal.WaitAsync(
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (_readerCancellationLag > TimeSpan.Zero)
                {
                    await Task.Delay(
                        _readerCancellationLag,
                        CancellationToken.None);
                    throw;
                }

                if (!_chunks.TryDequeue(
                        out byte[]? signaledChunk))
                {
                    continue;
                }

                _current = signaledChunk;
                _currentOffset = 0;
            }

            int length = Math.Min(
                buffer.Length,
                _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, length)
                .CopyTo(buffer);
            _currentOffset += length;
            Interlocked.Add(ref _totalBytesRead, length);
            return length;
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }
    }
}

internal static class ReadOnlyListTestExtensions
{
    public static int IndexOf<T>(
        this IReadOnlyList<T> values,
        T value)
    {
        EqualityComparer<T> comparer =
            EqualityComparer<T>.Default;
        for (int index = 0; index < values.Count; index++)
        {
            if (comparer.Equals(values[index], value))
            {
                return index;
            }
        }

        return -1;
    }
}
