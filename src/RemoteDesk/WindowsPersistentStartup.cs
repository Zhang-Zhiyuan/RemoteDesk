using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace RemoteDesk;

// A per-user interactive logon task, not a SYSTEM service. No Windows password is stored.
internal static class WindowsPersistentStartup
{
    internal const string InstallArgument = "--install-persistent-startup";
    internal const string EnableArgument = "--enable-persistent-startup";
    internal const string DisableArgument = "--disable-persistent-startup";
    private const string Marker = "RemoteDesk/InteractiveStartup/v1";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    internal static string UserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
    internal static string TaskName => "RemoteDesk-Interactive-" + UserSid;
    internal static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDesk", "Managed", UserSid);

    public static PersistentStartupStatus GetStatus()
    {
        try
        {
            using var scheduler = new Scheduler();
            dynamic? task = scheduler.FindTask();
            if (task is null) return default;
            try
            {
                string? path = ReadOwnedExecutable((string)task.Xml, UserSid, InstallRoot);
                return path is null ? default : new(true, (bool)task.Enabled, path);
            }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or XmlException)
        {
            return default;
        }
    }

    public static async Task ChangeAsync(string operation)
    {
        if (operation is not (InstallArgument or EnableArgument or DisableArgument))
            throw new ArgumentException("未知的常驻启动操作。", nameof(operation));
        // All writes happen in the helper. The caller keeps its UI and active sessions alive.
        var info = new ProcessStartInfo(Application.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)!
        };
        info.ArgumentList.Add(operation);
        info.ArgumentList.Add(UserSid);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("无法启动常驻配置程序。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("常驻启动未修改，请查看配置程序的错误提示。");
    }

    public static bool TryHandleCommand(string[] args)
    {
        string? operation = args.FirstOrDefault(arg => arg is InstallArgument or EnableArgument or DisableArgument);
        if (operation is null) return false;
        try
        {
            if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != UserSid) ||
                !WindowsProcessElevation.IsCurrentProcessElevated())
                throw new InvalidOperationException("常驻配置需要当前 Windows 用户的管理员授权。");
            if (operation == InstallArgument) Install(Application.ExecutablePath);
            else SetEnabled(operation == EnableArgument);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            MessageBox.Show(ex.Message, "RemoteDesk 常驻启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return true;
    }

