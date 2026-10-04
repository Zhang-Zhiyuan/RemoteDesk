using System.Text;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;

namespace RemoteDesk;

internal sealed record SshAliasConfigResult(string? BackupPath);

// Only our include/profile blocks are mutable. Existing user configuration is
// retained byte-for-byte (including its original BOM and newline convention).
internal static class SshAliasConfig
{
    private const int MaximumConfigBytes = 1024 * 1024;
    private const string IncludeBegin = "# BEGIN REMOTEDESK SSH INCLUDE v1";
    private const string IncludeEnd = "# END REMOTEDESK SSH INCLUDE v1";
    private const string ManagedHeader = "# RemoteDesk SSH terminal aliases v1\n";
    private const string ProfileBegin = "# BEGIN REMOTEDESK SSH PROFILE ";
    private const string ProfileEnd = "# END REMOTEDESK SSH PROFILE ";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly object Sync = new();
    private sealed record Block(Guid Id, string Alias, string Text);

    internal static bool IsValidAlias(string? alias) => alias is not null &&
        Regex.IsMatch(alias, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant);

    internal static SshAliasConfigResult Install(SshCommandProfile profile, string helperPath,
        string sshConfigPath, string managedConfigPath, string knownHostsPath)
    {
        lock (Sync)
        {
            profile = profile.Validate();
            if (!IsValidAlias(profile.Alias)) throw new ArgumentException("SSH 别名只能使用 1–64 个英文字母、数字、横线或下划线，且须以字母或数字开头。");
            ValidatePaths(sshConfigPath, managedConfigPath, knownHostsPath);
            if (!Path.IsPathFullyQualified(helperPath) || !File.Exists(helperPath))
                throw new FileNotFoundException("SSH 命令行助手不可用，请先发布或重新安装完整版本。", helperPath);
            using var userDirectories = SshProxyDeployment.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(sshConfigPath))!);
            using var managedDirectories = SshProxyDeployment.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(managedConfigPath))!);
            using var mutationLock = new MutationLease(managedConfigPath);
            byte[]? userBefore = Read(sshConfigPath), managedBefore = Read(managedConfigPath);
            (byte[] bom, string body) = UserBody(userBefore, managedConfigPath);
            CheckConflicts(body, profile.Alias);
            List<Block> blocks = ReadBlocks(managedBefore);
            if (blocks.Any(block => block.Id != profile.Id && string.Equals(block.Alias, profile.Alias, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("此 SSH 别名已属于另一条 RemoteDesk 配置，请换一个别名。");
            blocks.RemoveAll(block => block.Id == profile.Id);
            blocks.Add(new Block(profile.Id, profile.Alias, Render(profile, helperPath, knownHostsPath)));
            string include = IncludeBegin + "\nInclude " + ConfigPath(managedConfigPath, allowGlob: false) + "\nHost *\n" + IncludeEnd + "\n";
            byte[] userAfter = Combine(bom, Utf8.GetBytes(include + body));
            byte[] managedAfter = Utf8.GetBytes(ManagedHeader + string.Concat(blocks.Select(block => block.Text)));
            return Apply(sshConfigPath, userBefore, userAfter, managedConfigPath, managedBefore, managedAfter);
        }
    }

    internal static SshAliasConfigResult Remove(SshCommandProfile profile, string helperPath,
        string sshConfigPath, string managedConfigPath, string knownHostsPath)
    {
        lock (Sync)
        {
            ValidatePaths(sshConfigPath, managedConfigPath, knownHostsPath);
            using var userDirectories = SshProxyDeployment.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(sshConfigPath))!);
            using var managedDirectories = SshProxyDeployment.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(managedConfigPath))!);
            using var mutationLock = new MutationLease(managedConfigPath);
            byte[]? userBefore = Read(sshConfigPath), managedBefore = Read(managedConfigPath);
            (byte[] bom, string body) = UserBody(userBefore, managedConfigPath);
            List<Block> blocks = ReadBlocks(managedBefore);
            if (blocks.RemoveAll(block => block.Id == profile.Id) == 0) return new(null);
            string include = blocks.Count == 0 ? "" : IncludeBegin + "\nInclude " + ConfigPath(managedConfigPath, allowGlob: false) + "\nHost *\n" + IncludeEnd + "\n";
            return Apply(sshConfigPath, userBefore, Combine(bom, Utf8.GetBytes(include + body)),
                managedConfigPath, managedBefore, Utf8.GetBytes(ManagedHeader + string.Concat(blocks.Select(block => block.Text))));
        }
    }

    private static SshAliasConfigResult Apply(string userPath, byte[]? userBefore, byte[] userAfter,
        string managedPath, byte[]? managedBefore, byte[] managedAfter)
    {
        if (userAfter.Length > MaximumConfigBytes || managedAfter.Length > MaximumConfigBytes)
            throw new IOException("生成的 SSH 配置超过 1 MiB，原配置和受管配置均未修改。请缩减配置后重试。");
        WriteAtomic(managedPath, managedBefore, managedAfter, backup: false);
        try { return new(WriteAtomic(userPath, userBefore, userAfter, backup: true)); }
        catch
        {
            // Never roll back over a concurrent external edit.
            byte[]? current = Read(managedPath);
            if (current is not null && current.AsSpan().SequenceEqual(managedAfter))
            {
                if (managedBefore is null) File.Delete(managedPath);
                else WriteAtomic(managedPath, managedAfter, managedBefore, backup: false);
            }
            throw;
        }
    }

    private static string? WriteAtomic(string path, byte[]? before, byte[] after, bool backup)
    {
        if (before is not null && before.AsSpan().SequenceEqual(after)) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        ValidateNoLinks(path);
        string temporary = path + ".remotedesk-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(after); file.Flush(flushToDisk: true); }
            // OpenSSH rejects included configuration writable by unrelated users.
            // New files must not inherit a permissive temp/AppData parent DACL.
            var access = new FileSecurity();
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户身份。");
            access.SetOwner(owner); access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (SecurityIdentifier identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                access.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(temporary).SetAccessControl(access);
            byte[]? current = Read(path);
            if ((before is null) != (current is null) || before is not null && !before.AsSpan().SequenceEqual(current))
                throw new IOException("SSH 配置已被其他程序更改，请重新检查后再试；未覆盖新内容。");
            if (before is null) { File.Move(temporary, path); return null; }
            string? backupPath = backup ? path + ".remotedesk-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") : null;
            File.Replace(temporary, path, backupPath);
            return backupPath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[]? Read(string path)
    {
        ValidateNoLinks(path);
        if (!File.Exists(path)) return null;
        using var handle = SshProxyDeployment.OpenPath(path, directory: false);
        using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length > MaximumConfigBytes) throw new IOException("SSH 配置超过 1 MiB，未自动修改。");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }

    private static void ValidatePaths(params string[] paths)
    {
        if (paths.Any(path => !Path.IsPathFullyQualified(path)) || paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ArgumentException("SSH 配置路径必须为不同的绝对路径。");
        foreach (string path in paths) ValidateNoLinks(path);
    }

    private static void ValidateNoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("SSH 配置路径含链接或重定向，未自动修改；请手动配置。");
        }
    }

    private static (byte[] Bom, string Body) UserBody(byte[]? value, string managedConfigPath)
    {
        value ??= [];
        bool hasBom = value.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        byte[] bom = hasBom ? [0xef, 0xbb, 0xbf] : [];
        string text;
        try { text = Utf8.GetString(value, bom.Length, value.Length - bom.Length); }
        catch (DecoderFallbackException) { throw new IOException("SSH 配置不是 UTF-8 文本，未自动修改；请手动配置。"); }
        if (text.Contains('\0')) throw new IOException("SSH 配置包含无效字符，未自动修改。");
        if (!text.StartsWith(IncludeBegin + "\n", StringComparison.Ordinal))
        {
            if (text.Contains(IncludeBegin, StringComparison.Ordinal) || text.Contains(IncludeEnd, StringComparison.Ordinal))
                throw new IOException("RemoteDesk SSH 配置标记已被修改，未自动覆盖。");
            return (bom, text);
        }
        int end = text.IndexOf(IncludeEnd + "\n", StringComparison.Ordinal);
        if (end < 0) throw new IOException("RemoteDesk SSH 配置标记不完整，未自动覆盖。");
        string expected = IncludeBegin + "\nInclude " + ConfigPath(managedConfigPath, allowGlob: false) + "\nHost *\n" + IncludeEnd + "\n";
        if (!text.StartsWith(expected, StringComparison.Ordinal))
            throw new IOException("RemoteDesk SSH Include 内容已被修改，未自动覆盖。");
        string body = text[(end + IncludeEnd.Length + 1)..];
        if (body.Contains(IncludeBegin, StringComparison.Ordinal) || body.Contains(IncludeEnd, StringComparison.Ordinal))
            throw new IOException("SSH 配置含重复管理标记，未自动覆盖。");
        return (bom, body);
    }

    private static void CheckConflicts(string body, string alias)
    {
        foreach (string line in body.Split('\n'))
        {
            List<string> tokens = Tokens(line);
            if (tokens.Count == 0) continue;
            if (tokens[0].Equals("Include", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("Match", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("现有 SSH 配置含 Include / Match，无法安全自动合并。原配置未修改，请手动添加专用配置。");
            if (!tokens[0].Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            string[] patterns = tokens.Skip(1).ToArray();
            if (patterns.Any(pattern => pattern.Contains('\\')))
                throw new InvalidOperationException("现有 SSH Host 模式含转义字符，无法安全自动合并；原配置未修改。");
            if (patterns.Length == 1 && patterns[0] == "*") continue;
            bool matches = patterns.Any(pattern => !pattern.StartsWith('!') && Match(pattern, alias));
            if (matches && !patterns.Any(pattern => pattern.StartsWith('!') && Match(pattern[1..], alias)))
                throw new InvalidOperationException("现有 SSH Host 配置已匹配别名“" + alias + "”；未覆盖，请换一个别名。");
        }
    }

    private static bool Match(string pattern, string alias) => Regex.IsMatch(alias,
        "\\A" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>(); var current = new StringBuilder(); bool quoted = false;
        foreach (char character in line.TrimEnd('\r'))
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (!quoted && character == '#') break;
            if (!quoted && (char.IsWhiteSpace(character) || character == '='))
            { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } }
            else current.Append(character);
        }
        if (quoted) throw new IOException("SSH 配置含不完整引号，未自动修改。");
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static List<Block> ReadBlocks(byte[]? bytes)
    {
        if (bytes is null) return [];
        string text = Utf8.GetString(bytes);
        if (!text.StartsWith(ManagedHeader, StringComparison.Ordinal)) throw new IOException("专用 SSH 配置文件不是 RemoteDesk 管理的格式，未覆盖。");
        var result = new List<Block>(); int position = ManagedHeader.Length;
        while (position < text.Length)
        {
            int lineEnd = text.IndexOf('\n', position);
            if (lineEnd < 0 || !text.AsSpan(position).StartsWith(ProfileBegin) ||
                !Guid.TryParseExact(text[(position + ProfileBegin.Length)..lineEnd], "D", out Guid id))
                throw new IOException("专用 SSH 配置块已被修改，未覆盖。");
            string ending = ProfileEnd + id.ToString("D") + "\n";
            int end = text.IndexOf(ending, lineEnd, StringComparison.Ordinal);
            if (end < 0) throw new IOException("专用 SSH 配置块不完整，未覆盖。");
            string block = text[position..(end + ending.Length)];
            string hostLine = block.Split('\n').Skip(1).FirstOrDefault() ?? "";
            string alias = hostLine.StartsWith("Host ", StringComparison.Ordinal) ? hostLine[5..] : "";
            if (!IsValidAlias(alias) || result.Any(item => item.Id == id)) throw new IOException("专用 SSH 配置块身份无效或重复，未覆盖。");
            result.Add(new Block(id, alias, block)); position = end + ending.Length;
        }
        return result;
    }

    private static string Render(SshCommandProfile profile, string helperPath, string knownHostsPath)
    {
        string id = profile.Id.ToString("D");
        string result = ProfileBegin + id + "\nHost " + profile.Alias + "\n" +
            "  HostName " + id + ".remotedesk.invalid\n" +
            "  User " + profile.Username + "\n" +
            "  ProxyCommand " + ConfigPath(helperPath) + " --stdio " + id + "\n" +
            "  HostKeyAlias " + SshTerminalLauncher.HostKeyAlias(profile.RelayPin, profile.DeviceId) + "\n" +
            "  StrictHostKeyChecking ask\n  CheckHostIP no\n" +
            "  UserKnownHostsFile " + ConfigPath(knownHostsPath) + "\n  GlobalKnownHostsFile NUL\n" +
            "  ForwardAgent no\n  ForwardX11 no\n  PermitLocalCommand no\n  ProxyUseFdpass no\n" +
            "  ControlMaster no\n  ControlPath none\n  CanonicalizeHostname no\n" +
            "  ServerAliveInterval 30\n  ServerAliveCountMax 3\n  ConnectTimeout 20\n";
        if (!string.IsNullOrWhiteSpace(profile.IdentityFile)) result += "  IdentityFile " + ConfigPath(profile.IdentityFile) + "\n  IdentitiesOnly yes\n";
        return result + ProfileEnd + id + "\n";
    }

    private static string ConfigPath(string path, bool allowGlob = true)
    {
        string full = Path.GetFullPath(path).Replace('\\', '/');
        if (!allowGlob && full.Contains('%'))
            throw new ArgumentException("专用 SSH 配置路径含百分号（%）；不同 OpenSSH 版本的 Include 解析规则不一致，未自动修改。请使用不含百分号的目录，或手动配置 SSH。");
        if (full.Any(char.IsControl) || full.Contains('"') || full.Contains("${", StringComparison.Ordinal) ||
            !allowGlob && full.IndexOfAny(['*', '?', '[', ']']) >= 0)
            throw new ArgumentException("SSH 文件路径含无法安全转义的字符，请使用普通本地路径。");
        // Include did not expand percent tokens in older Windows OpenSSH but
        // current versions do. Refuse percent in this one path, since no spelling
        // keeps it literal across upgrades. Other directives support %% in both.
        return "\"" + (allowGlob ? full.Replace("%", "%%", StringComparison.Ordinal) : full) + "\"";
    }

    private static byte[] Combine(byte[] prefix, byte[] body)
    { byte[] result = new byte[prefix.Length + body.Length]; prefix.CopyTo(result, 0); body.CopyTo(result, prefix.Length); return result; }

    private sealed class MutationLease : IDisposable
    {
        private readonly Mutex _mutex;
        internal MutationLease(string managedPath)
        {
            string id = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Path.GetFullPath(managedPath).ToUpperInvariant())));
            _mutex = new Mutex(false, @"Local\RemoteDesk-SshConfig-" + id);
            try
            {
                if (!_mutex.WaitOne(0)) throw new IOException("另一 RemoteDesk 实例正在更新 SSH 配置，请稍后重试。");
            }
            catch (AbandonedMutexException) { } // Revalidate both files after a previous process exited.
            catch { _mutex.Dispose(); throw; }
        }
        public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
    }
}
