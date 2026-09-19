using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardAppliedSequenceTests
{
    [Fact]
    public void StableMatchingTextCanBeMarkedAsOurWrite() =>
        Assert.Equal(12U, ClipboardTextService.ReadVerifiedTextSequence("中文😀", () => 12, () => "中文😀"));

    [Fact]
    public void AnotherApplicationsCopyMustNotBeMarkedAsOurWrite() =>
        Assert.Throws<OperationCanceledException>(() => ClipboardTextService.ReadVerifiedTextSequence(
            "remote", () => 13, () => "new local copy"));

    [Fact]
    public void SequenceChangingDuringVerificationIsNotOurWrite()
    {
        uint sequence = 12;
        Assert.Throws<OperationCanceledException>(() => ClipboardTextService.ReadVerifiedTextSequence(
            "remote", () => sequence++, () => "remote"));
    }

    [Fact]
    public void UnavailableSequenceDoesNotReadOrAcceptText() =>
        Assert.Throws<OperationCanceledException>(() => ClipboardTextService.ReadVerifiedTextSequence(
            "remote", () => 0, () => throw new InvalidOperationException("Must not read")));
}
