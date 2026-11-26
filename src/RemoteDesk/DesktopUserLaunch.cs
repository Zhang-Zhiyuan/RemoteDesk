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

    internal static void SignIn(bool codex)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = string.Join(Path.PathSeparator, new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User,
            EnvironmentVariableTarget.Machine }.Select(target => Environment.GetEnvironmentVariable("PATH", target)));
        string tool = codex ? "codex" : "claude";
        string executable = FindLoginClient(codex, path, Path.Combine(home, ".vscode", "extensions"))
            ?? throw new FileNotFoundException($"未找到 {tool}。请安装对应 CLI，或将它加入当前用户的 PATH 后重试。");
        Open(executable, codex ? "login --device-auth" : "auth login");
    }

    internal static string? FindLoginClient(bool codex, string searchPath, string extensions)
    {
        string name = codex ? "codex" : "claude";
        foreach (string entry in searchPath.Split(Path.PathSeparator))
        {
            string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (string suffix in new[] { ".exe", ".cmd", ".bat" })
            {
                string path = Path.Combine(directory, name + suffix);
                if (File.Exists(path)) return path;
            }
        }
        if (!codex || !Directory.Exists(extensions)) return null;
        string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "windows-aarch64" : "windows-x86_64";
        return new DirectoryInfo(extensions).EnumerateDirectories("openai.chatgpt-*")
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .Select(directory => Path.Combine(directory.FullName, "bin", architecture, "codex.exe"))
            .FirstOrDefault(File.Exists);
    }

    internal static string[] EditorArguments(string alias) => SshAliasConfig.IsValidAlias(alias)
        ? ["--new-window", "--remote", "ssh-remote+" + alias] : throw new ArgumentException("SSH 别名无效。");

    internal static void OpenEditor(string alias) => StartEditor(string.Join(" ", EditorArguments(alias)));

    internal static void OpenDirectSsh(DirectSshTarget target, bool editor)
    {
        if (editor) { StartEditor("--new-window --remote " + target.EditorAuthority()); return; }
        string executable = SshTerminalLauncher.ExecutablePath;
        if (!File.Exists(executable)) throw new FileNotFoundException("请在 Windows 可选功能中安装 OpenSSH 客户端。");
        Open(executable, string.Join(" ", target.TerminalArguments()));
    }

    private static void StartEditor(string args)
    {
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
