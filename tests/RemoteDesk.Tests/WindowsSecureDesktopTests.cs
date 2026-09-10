using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSecureDesktopTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private static string Executable => Path.Combine(WindowsSecureDesktopInstallation.RootForSid(Sid), new string('A', 64), "RemoteDesk.exe");

    [Fact]
    public void ProcessIdentityUsesQueryOnlyTokenWithoutImpersonation()
    {
        var identity = WindowsSecureDesktopNative.ProcessIdentity((uint)Environment.ProcessId);
        Assert.Equal(WindowsPersistentStartup.UserSid, identity.Sid);
        Assert.Equal(WindowsProcessElevation.IsCurrentProcessElevated(), identity.Elevated);
        Assert.Equal(Environment.ProcessPath, identity.Image, ignoreCase: true);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().SessionId, identity.Session);
    }

    [Fact]
    public void HelperDesktopAccessSupportsInputButDoesNotModifyAclOrActivateDesktop()
    {
        uint access = WindowsSecureDesktopAgent.DesktopAccess;
        Assert.NotEqual(0u, access & 0x20); // Required for SendInput, not just SetCursorPos.
        Assert.NotEqual(0u, access & 0x10); // Fixed Shift-state release diagnostic.
        Assert.Equal(0u, access & (0x40000u | 0x80000u | 0x100u | 0x2u));
    }

    [Fact]
    public void SecureCaptureResolvesTheSameMonitorAfterOrientationChange()
    {
        var request = new SecureDesktopRequest("capture", Width: 3840, Height: 2160, CaptureTargetId: @"\\.\DISPLAY1");
        var portrait = new Rectangle(0, 0, 2160, 3840);
        ScreenCaptureTarget[] targets = [new(@"\\.\DISPLAY1", "Screen", portrait)];
        Assert.Equal(portrait, WindowsSecureDesktopDisplays.ResolveCaptureBounds(request, targets));
        Assert.False(WindowsSecureDesktopDisplays.Contains(targets, new Rectangle(0, 0, 3840, 2160)));
        Assert.True(WindowsSecureDesktopDisplays.Contains(targets, portrait));
        Assert.Throws<ScreenCaptureTargetUnavailableException>(() =>
            WindowsSecureDesktopDisplays.ResolveCaptureBounds(request with { CaptureTargetId = @"\\.\DISPLAY2" }, targets));
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(request with { CaptureTargetId = new string('x', 129) }));
    }

    [Fact]
    public void ServiceCommandIsQuotedAndBoundToProtectedOwnerPackage()
    {
        string command = WindowsSecureDesktopInstallation.ServiceCommand(Executable, Sid);
        Assert.Equal(Executable, WindowsSecureDesktopInstallation.ReadOwnedExecutable(command, Sid));
        Assert.Null(WindowsSecureDesktopInstallation.ReadOwnedExecutable(command + " --extra", Sid));
        Assert.Null(WindowsSecureDesktopInstallation.ReadOwnedExecutable(command.Trim('"'), Sid));
        Assert.Null(WindowsSecureDesktopInstallation.ReadOwnedExecutable(command, "S-1-5-21-1-2-3-1002"));
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("..\\..\\other")]
    [InlineData("S-1-5-21-1-2-3-1001 --extra")]
    public void ServiceRejectsNonAccountOrUntrustedIdentity(string sid) =>
        Assert.ThrowsAny<ArgumentException>(() => WindowsSecureDesktopInstallation.ValidateSid(sid));

    [Theory]
    [InlineData(@"C:\Users\User\RemoteDesk.exe")]
    [InlineData(@"C:\Program Files\RemoteDesk\Managed\S-1-5-21-1-2-3-10010\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\RemoteDesk.exe")]
    [InlineData(@"C:\Program Files\RemoteDesk\Managed\S-1-5-21-1-2-3-1001\..\RemoteDesk.exe")]
    public void ServiceDoesNotExecuteUnprotectedOrEscapingPath(string path) =>
        Assert.Null(WindowsSecureDesktopInstallation.ReadOwnedExecutable(WindowsSecureDesktopInstallation.ServiceCommand(path, Sid), Sid));

    [Theory]
    [InlineData(Sid, true, 1, 1, true)]
    [InlineData(Sid, false, 1, 1, false)]
    [InlineData("S-1-5-21-1-2-3-1002", true, 1, 1, false)]
    [InlineData("S-1-5-18", true, 1, 1, false)]
    [InlineData(Sid, true, 2, 1, false)]
    [InlineData(Sid, true, 0, 0, false)]
    public void PipeRequiresElevatedOwnerInSameInteractiveSession(string sid, bool elevated, int clientSession, uint session, bool expected) =>
        Assert.Equal(expected, WindowsSecureDesktopProtocol.AllowsClient(sid, elevated, clientSession, Sid, session));

    [Fact]
    public void PipeDeniesNetworkAndDoesNotGrantUnprivilegedUserAccess()
    {
        var security = WindowsSecureDesktopProtocol.PipeSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<System.IO.Pipes.PipeAccessRule>().ToArray();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-2" && rule.AccessControlType == AccessControlType.Deny);
        Assert.All(rules.Where(rule => rule.AccessControlType == AccessControlType.Allow),
            rule => Assert.Contains(rule.IdentityReference.Value, new[] { "S-1-5-18", "S-1-5-32-544" }));
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("read-file")]
    [InlineData("unlock-password")]
    [InlineData("switch-desktop")]
    [InlineData("")]
    public void HelperRejectsCapabilitiesOutsideCaptureAndInput(string operation) =>
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(new(operation)));

    [Fact]
    public void ProtocolBoundsGeometryAndInputBeforeNativeCalls()
    {
        var request = new SecureDesktopRequest("capture", Left: -1920, Width: 1920, Height: 1080);
        WindowsSecureDesktopProtocol.Validate(request);
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(request with { Width = int.MaxValue }));
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(request with { Top = int.MinValue }));
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(request with { Quality = 101 }));
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(request with { Scale = 0 }));
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.ValidateFrameSize(32768, 32768));
        WindowsSecureDesktopProtocol.ValidateFrameSize(3840, 2160);
        Assert.ThrowsAny<Exception>(() => WindowsSecureDesktopProtocol.Validate(new("input", RemoteInputCommand.KeyDown(0),
            Width: 1920, Height: 1080, FrameWidth: 1920, FrameHeight: 1080)));
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(new("release-key", RemoteInputCommand.KeyUp(0x10))));
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Validate(new("release-mouse")));
    }

    [Theory]
    [InlineData(0x41, 0x1E, 1)] // A, physical keyboard.
    [InlineData(0x30, 0x0B, 1)] // Top-row 0, including leading-zero PINs.
    [InlineData(0x60, 0x52, 1)] // Numpad 0: not the extended Insert key.
    [InlineData(0x0D, 0x1C, 1)] // Enter.
    [InlineData(0x0D, 0x1C, 3)] // Numpad Enter.
    [InlineData(0x08, 0x0E, 1)] // Backspace edits a mistyped credential.
    [InlineData(0x09, 0x0F, 1)] // Tab selects fields/sign-in options.
    [InlineData(0x31, 0, 0)] // Virtual keys from non-Windows clients.
    public void LoginKeyCommandsPreservePhysicalAndVirtualInputAcrossBothProtocols(int key, int scan, int flags)
    {
        foreach (bool up in new[] { false, true })
        {
            var command = up ? RemoteInputCommand.KeyUp(key, scan, (RemoteKeyboardFlags)flags) :
                RemoteInputCommand.KeyDown(key, scan, (RemoteKeyboardFlags)flags);
            var request = new SecureDesktopRequest("input", RemoteMessageCodec.DecodeInput(RemoteMessageCodec.EncodeInput(command)),
                Width: 1920, Height: 1080, FrameWidth: 1920, FrameHeight: 1080);
            WindowsSecureDesktopProtocol.Validate(request);
            using var stream = new MemoryStream();
            WindowsSecureDesktopProtocol.Write(stream, request, default);
            stream.Position = 0;
            var restored = WindowsSecureDesktopProtocol.Read<SecureDesktopRequest>(stream, default);
            Assert.Equal(command, restored.Command);
            Assert.Equal(InputInjector.CreateNativeKeyboardInput(command), InputInjector.CreateNativeKeyboardInput(restored.Command));
        }
    }

    [Theory]
    [InlineData('0')]
    [InlineData('!')]
    [InlineData('中')]
    [InlineData(0x1F600)]
    public void MobileTextInputPreservesDigitsSymbolsAndUnicodeWithoutClipboard(int codePoint)
    {
        var command = RemoteInputCommand.TextInput(codePoint);
        var request = new SecureDesktopRequest("input", RemoteMessageCodec.DecodeInput(RemoteMessageCodec.EncodeInput(command)),
            Width: 1920, Height: 1080, FrameWidth: 1920, FrameHeight: 1080);
        WindowsSecureDesktopProtocol.Validate(request);
        using var stream = new MemoryStream();
        WindowsSecureDesktopProtocol.Write(stream, request, default);
        stream.Position = 0;
        Assert.Equal(command, WindowsSecureDesktopProtocol.Read<SecureDesktopRequest>(stream, default).Command);
    }

    [Fact]
    public void FramedMetadataRoundTripsWithoutConsumingFollowingBinaryFrame()
    {
        using var stream = new MemoryStream();
        var request = new SecureDesktopRequest("capture", Width: 1920, Height: 1080);
        WindowsSecureDesktopProtocol.Write(stream, request, default);
        stream.WriteByte(0xAB);
        stream.Position = 0;
        Assert.Equal(request, WindowsSecureDesktopProtocol.Read<SecureDesktopRequest>(stream, default));
        Assert.Equal(0xAB, stream.ReadByte());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(int.MaxValue)]
    public void MalformedLengthsAreRejectedBeforeAllocation(int size)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, size);
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => WindowsSecureDesktopProtocol.Read<SecureDesktopRequest>(stream, default));
    }
}
