using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsInputIntegrityGuardTests
{
    [Theory]
    [InlineData(0x2000u, 0x3000u, true)]
    [InlineData(0x3000u, 0x3000u, false)]
    [InlineData(0x3000u, 0x2000u, false)]
    public void OnlyHigherTargetIntegrityRequiresElevation(
        uint current,
        uint target,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsInputIntegrityGuard
                .IsTargetIntegrityHigher(
                    current,
                    target));
    }

    [Fact]
    public void UnknownIntegrityNeverBlocksInput()
    {
        Assert.False(
            WindowsInputIntegrityGuard
                .IsTargetIntegrityHigher(
                    null,
                    0x3000));
        Assert.False(
            WindowsInputIntegrityGuard
                .IsTargetIntegrityHigher(
                    0x2000,
                    null));
    }

    [Fact]
    public void FailureMessageGivesActionableWindowsBoundary()
    {
        Assert.Contains(
            "管理员重启",
            WindowsInputIntegrityGuard
                .ElevationRequiredMessage);
        Assert.Contains(
            "安全桌面",
            WindowsInputIntegrityGuard
                .ElevationRequiredMessage);
    }
}
