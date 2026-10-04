namespace RemoteDesk;

// Connection preferences only: never stores a password or private-key content.
internal sealed record SshTerminalProfile(string HostKeyAlias, string Username, string? IdentityFile)
{
    internal static List<SshTerminalProfile> Normalize(IEnumerable<SshTerminalProfile?>? profiles) => (profiles ?? [])
        .Where(profile => profile is not null && IsValidAlias(profile.HostKeyAlias) && SshTerminalLauncher.IsValidUsername(profile.Username) &&
            (string.IsNullOrEmpty(profile.IdentityFile) || profile.IdentityFile.Length <= 32767 &&
                Path.IsPathFullyQualified(profile.IdentityFile) && !profile.IdentityFile.Any(char.IsControl)))
        .Select(profile => profile! with { HostKeyAlias = profile!.HostKeyAlias.ToLowerInvariant() })
        .DistinctBy(profile => profile.HostKeyAlias).Take(50).ToList();

    private static bool IsValidAlias(string? value) => value is { Length: 116 } &&
        value.StartsWith("remotedesk-", StringComparison.OrdinalIgnoreCase) && value.EndsWith(":ssh", StringComparison.Ordinal) &&
        value.AsSpan(11, 64).ToArray().All(Uri.IsHexDigit) && value[75] == '-' && Guid.TryParse(value.Substring(76, 36), out Guid id) && id != Guid.Empty;
}
