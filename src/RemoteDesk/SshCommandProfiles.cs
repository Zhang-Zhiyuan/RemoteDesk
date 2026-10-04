using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteDesk;

internal sealed record SshCommandProfile(Guid Id, string Alias, string DeviceId, string RelayScope,
    string RelayPin, string Username, string? IdentityFile)
{
    internal SshCommandProfile Validate()
    {
        if (Id == Guid.Empty || !SshCommandProfiles.IsValidAlias(Alias) ||
            !Guid.TryParse(DeviceId, out Guid device) || device == Guid.Empty ||
            RelayScope is not { Length: 64 } || !RelayScope.All(Uri.IsHexDigit) ||
            RelayPin is not { Length: 64 } || !RelayPin.All(Uri.IsHexDigit) ||
            Username is not { Length: > 0 and <= 128 } ||
            !Username.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '@' or '\\'))
            throw new InvalidDataException("终端 SSH 别名配置无效，请在 RemoteDesk 中重新配置。");
        string? identity = string.IsNullOrWhiteSpace(IdentityFile) ? null : IdentityFile;
        if (identity is not null && (!Path.IsPathFullyQualified(identity) || identity.Any(char.IsControl) ||
            identity.Contains('"') || identity.Contains("${", StringComparison.Ordinal)))
            throw new InvalidDataException("SSH 私钥路径无效，请重新选择完整文件路径。");
        return this with { Alias = Alias.ToLowerInvariant(), DeviceId = device.ToString("D"),
            RelayScope = RelayScope.ToUpperInvariant(), RelayPin = RelayPin.ToUpperInvariant(),
            IdentityFile = identity is null ? null : Path.GetFullPath(identity) };
    }

    public override string ToString() => $"SshCommandProfile({Alias}, {Id:D})";
}

/// <summary>Public routing metadata only. Device/relay credentials remain in the current user's existing settings.</summary>
internal static class SshCommandProfiles
{
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk", "ssh");
    internal static string ManagedConfigPath => Path.Combine(DirectoryPath, "terminal_config");
    internal static string KnownHostsPath => Path.Combine(DirectoryPath, "known_hosts");
    internal static string DefaultSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk", "settings.json");
    internal static bool IsValidAlias(string? alias) => alias is not null &&
        Regex.IsMatch(alias, "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\z", RegexOptions.CultureInvariant);

    internal static string ComputeRelayScope(RelayConnectionOptions options) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(options.ServerAddress.Trim().TrimEnd('.').ToLowerInvariant() + "\n" + options.Port + "\n" +
            RelayTls.NormalizeFingerprint(options.TlsCertificateSha256))));

    internal static IReadOnlyList<SshCommandProfile> ReadAll(string? directory = null)
    {
        string profiles = ProfilesDirectory(directory);
        if (!Directory.Exists(profiles)) return [];
        RejectReparsePoint(profiles);
        string[] paths = Directory.GetFiles(profiles, "*.json");
        if (paths.Length > 50) throw new InvalidDataException("终端 SSH 别名超过 50 个，请先移除不需要的别名。");
        var result = paths.Select(ReadFile).ToArray();
        if (result.Select(p => p.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new InvalidDataException("终端 SSH 别名重复，请修复别名配置后重试。");
        return result;
    }

    internal static SshCommandProfile Read(Guid id, string? directory = null)
    {
        if (id == Guid.Empty) throw new InvalidDataException("终端 SSH 配置标识无效。");
        string profiles = ProfilesDirectory(directory);
        RejectReparsePoint(profiles);
        return ReadFile(Path.Combine(profiles, id.ToString("D") + ".json"));
    }

    internal static void Save(SshCommandProfile profile, string? directory = null)
    {
        profile = profile.Validate();
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(profile);
        if (serialized.Length > 65536)
            throw new InvalidDataException("终端 SSH 别名记录过大，请缩短私钥路径；现有配置未修改。");
        string profiles = ProfilesDirectory(directory);
        using var pinned = SshFileSafety.PinDirectories(profiles);
        RejectReparsePoint(profiles);
        RejectReparsePointIfPresent(Path.Combine(profiles, ".lock"));
        using var locked = new FileStream(Path.Combine(profiles, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        IReadOnlyList<SshCommandProfile> entries = ReadAll(directory);
        if (entries.Any(p => p.Id != profile.Id && p.Alias.Equals(profile.Alias, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("此终端 SSH 别名已存在，请使用其他名称。");
        var previous = entries.FirstOrDefault(p => p.Id == profile.Id);
        if (previous is not null && (previous.DeviceId != profile.DeviceId || previous.RelayScope != profile.RelayScope))
            throw new InvalidDataException("此别名已指向其他设备，请先移除旧别名，不能静默替换目标。");
        if (previous is null && entries.Count >= 50) throw new InvalidDataException("终端 SSH 别名已达到 50 个，请先移除一个。");
        string target = Path.Combine(profiles, profile.Id.ToString("D") + ".json");
        if (File.Exists(target)) RejectReparsePoint(target);
        string temporary = Path.Combine(profiles, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(serialized);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Remove(Guid id, string? directory = null)
    {
        if (id == Guid.Empty) throw new InvalidDataException("终端 SSH 配置标识无效。");
        string profiles = ProfilesDirectory(directory);
        if (!Directory.Exists(profiles)) return;
        using var pinned = SshFileSafety.PinDirectories(profiles);
        RejectReparsePoint(profiles);
        RejectReparsePointIfPresent(Path.Combine(profiles, ".lock"));
        using var locked = new FileStream(Path.Combine(profiles, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string path = Path.Combine(profiles, id.ToString("D") + ".json");
        if (File.Exists(path)) { _ = ReadFile(path); File.Delete(path); }
    }

    private static string ProfilesDirectory(string? directory) => Path.Combine(directory ?? DirectoryPath, "profiles");
    private static SshCommandProfile ReadFile(string path)
    {
        RejectReparsePoint(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 65536) throw new InvalidDataException("终端 SSH 别名文件过大。");
            var profile = (JsonSerializer.Deserialize<SshCommandProfile>(file) ?? throw new InvalidDataException("终端 SSH 别名为空。")).Validate();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out Guid id) || profile.Id != id)
                throw new InvalidDataException("终端 SSH 别名文件身份不匹配。");
            return profile;
        }
        catch (JsonException) { throw new InvalidDataException("终端 SSH 别名文件损坏，原文件未修改。"); }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("终端 SSH 配置不能通过符号链接重定向，请使用普通文件和目录。");
    }

    private static void RejectReparsePointIfPresent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) RejectReparsePoint(path);
    }
}
