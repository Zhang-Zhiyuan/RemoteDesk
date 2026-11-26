using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static class DesktopUserLaunch
{
    // The host may be elevated. Launch developer tools through the interactive
    // Explorer desktop so they use the same account and privileges as Start.
    private static void Open(string executable, string arguments)
    {
        object? shell = null, windows = null, desktop = null, document = null, application = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            windows = ((dynamic)shell!).Windows();
            object location = 0, root = 0;
            int handle = 0;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out handle, 1);
            if (desktop is null) throw new InvalidOperationException("请在已登录且运行着资源管理器的 Windows 桌面重试。");
            document = ((dynamic)desktop).Document;
            application = ((dynamic)document).Application;
            ((dynamic)application).ShellExecute(executable, arguments, Environment.SystemDirectory, "open", 1);
        }
        finally
        {
            foreach (var item in new[] { application, document, desktop, windows, shell })
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }

    internal static void SignIn(bool codex) =>
        Open(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /k " + DeveloperLogin.Command(codex));

    internal static string[] EditorArguments(string alias) => SshAliasConfig.IsValidAlias(alias)
        ? ["--new-window", "--remote", "ssh-remote+" + alias] : throw new ArgumentException("SSH 别名无效。");

    internal static void OpenEditor(string alias)
    {
        string args = string.Join(" ", EditorArguments(alias));
        const string key = @"\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Code.exe";
        var candidates = new List<string?> {
            Registry.GetValue("HKEY_CURRENT_USER" + key, "", null) as string,
            Registry.GetValue("HKEY_LOCAL_MACHINE" + key, "", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe")
        };
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, "code.cmd")) &&
                Directory.GetParent(directory) is { } installation)
                candidates.Add(Path.Combine(installation.FullName, "Code.exe"));
        }
        string binary = candidates.FirstOrDefault(path => path != null && Path.IsPathFullyQualified(path) && File.Exists(path))
            ?? throw new FileNotFoundException("请安装桌面版 VS Code 和 Remote - SSH 扩展，再点“用 VS Code 打开”。");
        Open(binary, args);
    }
}