    internal static void Install(string sourceExecutable)
    {
        if (!WindowsProcessElevation.IsCurrentProcessElevated()) throw new UnauthorizedAccessException();
        // A build-output apphost is not a standalone package. Copying only it would break next login.
        if (File.Exists(Path.ChangeExtension(sourceExecutable, ".runtimeconfig.json")))
            throw new InvalidOperationException("请在发布版 RemoteDesk.exe 中安装常驻启动，开发目录不是完整安装包。");
        using var scheduler = new Scheduler();
        dynamic? previous = scheduler.FindTask();
        if (previous is not null)
        {
            try
            {
                if (ReadOwnedExecutable((string)previous.Xml, UserSid, InstallRoot) is null)
                    throw new InvalidOperationException("同名计划任务不属于此 RemoteDesk 安装，未覆盖。");
            }
            finally { Marshal.FinalReleaseComObject(previous); }
        }

        EnsureProtectedDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDesk"));
        EnsureProtectedDirectory(Path.GetDirectoryName(InstallRoot)!);
        EnsureProtectedDirectory(InstallRoot);
        using var source = new FileStream(sourceExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(SHA256.HashData(source));
        string package = Path.Combine(InstallRoot, hash);
        EnsureProtectedDirectory(package);
        string target = Path.Combine(package, "RemoteDesk.exe");
        if (File.Exists(target))
        {
            ValidateProtectedFile(target);
            using var existing = File.OpenRead(target);
            if (Convert.ToHexString(SHA256.HashData(existing)) != hash)
                throw new IOException("已安装文件校验失败，未覆盖或启动。");
        }
        else
        {
            // Incomplete copies are never registered; a failed copy can be retried safely.
            string staging = Path.Combine(package, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                source.Position = 0;
                using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }
                ProtectFile(staging);
                File.Move(staging, target);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
        // License files are copied, never scripts or DLLs from an untrusted search directory.
        foreach (string name in new[] { "DOTNET-RUNTIME-LICENSE.TXT", "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT", "THIRD-PARTY-NOTICES.md" })
        {
            string license = Path.Combine(Path.GetDirectoryName(sourceExecutable)!, name);
            string installed = Path.Combine(package, name);
            if (File.Exists(license) && !File.Exists(installed))
            {
                File.Copy(license, installed, overwrite: false);
                ProtectFile(installed);
            }
        }
        dynamic registered = scheduler.Folder.RegisterTask(TaskName, BuildTaskXml(target, UserSid),
            6, UserSid, null, 3, null); // CREATE_OR_UPDATE, INTERACTIVE_TOKEN; no password.
        Marshal.FinalReleaseComObject(registered);
        StartupService.SetRegistryEnabled(false);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!WindowsProcessElevation.IsCurrentProcessElevated()) throw new UnauthorizedAccessException();
        using var scheduler = new Scheduler();
        dynamic task = scheduler.FindTask() ?? throw new InvalidOperationException("常驻启动尚未安装。");
        try
        {
            string? path = ReadOwnedExecutable((string)task.Xml, UserSid, InstallRoot);
            if (path is null) throw new InvalidOperationException("同名任务不是 RemoteDesk 的常驻安装。");
            if (enabled) ValidateProtectedFile(path);
            task.Enabled = enabled;
            StartupService.SetRegistryEnabled(false);
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    public static bool TryStartInstalled()
    {
        try
        {
            using var scheduler = new Scheduler();
            dynamic? task = scheduler.FindTask();
            if (task is null) return false;
            try
            {
                string? path = ReadOwnedExecutable((string)task.Xml, UserSid, InstallRoot);
                if (path is null || !(bool)task.Enabled) return false;
                ValidateProtectedFile(path);
                dynamic running = task.Run(null);
                Marshal.FinalReleaseComObject(running);
                return true;
            }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    internal static string BuildTaskXml(string executable, string sid)
    {
        XNamespace ns = TaskNamespace;
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "URI", "\\RemoteDesk-Interactive-" + sid),
                new XElement(ns + "Source", Marker),
                new XElement(ns + "Description", "RemoteDesk 当前用户登录后以管理员权限运行；不保存系统密码。")),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger",
                new XElement(ns + "Enabled", true), new XElement(ns + "UserId", sid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                new XElement(ns + "UserId", sid), new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "AllowStartOnDemand", true), new XElement(ns + "Enabled", true),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), new XElement(ns + "Exec",
                new XElement(ns + "Command", executable), new XElement(ns + "Arguments", "--tray"),
                new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable))))).ToString();
    }

    internal static string? ReadOwnedExecutable(string xml, string sid, string root)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536
        });
        XElement task = XElement.Load(reader);
        XNamespace ns = TaskNamespace;
        XElement? principal = task.Element(ns + "Principals")?.Element(ns + "Principal");
        XElement[] actions = task.Element(ns + "Actions")?.Elements().ToArray() ?? [];
        if ((string?)task.Element(ns + "RegistrationInfo")?.Element(ns + "Source") != Marker ||
            (string?)task.Element(ns + "RegistrationInfo")?.Element(ns + "URI") != "\\RemoteDesk-Interactive-" + sid ||
            (string?)principal?.Element(ns + "UserId") != sid ||
            (string?)principal?.Element(ns + "LogonType") != "InteractiveToken" ||
            (string?)principal?.Element(ns + "RunLevel") != "HighestAvailable" ||
            actions.Length != 1 || actions[0].Name != ns + "Exec" ||
            (string?)actions[0].Element(ns + "Arguments") != "--tray") return null;
        string? command = (string?)actions[0].Element(ns + "Command");
        return command is not null && IsManagedExecutable(command, root) ? command : null;
    }

    internal static bool IsManagedExecutable(string path, string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            return parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) &&
                string.Equals(parts[1], "RemoteDesk.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static void EnsureProtectedDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            ValidateProtectedDirectory(path);
            return;
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(Administrators);
        foreach (SecurityIdentifier sid in new[] { Administrators, SystemAccount, Users })
            security.AddAccessRule(new FileSystemAccessRule(sid,
                sid == Users ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        ValidateProtectedDirectory(path);
    }

    private static void ProtectFile(string path)
    {
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(false, false);
        new FileInfo(path).SetAccessControl(security);
        ValidateProtectedFile(path);
    }

    private static void ValidateProtectedDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("安装目录不能是链接。");
        ValidateAcl(directory.GetAccessControl());
    }

    private static void ValidateProtectedFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("常驻程序不存在或是链接。");
        ValidateAcl(file.GetAccessControl());
        // Validate all ancestors of the dedicated installation, not just the executable ACL.
        for (DirectoryInfo? directory = file.Directory; directory is not null &&
             !string.Equals(directory.FullName, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);
             directory = directory.Parent) ValidateProtectedDirectory(directory.FullName);
    }

    internal static void ValidateAcl(FileSystemSecurity security)
    {
        var owner = (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
        if (owner != Administrators && owner != SystemAccount) throw new UnauthorizedAccessException("常驻目录必须归管理员或 SYSTEM 所有。");
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 &&
                !rule.IdentityReference.Equals(Administrators) && !rule.IdentityReference.Equals(SystemAccount))
                throw new UnauthorizedAccessException("安装目录允许普通用户修改，未创建管理员启动任务。");
    }

    private sealed class Scheduler : IDisposable
    {
        private readonly dynamic service;
        internal dynamic Folder { get; }
        internal Scheduler()
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            try { service.Connect(); Folder = service.GetFolder("\\"); }
            catch { Marshal.FinalReleaseComObject(service); throw; }
        }
        internal dynamic? FindTask()
        {
            try { return Folder.GetTask(TaskName); }
            catch (FileNotFoundException) { return null; }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
        }
        public void Dispose() { Marshal.FinalReleaseComObject(Folder); Marshal.FinalReleaseComObject(service); }
    }
}

internal readonly record struct PersistentStartupStatus(bool IsInstalled, bool IsEnabled, string? ExecutablePath);
