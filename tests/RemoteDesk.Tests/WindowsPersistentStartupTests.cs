using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsPersistentStartupTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Root = @"C:\Program Files\RemoteDesk\Managed\S-1-5-21-1-2-3-1001";
    private static string Executable => Path.Combine(Root, new string('A', 64), "RemoteDesk.exe");

    [Fact]
    public void TaskUsesInteractiveCurrentUserWithoutCredentialsOrTimeout()
    {
        string xml = WindowsPersistentStartup.BuildTaskXml(Executable, Sid);
        Assert.Equal(Executable, WindowsPersistentStartup.ReadOwnedExecutable(xml, Sid, Root));
        var task = XElement.Parse(xml);
        XNamespace ns = task.Name.Namespace;
        Assert.Equal("IgnoreNew", (string?)task.Element(ns + "Settings")?.Element(ns + "MultipleInstancesPolicy"));
        Assert.Equal("PT0S", (string?)task.Element(ns + "Settings")?.Element(ns + "ExecutionTimeLimit"));
        Assert.DoesNotContain("Password", xml);
        Assert.DoesNotContain("S-1-5-18", xml); // Not SYSTEM or pre-login desktop.
    }

    [Theory]
    [InlineData("UserId", "S-1-5-18")]
    [InlineData("LogonType", "Password")]
    [InlineData("RunLevel", "LeastPrivilege")]
    [InlineData("Arguments", "--tray --unexpected")]
    [InlineData("URI", "NotRemoteDesk")]
    [InlineData("Source", "NotRemoteDesk")]
    public void ForeignOrModifiedTaskIsNotOwned(string element, string value)
    {
        XElement xml = XElement.Parse(WindowsPersistentStartup.BuildTaskXml(Executable, Sid));
        foreach (XElement node in xml.Descendants().Where(node => node.Name.LocalName == element)) node.Value = value;
        Assert.Null(WindowsPersistentStartup.ReadOwnedExecutable(xml.ToString(), Sid, Root));
    }

    [Theory]
    [InlineData(@"C:\Users\User\Desktop\RemoteDesk.exe")]
    [InlineData(@"C:\Program Files\RemoteDesk\Managed\S-1-5-21-1-2-3-10010\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\RemoteDesk.exe")]
    [InlineData(@"..\RemoteDesk.exe")]
    [InlineData(@"C:\Program Files\RemoteDesk\Managed\S-1-5-21-1-2-3-1001\..\RemoteDesk.exe")]
    public void UnprotectedOrEscapingPathIsRejected(string path)
    {
        Assert.False(WindowsPersistentStartup.IsManagedExecutable(path, Root));
    }

    [Fact]
    public void ExtraActionAndDtdAreRejected()
    {
        XElement xml = XElement.Parse(WindowsPersistentStartup.BuildTaskXml(Executable, Sid));
        XNamespace ns = xml.Name.Namespace;
        xml.Element(ns + "Actions")!.Add(new XElement(ns + "Exec", new XElement(ns + "Command", "cmd.exe")));
        Assert.Null(WindowsPersistentStartup.ReadOwnedExecutable(xml.ToString(), Sid, Root));
        Assert.Throws<XmlException>(() => WindowsPersistentStartup.ReadOwnedExecutable("<!DOCTYPE a [<!ENTITY e 'x'>]><a>&e;</a>", Sid, Root));
    }

    [Fact]
    public void OrdinaryUsersCannotOwnOrWriteElevatedExecutable()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var acl = new FileSecurity();
        acl.SetOwner(admins);
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        WindowsPersistentStartup.ValidateAcl(acl);
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsPersistentStartup.ValidateAcl(acl));
        acl.SetOwner(users);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsPersistentStartup.ValidateAcl(acl));
    }

    [Fact]
    public void MissingPersistentInstallIsNotSilentlyReplacedByPortableAutostart()
    {
        var status = new StartupRegistrationStatus(true, false, Executable) { IsPersistent = true };
        Assert.False(StartupService.IsOrphanedRegistration(status, _ => false));
    }

    [Fact]
    public void RemoteUpdateRestoresInstalledPermissionsBeforeStartingReceivedBinary()
    {
        string script = RemoteUpdater.CreateUpdaterScript();
        int restoreAcl = script.IndexOf("Set-Acl -LiteralPath $TargetPath -AclObject $targetFileSecurity", StringComparison.Ordinal);
        int start = script.IndexOf("$started = Start-Process -FilePath $TargetPath", StringComparison.Ordinal);
        Assert.Contains("$targetFileSecurity = Get-Acl -LiteralPath $BackupPath", script);
        Assert.True(restoreAcl >= 0 && start > restoreAcl);
    }
}
