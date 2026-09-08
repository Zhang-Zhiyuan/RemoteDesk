using System.ComponentModel;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsProcessElevationTests
{
    [Fact]
    public void ElevatedReplacementPreservesHostAndWaitsForOldProcess()
    {
        const string executable =
            @"C:\Program Files\RemoteDesk\RemoteDesk.exe";

        System.Diagnostics.ProcessStartInfo startInfo =
            WindowsProcessElevation
                .CreateElevatedReplacementStartInfo(
                    executable,
                    previousProcessId: 4321);

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(
            [
                "--tray",
                RemoteUpdater.ResumeHostAfterUpdateArgument,
                WindowsProcessElevation.WaitForProcessArgument,
                "4321"
            ],
            startInfo.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData("--wait-for-process", "83", 83)]
    [InlineData("--WAIT-FOR-PROCESS", "17", 17)]
    [InlineData("--wait-for-process", "0", null)]
    [InlineData("--wait-for-process", "bad", null)]
    [InlineData("--tray", "83", null)]
    public void PreviousProcessArgumentIsParsedStrictly(
        string name,
        string value,
        int? expected)
    {
        Assert.Equal(
            expected,
            WindowsProcessElevation
                .TryReadPreviousProcessId(
                    [name, value]));
    }

    [Fact]
    public void UacCancellationIsRecognized()
    {
        Assert.True(
            WindowsProcessElevation.IsUserCancellation(
                new Win32Exception(
                    WindowsProcessElevation
                        .UserCancelledError)));
        Assert.False(
            WindowsProcessElevation.IsUserCancellation(
                new Win32Exception(5)));
    }
}
