using System.Diagnostics;
using System.Text;

namespace RemoteDesk;

internal static class SshPublicKeyPairing
{
    private static readonly string OpenSsh = Path.Combine(Environment.SystemDirectory, "OpenSSH");
    private static readonly string[] SessionOptions = ["-T", "-o", "ControlPath=none", "-o", "ControlMaster=no",
        "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "ConnectTimeout=20", "-o", "RemoteCommand=none"];
    private const string Marker = "RemoteDesk-publickey-ok";

    internal static async Task RunAsync(string[] args, CancellationToken token)
    {
        string[] route;
        string? expectedHost = null;
        string label;
        if (args is ["--pair", var id] && Guid.TryParseExact(id, "D", out var guid))
        {
            var profile = SshCommandProfiles.Read(guid);
            route = ["-F", SshCommandProfiles.ManagedConfigPath, "--", profile.Alias];
            expectedHost = profile.Id.ToString("D") + ".remotedesk.invalid";
            label = profile.Username + "@" + profile.Alias;
        }
        else if (args is ["--pair-direct", var host, var port, var user])
        {
            var target = DirectSshTarget.Create(host, port == "-" ? "" : port, user == "-" ? "" : user);
            route = target.TerminalArguments();
            label = (target.Username.Length == 0 ? "" : target.Username + "@") + target.Host;
        }
        else throw new ArgumentException("请从目标设备的 SSH 选项打开免密配对。");

        await PairAsync(route, label, expectedHost, token);
    }

    internal static async Task PairAsync(string[] route, string label, string? expectedHost, CancellationToken token)
    {
        Console.WriteLine($"正在检查 {label} 的 SSH 公钥登录…");
        var config = await Run("ssh.exe", ["-G", "-T", ..route], token, capture: true);
        string[] lines = config.Output.Replace("\r", "").Split('\n');
        if (config.Status != 0 || expectedHost != null && !lines.Contains("hostname " + expectedHost, StringComparer.OrdinalIgnoreCase))
            throw new IOException("此设备的 SSH 配置已改变，请在 RemoteDesk 中重新配置别名。");
        async Task<bool> Works()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            (int Status, string Output) probe;
            try { probe = await Run("ssh.exe", [..SessionOptions, "-n", "-o", "BatchMode=yes", "-o",
                "PreferredAuthentications=publickey", "-o", "StrictHostKeyChecking=yes", ..route, "echo " + Marker],
                timeout.Token, capture: true, quiet: true); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("SSH 登录检查超时，请检查目标设备的连接。"); }
            return probe.Status == 0 && probe.Output.Split('\n').Any(line => line.TrimEnd('\r') == Marker);
        }
        if (await Works())
        {
            Console.WriteLine("此电脑已经可以免密登录。终端和 VS Code 均可直接连接。");
            return;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = lines.Where(line => line.StartsWith("identityfile ", StringComparison.Ordinal))
            .Select(line => line["identityfile ".Length..].Trim())
            .Select(path => path.StartsWith("~/") ? Path.Combine(home, path[2..]) : path)
            .Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).ToList();
        string? identity = candidates.FirstOrDefault(File.Exists);
        if (identity is null)
        {
            string normal = Path.Combine(home, ".ssh", "id_ed25519");
            if (!candidates.Contains(normal, StringComparer.OrdinalIgnoreCase) || File.Exists(normal + ".pub"))
                throw new IOException("请在 SSH 登录设置中选择本机已有的私钥，或先用 ssh-keygen 建立密钥对。");
            Directory.CreateDirectory(Path.GetDirectoryName(normal)!);
            Console.WriteLine("正在当前 Windows 账户创建 Ed25519 登录密钥。");
            // Generate under a unique name. Never replace a concurrently created identity.
            string temporary = normal + "." + Guid.NewGuid().ToString("N");
            try
            {
                if ((await Run("ssh-keygen.exe", ["-q", "-t", "ed25519", "-N", "", "-C", "RemoteDesk", "-f", temporary], token)).Status != 0)
                    throw new IOException("OpenSSH 未能创建本机密钥。");
                File.Move(temporary, normal);
                File.Move(temporary + ".pub", normal + ".pub");
            }
            finally { File.Delete(temporary); File.Delete(temporary + ".pub"); }
            identity = normal;
        }
        string publicKey;
        string publicPath = identity.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? identity : identity + ".pub";
        if (File.Exists(publicPath))
        {
            using var file = File.OpenRead(publicPath);
            if (file.Length > 24576) throw new InvalidDataException("SSH 公钥文件过大。");
            using var reader = new StreamReader(file);
            publicKey = await reader.ReadToEndAsync(token);
        }
        else
        {
            var derived = await Run("ssh-keygen.exe", ["-y", "-f", identity], token, capture: true);
            if (derived.Status != 0) throw new IOException("OpenSSH 无法取得所选身份的公钥。");
            publicKey = derived.Output;
        }
        string[] fields = publicKey.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte[] blob = new byte[16384];
        if (fields.Length < 2 || fields[0] is not ("ssh-ed25519" or "ssh-rsa" or "ecdsa-sha2-nistp256" or
                "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" or "sk-ssh-ed25519@openssh.com" or "sk-ecdsa-sha2-nistp256@openssh.com") ||
            fields[1].Length > 22000 || !Convert.TryFromBase64String(fields[1], blob, out int count) || count <= 4 + fields[0].Length ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob) != fields[0].Length ||
            Encoding.ASCII.GetString(blob, 4, fields[0].Length) != fields[0])
            throw new InvalidDataException("所选文件没有有效的 OpenSSH 公钥。");
        using var resource = typeof(SshPublicKeyPairing).Assembly.GetManifestResourceStream("RemoteDesk.AuthorizeKey.py")!;
        using var script = new StreamReader(resource);
        string remote = "python3 -c '" + (await script.ReadToEndAsync(token)).Replace("\r\n", "\n").Replace("'", "'\"'\"'") + "'";
        Console.WriteLine($"请按终端提示确认主机并登录 {label}。公钥将登记到这个 Linux / WSL 账号。");
        var install = await Run("ssh.exe", [..SessionOptions, "-o", "StrictHostKeyChecking=ask", ..route, remote],
            token, input: fields[0] + " " + fields[1] + "\n");
        if (install.Status != 0) throw new IOException("登记未完成，请查看上方的 SSH 提示，并确认目标账号可运行 Python 3。");
        if (!await Works()) throw new IOException("已提交公钥，但独立的公钥登录未通过。若本机私钥有口令，请先用 ssh-add 解锁；也可检查目标 sshd 的授权文件设置。");
        Console.WriteLine($"已验证 {label} 的免密登录。终端和 VS Code 现在都可以使用。");
    }

    private static async Task<(int Status, string Output)> Run(string program, string[] arguments,
        CancellationToken token, bool capture = false, bool quiet = false, string? input = null)
    {
        var start = new ProcessStartInfo(Path.Combine(OpenSsh, program)) { UseShellExecute = false,
            RedirectStandardInput = input != null, RedirectStandardOutput = capture, RedirectStandardError = quiet };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        if (input != null) start.StandardInputEncoding = new UTF8Encoding(false);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows OpenSSH。");
        try
        {
            var output = capture ? process.StandardOutput.ReadToEndAsync(token) : Task.FromResult("");
            var errors = quiet ? process.StandardError.BaseStream.CopyToAsync(Stream.Null, token) : Task.CompletedTask;
            if (input != null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), token);
                process.StandardInput.Close();
            }
            await Task.WhenAll(process.WaitForExitAsync(token), output, errors);
            return (process.ExitCode, await output);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
