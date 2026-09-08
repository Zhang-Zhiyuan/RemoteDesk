using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class MediaFoundationD3D11H264DecoderTests
{
    [Fact]
    public void InputTypeDeclaresOnlyNegotiatedGop1AsIndependent()
    {
        var defaultOptions =
            new MediaFoundationD3D11H264DecoderOptions(
                1920,
                1080,
                60);
        var allIndependentOptions =
            defaultOptions with
            {
                AllSamplesIndependent = true
            };

        Assert.Equal(
            0u,
            MediaFoundationD3D11H264Decoder
                .GetAllSamplesIndependentAttributeValue(
                    defaultOptions));
        Assert.Equal(
            1u,
            MediaFoundationD3D11H264Decoder
                .GetAllSamplesIndependentAttributeValue(
                    allIndependentOptions));
    }

    [Fact]
    public void OptionsAcceptCapturePipelineBounds()
    {
        var options = new MediaFoundationD3D11H264DecoderOptions(
            Width: 3840,
            Height: 2160,
            FramesPerSecond: 120);

        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData(48, 48)]
    [InlineData(4096, 2304)]
    public void OptionsAcceptDocumentedDecoderDimensionBoundaries(
        int width,
        int height)
    {
        var options = new MediaFoundationD3D11H264DecoderOptions(
            width,
            height,
            FramesPerSecond: 60);

        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData(0, 1080, 60, 1024, "widths")]
    [InlineData(46, 1080, 60, 1024, "widths")]
    [InlineData(4098, 1080, 60, 1024, "widths")]
    [InlineData(1920, 2306, 60, 1024, "heights")]
    [InlineData(1921, 1080, 60, 1024, "even")]
    [InlineData(1920, 1079, 60, 1024, "even")]
    [InlineData(1920, 1080, 0, 1024, "Frame rate")]
    [InlineData(1920, 1080, 121, 1024, "Frame rate")]
    [InlineData(1920, 1080, 60, 0, "access-unit")]
    public void OptionsRejectUnsafeValues(
        int width,
        int height,
        int framesPerSecond,
        int maxAccessUnitBytes,
        string expectedDetail)
    {
        var options = new MediaFoundationD3D11H264DecoderOptions(
            width,
            height,
            framesPerSecond,
            maxAccessUnitBytes);

        string? failure = options.Validate();

        Assert.NotNull(failure);
        Assert.Contains(
            expectedDetail,
            failure,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectorAcceptsMixedAnnexBStartCodes()
    {
        byte[] accessUnit = Join(
            Nal(9, fourByteStartCode: true, 0xF0),
            Nal(7, fourByteStartCode: false, 0x64, 0x00, 0x1F),
            Nal(8, fourByteStartCode: true, 0xEE, 0x06),
            Nal(6, fourByteStartCode: false, 0x05),
            Nal(5, fourByteStartCode: true, 0xAA, 0xBB));

        AnnexBH264AccessUnitInfo info =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    accessUnit,
                    maxAccessUnitBytes: accessUnit.Length);

        Assert.True(info.IsIndependentFrame);
        Assert.True(info.IsWithinSizeLimit);
        Assert.True(info.HasStartCode);
        Assert.True(info.HasSequenceParameterSet);
        Assert.True(info.HasPictureParameterSet);
        Assert.True(info.HasIdrSlice);
        Assert.False(info.HasInvalidNalHeader);
        Assert.Equal(5, info.NalUnitCount);
        Assert.Equal(
            string.Empty,
            info.GetFailureDetail(
                requireIndependentFrame: true));
    }

    [Theory]
    [InlineData(7, "SPS")]
    [InlineData(8, "PPS")]
    [InlineData(5, "IDR")]
    public void InspectorRejectsMissingIndependentNal(
        int omittedNalType,
        string expectedDetail)
    {
        byte[][] allNals =
        [
            Nal(7, fourByteStartCode: true, 0x11),
            Nal(8, fourByteStartCode: false, 0x22),
            Nal(5, fourByteStartCode: true, 0x33)
        ];
        byte[] accessUnit = Join(
            allNals.Where(
                nal => (nal[^2] & 0x1F) != omittedNalType)
                .ToArray());

        AnnexBH264AccessUnitInfo info =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    accessUnit,
                    maxAccessUnitBytes: 1024);

        Assert.False(info.IsIndependentFrame);
        Assert.Contains(
            expectedDetail,
            info.GetFailureDetail(
                requireIndependentFrame: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InspectorRejectsOversizedAndInvalidNalHeader()
    {
        byte[] accessUnit = Join(
            Nal(7, fourByteStartCode: true, 0x11),
            Nal(8, fourByteStartCode: true, 0x22),
            Nal(5, fourByteStartCode: true, 0x33));
        AnnexBH264AccessUnitInfo oversized =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    accessUnit,
                    maxAccessUnitBytes: accessUnit.Length - 1);

        accessUnit[4] |= 0x80;
        AnnexBH264AccessUnitInfo invalidHeader =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    accessUnit,
                    maxAccessUnitBytes: accessUnit.Length);

        Assert.False(oversized.IsIndependentFrame);
        Assert.Contains(
            "size limit",
            oversized.GetFailureDetail(
                requireIndependentFrame: false),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidHeader.IsIndependentFrame);
        Assert.Contains(
            "invalid NAL",
            invalidHeader.GetFailureDetail(
                requireIndependentFrame: false),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectorRejectsNonAnnexBData()
    {
        AnnexBH264AccessUnitInfo info =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    [0x67, 0x64, 0x00, 0x1F],
                    maxAccessUnitBytes: 1024);

        Assert.False(info.IsIndependentFrame);
        Assert.False(info.HasStartCode);
        Assert.Contains(
            "not Annex-B",
            info.GetFailureDetail(
                requireIndependentFrame: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependentVclIsAcceptedOnlyAfterRecoveryFrame()
    {
        byte[] dependentAccessUnit =
            Nal(
                1,
                fourByteStartCode: true,
                0x9A,
                0x20);
        AnnexBH264AccessUnitInfo info =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    dependentAccessUnit,
                    maxAccessUnitBytes: 1024);

        Assert.True(info.IsValidAccessUnit);
        Assert.True(info.HasNonIdrSlice);
        Assert.False(info.IsIndependentFrame);
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .CanDecodeAccessUnit(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    info));
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .CanDecodeAccessUnit(
                    MediaFoundationD3D11ReferenceState.Ready,
                    info));
    }

    [Fact]
    public void ConfiguredIdrEstablishesRecoveryState()
    {
        byte[] recoveryAccessUnit = Join(
            Nal(7, fourByteStartCode: true, 0x64, 0x00, 0x1F),
            Nal(8, fourByteStartCode: false, 0xEE, 0x06),
            Nal(5, fourByteStartCode: true, 0xAA));
        AnnexBH264AccessUnitInfo info =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    recoveryAccessUnit,
                    maxAccessUnitBytes: 1024);

        Assert.True(info.IsIndependentFrame);
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .CanDecodeAccessUnit(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    info));
    }

    [Fact]
    public void DelayedOutputWaitIsBoundedAcrossPrimingDependentFrames()
    {
        AnnexBH264AccessUnitInfo recovery =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    Join(
                        Nal(7, true, 0x11),
                        Nal(8, false, 0x22),
                        Nal(5, true, 0x33)),
                    maxAccessUnitBytes: 1024);
        AnnexBH264AccessUnitInfo dependent =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    Nal(1, true, 0x44),
                    maxAccessUnitBytes: 1024);

        Assert.True(
            MediaFoundationD3D11H264Decoder
                .CanWaitForDelayedOutput(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    recovery));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .CanWaitForDelayedOutput(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    dependent));
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .CanWaitForDelayedOutput(
                    MediaFoundationD3D11ReferenceState.Ready,
                    dependent));
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .CanWaitForDelayedOutput(
                    MediaFoundationD3D11ReferenceState.Ready,
                    dependent,
                    acceptedInputsWithoutOutput: 1));
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .CanWaitForDelayedOutput(
                    MediaFoundationD3D11ReferenceState.Ready,
                    dependent,
                    acceptedInputsWithoutOutput: 2));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .CanWaitForDelayedOutput(
                    MediaFoundationD3D11ReferenceState.Ready,
                    dependent,
                    acceptedInputsWithoutOutput: 3));
    }

    [Fact]
    public void DelayedGop1RecoveryDrainsWithoutWaitingForFutureInput()
    {
        AnnexBH264AccessUnitInfo recovery =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    Join(
                        Nal(7, true, 0x11),
                        Nal(8, false, 0x22),
                        Nal(5, true, 0x33)),
                    maxAccessUnitBytes: 1024);
        AnnexBH264AccessUnitInfo dependent =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    Nal(1, true, 0x44),
                    maxAccessUnitBytes: 1024);

        Assert.True(
            MediaFoundationD3D11H264Decoder
                .ShouldDrainDelayedIndependentOutput(
                    allSamplesIndependent: true,
                    recovery,
                    outputAlreadyAvailable: false));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .ShouldDrainDelayedIndependentOutput(
                    allSamplesIndependent: false,
                    recovery,
                    outputAlreadyAvailable: false));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .ShouldDrainDelayedIndependentOutput(
                    allSamplesIndependent: true,
                    dependent,
                    outputAlreadyAvailable: false));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .ShouldDrainDelayedIndependentOutput(
                    allSamplesIndependent: true,
                    recovery,
                    outputAlreadyAvailable: true));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .CanWaitForDelayedOutput(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    recovery,
                    acceptedInputsWithoutOutput: 0,
                    drainCompleted: true));
    }

    [Fact]
    public void DrainRestartMarksNextInputAsDiscontinuous()
    {
        AnnexBH264AccessUnitInfo recovery =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    Join(
                        Nal(7, true, 0x11),
                        Nal(8, false, 0x22),
                        Nal(5, true, 0x33)),
                    maxAccessUnitBytes: 1024);

        Assert.False(
            MediaFoundationD3D11H264Decoder
                .ShouldMarkInputDiscontinuity(
                    MediaFoundationD3D11ReferenceState.Ready,
                    recovery,
                    restartingAfterDrain: false));
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .ShouldMarkInputDiscontinuity(
                    MediaFoundationD3D11ReferenceState.Ready,
                    recovery,
                    restartingAfterDrain: true));
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .ShouldMarkInputDiscontinuity(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    recovery,
                    restartingAfterDrain: false));
    }

    [Fact]
    public void DiscontinuityIsMarkedOnlyForActualRecoveryPoint()
    {
        AnnexBH264AccessUnitInfo recovery =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    Join(
                        Nal(7, true, 0x11),
                        Nal(8, false, 0x22),
                        Nal(5, true, 0x33)),
                    maxAccessUnitBytes: 1024);

        Assert.True(
            MediaFoundationD3D11H264Decoder
                .ShouldMarkDiscontinuity(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    recovery));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .ShouldMarkDiscontinuity(
                    MediaFoundationD3D11ReferenceState.Ready,
                    recovery));
    }

    [Fact]
    public void RejectedDependentVclForcesRecoveryWhenPreviouslyReady()
    {
        byte[] invalidDependent =
            Nal(
                1,
                fourByteStartCode: true,
                0x44);
        invalidDependent[4] |= 0x80;
        AnnexBH264AccessUnitInfo info =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    invalidDependent,
                    maxAccessUnitBytes: 1024);

        Assert.False(info.IsValidAccessUnit);
        Assert.True(info.HasNonIdrSlice);
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .ShouldEnterRecoveryAfterRejectedAccessUnit(
                    MediaFoundationD3D11ReferenceState.Ready,
                    info));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .ShouldEnterRecoveryAfterRejectedAccessUnit(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    info));
    }

    [Theory]
    [InlineData(1920, 1080, 1920u, 1080u, true)]
    [InlineData(1920, 1080, 1920u, 1088u, true)]
    [InlineData(1366, 768, 1376u, 768u, true)]
    [InlineData(1920, 1080, 1280u, 720u, false)]
    [InlineData(1920, 1080, 3840u, 2160u, false)]
    [InlineData(1920, 1080, 1920u, 1104u, false)]
    public void NegotiatedSurfaceMustMatchDeclaredVisibleFrame(
        int visibleWidth,
        int visibleHeight,
        uint codedWidth,
        uint codedHeight,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaFoundationD3D11H264Decoder
                .IsNegotiatedSurfaceCompatible(
                    visibleWidth,
                    visibleHeight,
                    codedWidth,
                    codedHeight));
    }

    [Fact]
    public void IdrWithoutConfigurationRequiresExistingDecoderState()
    {
        AnnexBH264AccessUnitInfo info =
            MediaFoundationD3D11H264Decoder
                .InspectAccessUnit(
                    Nal(
                        5,
                        fourByteStartCode: true,
                        0xAA),
                    maxAccessUnitBytes: 1024);

        Assert.True(info.IsValidAccessUnit);
        Assert.False(info.IsIndependentFrame);
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .CanDecodeAccessUnit(
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame,
                    info));
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .CanDecodeAccessUnit(
                    MediaFoundationD3D11ReferenceState.Ready,
                    info));
    }

    [Theory]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Created,
        (int)MediaFoundationD3D11DecoderState.Streaming,
        true)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Created,
        (int)MediaFoundationD3D11DecoderState.Draining,
        false)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Streaming,
        (int)MediaFoundationD3D11DecoderState.Draining,
        true)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Streaming,
        (int)MediaFoundationD3D11DecoderState.Faulted,
        true)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Draining,
        (int)MediaFoundationD3D11DecoderState.Streaming,
        true)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Faulted,
        (int)MediaFoundationD3D11DecoderState.Streaming,
        false)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Faulted,
        (int)MediaFoundationD3D11DecoderState.Disposed,
        true)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.DeviceLost,
        (int)MediaFoundationD3D11DecoderState.Disposed,
        true)]
    [InlineData(
        (int)MediaFoundationD3D11DecoderState.Disposed,
        (int)MediaFoundationD3D11DecoderState.Streaming,
        false)]
    public void StateTransitionsAreExplicit(
        int from,
        int to,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaFoundationD3D11H264Decoder
                .IsValidStateTransition(
                    (MediaFoundationD3D11DecoderState)from,
                    (MediaFoundationD3D11DecoderState)to));
    }

    [Fact]
    public void OnlyStreamingStateAcceptsInput()
    {
        foreach (MediaFoundationD3D11DecoderState state in
            Enum.GetValues<MediaFoundationD3D11DecoderState>())
        {
            Assert.Equal(
                state ==
                    MediaFoundationD3D11DecoderState.Streaming,
                MediaFoundationD3D11H264Decoder
                    .CanAcceptInput(state));
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005), true)]
    [InlineData(unchecked((int)0x887A0006), true)]
    [InlineData(unchecked((int)0x887A0007), true)]
    [InlineData(unchecked((int)0x887A0020), true)]
    [InlineData(unchecked((int)0x80070057), false)]
    [InlineData(0, false)]
    public void DeviceLossHResultsAreClassified(
        int hresult,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaFoundationD3D11H264Decoder
                .IsDeviceLostHResult(hresult));
    }

    [Fact]
    public void H264VldHardwareProfileIsRecognizedExplicitly()
    {
        Assert.True(
            MediaFoundationD3D11H264Decoder
                .IsH264HardwareDecoderProfile(
                    new Guid(
                        "1B81BE68-A0C7-11D3-B984-00C04F2E73C5")));
        Assert.False(
            MediaFoundationD3D11H264Decoder
                .IsH264HardwareDecoderProfile(Guid.Empty));
    }

    [Fact]
    public void InvalidOptionsReturnCapabilityFailureWithoutThrowing()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            return;
        }

        bool created =
            MediaFoundationD3D11H264Decoder.TryCreate(
                new(
                    Width: 1919,
                    Height: 1080,
                    FramesPerSecond: 60),
                out MediaFoundationD3D11H264Decoder? decoder,
                out MediaFoundationD3D11Capability capability);

        Assert.False(created);
        Assert.Null(decoder);
        Assert.False(capability.IsAvailable);
        Assert.Equal(
            MediaFoundationD3D11CapabilityStatus.InvalidOptions,
            capability.Status);
    }

    [HardwareSmokeFact]
    [Trait("Category", "HardwareSmoke")]
    public void AnnexBIdrDecodesToNv12TextureWhenHardwareSmokeIsEnabled()
    {
        byte[] accessUnit =
            Convert.FromBase64String(SmokeIdrBase64);
        bool created =
            MediaFoundationD3D11H264Decoder.TryCreate(
                new(
                    Width: 64,
                    Height: 64,
                    FramesPerSecond: 30),
                out MediaFoundationD3D11H264Decoder? decoder,
                out MediaFoundationD3D11Capability capability);

        Assert.True(
            created,
            $"{capability.Status}: {capability.Detail}");
        Assert.Contains(
            "H.264 VLD hardware decoder profile",
            capability.Detail,
            StringComparison.Ordinal);
        Assert.NotNull(decoder);
        using (decoder)
        {
            bool decoded =
                decoder.TryDecodeIndependentAccessUnit(
                    accessUnit,
                    sampleTime100Nanoseconds: 42,
                    out MediaFoundationD3D11DecodedFrame? frame,
                    out string? failureDetail);

            Assert.True(decoded, failureDetail);
            Assert.NotNull(frame);
            using (frame)
            {
                Assert.Equal("NV12", frame.Format.ToString());
                Assert.Equal(64, frame.VisibleWidth);
                Assert.Equal(64, frame.VisibleHeight);
                Assert.True(frame.TextureWidth >= 64);
                Assert.True(frame.TextureHeight >= 64);
                Assert.Equal(42, frame.SampleTime100Nanoseconds);
            }

            Assert.Equal(
                MediaFoundationD3D11ReferenceState.Ready,
                decoder.ReferenceState);
        }
    }

    [HardwareSmokeFact]
    [Trait("Category", "HardwareSmoke")]
    public void MismatchedBitstreamDimensionsFaultInsteadOfPublishingFrame()
    {
        byte[] accessUnit =
            Convert.FromBase64String(SmokeIdrBase64);
        Assert.True(
            MediaFoundationD3D11H264Decoder.TryCreate(
                new(
                    Width: 1920,
                    Height: 1080,
                    FramesPerSecond: 30),
                out MediaFoundationD3D11H264Decoder? decoder,
                out MediaFoundationD3D11Capability capability),
            $"{capability.Status}: {capability.Detail}");
        Assert.NotNull(decoder);
        using (decoder)
        {
            bool decoded =
                decoder.TryDecodeAccessUnit(
                    accessUnit,
                    sampleTime100Nanoseconds: 0,
                    out MediaFoundationD3D11DecodedFrame? frame,
                    out string? failureDetail);

            Assert.False(decoded);
            Assert.Null(frame);
            Assert.Equal(
                MediaFoundationD3D11DecoderState.Faulted,
                decoder.State);
            Assert.Contains(
                "64x64",
                failureDetail,
                StringComparison.Ordinal);
        }
    }

    [HardwareSmokeFact]
    [Trait("Category", "HardwareSmoke")]
    public void FrameLeaseOutlivesDecoderAndDisposesIdempotently()
    {
        Assert.True(
            MediaFoundationD3D11H264Decoder.TryCreate(
                new(
                    Width: 64,
                    Height: 64,
                    FramesPerSecond: 30),
                out MediaFoundationD3D11H264Decoder? decoder,
                out MediaFoundationD3D11Capability capability),
            $"{capability.Status}: {capability.Detail}");
        Assert.NotNull(decoder);
        Assert.True(
            decoder.TryDecodeIndependentAccessUnit(
                Convert.FromBase64String(SmokeIdrBase64),
                sampleTime100Nanoseconds: 7,
                out MediaFoundationD3D11DecodedFrame? frame,
                out string? failureDetail),
            failureDetail);
        Assert.NotNull(frame);
        Assert.Equal(1, decoder.OutstandingFrameLeaseCount);

        decoder.Dispose();
        Assert.Equal(
            MediaFoundationD3D11DecoderState.Disposed,
            decoder.State);
        Assert.Equal(1, decoder.OutstandingFrameLeaseCount);
        using (var textureLease = frame.AcquireTextureLease())
        using (var deviceLease = frame.AcquireDeviceLease())
        {
            Assert.Equal("NV12", textureLease.Description.Format.ToString());
            Assert.NotEqual(0, deviceLease.NativePointer);
        }

        frame.Dispose();
        frame.Dispose();
        Assert.Equal(0, decoder.OutstandingFrameLeaseCount);
        Assert.Throws<ObjectDisposedException>(
            frame.AcquireTextureLease);
        decoder.Dispose();
    }

    [HardwareSmokeFact]
    [Trait("Category", "HardwareSmoke")]
    public void DecodedNv12FramesPresentDirectlyWithoutCpuReadback()
    {
        Assert.True(
            MediaFoundationD3D11H264Decoder.TryCreate(
                new(
                    Width: 64,
                    Height: 64,
                    FramesPerSecond: 30),
                out MediaFoundationD3D11H264Decoder? decoder,
                out MediaFoundationD3D11Capability
                    decoderCapability),
            $"{decoderCapability.Status}: " +
                decoderCapability.Detail);
        Assert.NotNull(decoder);
        using (decoder)
        using (var window =
            new Form
            {
                ClientSize = new Size(320, 240),
                ShowInTaskbar = false
            })
        {
            window.CreateControl();
            nint handle = window.Handle;
            using var deviceLease =
                decoder.AcquireDeviceLease();
            Assert.True(
                D3D11HwndVideoPresenter.TryCreate(
                    deviceLease,
                    new(
                        handle,
                        SourceWidth: 64,
                        SourceHeight: 64,
                        FramesPerSecond: 30),
                    out D3D11HwndVideoPresenter? presenter,
                    out D3D11HwndVideoPresenterCapability
                        presenterCapability),
                $"{presenterCapability.Status}: " +
                    presenterCapability.Detail);
            Assert.NotNull(presenter);
            using (presenter)
            {
                PresentDecodedAccessUnit(
                    decoder,
                    presenter,
                    Convert.FromBase64String(
                        RecoveryIdrBase64),
                    sampleTime100Nanoseconds: 0);
                byte[] dependent =
                    Convert.FromBase64String(
                        DependentPBase64);
                for (int index = 1;
                    index <= 120;
                    index++)
                {
                    PresentDecodedAccessUnit(
                        decoder,
                        presenter,
                        dependent,
                        sampleTime100Nanoseconds:
                            index * 333_333L);
                }

                Assert.Equal(
                    0,
                    decoder.OutstandingFrameLeaseCount);
            }
        }
    }

    [HardwareSmokeFact]
    [Trait("Category", "HardwareSmoke")]
    public void GapAndRejectedDependentFrameRequireHardwareRecovery()
    {
        Assert.True(
            MediaFoundationD3D11H264Decoder.TryCreate(
                new(
                    Width: 64,
                    Height: 64,
                    FramesPerSecond: 30),
                out MediaFoundationD3D11H264Decoder? decoder,
                out MediaFoundationD3D11Capability capability),
            $"{capability.Status}: {capability.Detail}");
        Assert.NotNull(decoder);
        using (decoder)
        {
            byte[] recovery =
                Convert.FromBase64String(
                    RecoveryIdrBase64);
            byte[] dependent =
                Convert.FromBase64String(
                    DependentPBase64);

            AssertDecoded(decoder, recovery, 0);
            AssertDecoded(decoder, dependent, 333_333);
            Assert.True(
                decoder.NotifyAccessUnitGap(
                    out string? gapFailure),
                gapFailure);
            Assert.Equal(
                MediaFoundationD3D11ReferenceState
                    .NeedsIndependentFrame,
                decoder.ReferenceState);
            Assert.False(
                decoder.TryDecodeAccessUnit(
                    dependent,
                    666_666,
                    out MediaFoundationD3D11DecodedFrame? gapFrame,
                    out string? dependentFailure));
            Assert.Null(gapFrame);
            Assert.Contains(
                "requires",
                dependentFailure,
                StringComparison.OrdinalIgnoreCase);

            AssertDecoded(decoder, recovery, 999_999);
            byte[] rejectedDependent =
                dependent.ToArray();
            rejectedDependent[9] |= 0x80;
            AnnexBH264AccessUnitInfo rejectedInfo =
                MediaFoundationD3D11H264Decoder
                    .InspectAccessUnit(
                        rejectedDependent,
                        1024);
            Assert.True(rejectedInfo.HasNonIdrSlice);
            Assert.False(rejectedInfo.IsValidAccessUnit);
            Assert.False(
                decoder.TryDecodeAccessUnit(
                    rejectedDependent,
                    1_333_332,
                    out MediaFoundationD3D11DecodedFrame? rejectedFrame,
                    out string? rejectedFailure));
            Assert.Null(rejectedFrame);
            Assert.Contains(
                "fresh",
                rejectedFailure,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                MediaFoundationD3D11ReferenceState
                    .NeedsIndependentFrame,
                decoder.ReferenceState);

            AssertDecoded(decoder, recovery, 1_666_665);
            byte[] damagedDependent =
                dependent[..12];
            MediaFoundationD3D11DecodeResult damaged =
                decoder.DecodeAccessUnit(
                    damagedDependent,
                    1_999_998);
            damaged.Frame?.Dispose();
            Assert.Equal(
                MediaFoundationD3D11DecodeStatus
                    .AcceptedAwaitingOutput,
                damaged.Status);
            Assert.Equal(
                MediaFoundationD3D11DecoderState.Streaming,
                decoder.State);
            Assert.Equal(
                MediaFoundationD3D11ReferenceState
                    .Ready,
                decoder.ReferenceState);
            Assert.True(
                decoder.NotifyAccessUnitGap(
                    out string? damagedGapFailure),
                damagedGapFailure);
            Assert.Equal(
                MediaFoundationD3D11ReferenceState
                    .NeedsIndependentFrame,
                decoder.ReferenceState);
            AssertDecoded(decoder, recovery, 2_333_331);
        }
    }

    [Fact]
    public void RuntimeFrameLeaseReleaseIsIdempotent()
    {
        int shutdownCount = 0;
        var lifetime = new MediaFoundationRuntimeLifetime(
            () => Interlocked.Increment(
                ref shutdownCount));
        IDisposable lease = lifetime.AcquireFrameLease();

        Assert.Equal(1, lifetime.OutstandingFrameLeaseCount);
        lifetime.ReleaseOwner();
        Assert.Equal(0, shutdownCount);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, lifetime.OutstandingFrameLeaseCount);
        Assert.Equal(1, shutdownCount);
        lifetime.ReleaseOwner();
        Assert.Equal(1, shutdownCount);
    }

    private const string SmokeIdrBase64 =
        "AAAAAQkQAAAAAWdCwArcQmwEQAAAAwBAAAAPI8SJ4AAAAAFozg/" +
        "IAAABZYiEOhGKAAIxccAAQ8o4AAgFycnJ1111111111114A==";
    private const string RecoveryIdrBase64 =
        "AAAAAQkQAAAAAWdCwAraEJsBEAAAAwAQAAADA8jxImoAAAABaM4P" +
        "yAAAAWWIhDoRigACMXHAAEPKOAAIBcnJyddddddddddddeA=";
    private const string DependentPBase64 =
        "AAAAAQkwAAABQZogNoIw";

    private static void AssertDecoded(
        MediaFoundationD3D11H264Decoder decoder,
        byte[] accessUnit,
        long sampleTime100Nanoseconds)
    {
        Assert.True(
            decoder.TryDecodeAccessUnit(
                accessUnit,
                sampleTime100Nanoseconds,
                out MediaFoundationD3D11DecodedFrame? frame,
                out string? failureDetail),
            failureDetail);
        Assert.NotNull(frame);
        frame.Dispose();
        Assert.Equal(
            MediaFoundationD3D11ReferenceState.Ready,
            decoder.ReferenceState);
    }

    private static void PresentDecodedAccessUnit(
        MediaFoundationD3D11H264Decoder decoder,
        D3D11HwndVideoPresenter presenter,
        byte[] accessUnit,
        long sampleTime100Nanoseconds)
    {
        Assert.True(
            decoder.TryDecodeAccessUnit(
                accessUnit,
                sampleTime100Nanoseconds,
                out MediaFoundationD3D11DecodedFrame? frame,
                out string? failureDetail),
            failureDetail);
        Assert.NotNull(frame);
        using (frame)
        {
            D3D11HwndVideoPresenterResult result =
                presenter.Present(
                    frame,
                    new Rectangle(
                        0,
                        0,
                        frame.VisibleWidth,
                        frame.VisibleHeight),
                    captureValidation: true);
            Assert.True(
                result.IsSuccess ||
                    result.Status ==
                        D3D11HwndVideoPresenterStatus
                            .Occluded,
                $"{result.Status}: 0x{result.HResult:X8}, " +
                    result.Detail);
            if (result.Status ==
                D3D11HwndVideoPresenterStatus.Presented)
            {
                D3D11HwndVideoValidationResult validation =
                    presenter.ValidatePresentedFrame();
                Assert.True(
                    validation.IsValid,
                    validation.Detail);
            }
        }
    }

    private static byte[] Nal(
        int nalType,
        bool fourByteStartCode,
        params byte[] payload)
    {
        byte[] startCode = fourByteStartCode
            ? [0, 0, 0, 1]
            : [0, 0, 1];
        return
        [
            .. startCode,
            (byte)(0x60 | nalType),
            .. payload
        ];
    }

    private static byte[] Join(params byte[][] values)
    {
        int length = values.Sum(value => value.Length);
        var joined = new byte[length];
        int offset = 0;
        foreach (byte[] value in values)
        {
            Buffer.BlockCopy(
                value,
                0,
                joined,
                offset,
                value.Length);
            offset += value.Length;
        }

        return joined;
    }
}

internal sealed class HardwareSmokeFactAttribute : FactAttribute
{
    public HardwareSmokeFactAttribute()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(
                "REMOTEDESK_RUN_HARDWARE_SMOKE"),
            "1",
            StringComparison.Ordinal) ||
            !OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            Skip =
                "Set REMOTEDESK_RUN_HARDWARE_SMOKE=1 on Windows 8+ " +
                "to run this hardware smoke test.";
        }
    }
}
