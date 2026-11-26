param([switch]$EnableDisabled)
# Only an explicit action on this Windows host supplies EnableDisabled.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$stage = 'inspection'
trap { 'PREP_ERROR {0} 0x{1:X8}' -f $stage,$_.Exception.HResult; break }

function Write-CheckedConfig([string]$path, [byte[]]$contents, [byte[]]$expected) {
    $staged = Join-Path (Split-Path -Parent $path) ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $file = [IO.File]::Open($staged,'CreateNew','Write','None')
        try { $file.Write($contents,0,$contents.Length); $file.Flush($true) } finally { $file.Dispose() }
        if ($null -eq $expected) { [IO.File]::Move($staged,$path); return $true }
        $original = [IO.File]::Open($path,'Open','Read',[IO.FileShare]'Read,Delete')
        try {
            if ($original.Length -ne $expected.Length) { return $false }
            $copy = [IO.MemoryStream]::new()
            try { $original.CopyTo($copy); $same = [Convert]::ToBase64String($copy.ToArray()) -ceq [Convert]::ToBase64String($expected) }
            finally { $copy.Dispose() }
            if (!$same) { return $false }
            [IO.File]::Replace($staged,$path,[Management.Automation.Language.NullString]::Value)
            return $true
        } finally { $original.Dispose() }
    } finally { if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) } }
}
$service = Get-Service -Name sshd -ErrorAction SilentlyContinue
if ($service.Status -eq 'Running') { 'READY'; return }
$wasDisabled = $service -and $service.StartType -eq 'Disabled'
if ($wasDisabled -and !$EnableDisabled) { 'DISABLED'; return }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { 'ADMIN_REQUIRED'; return }
$base = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ssh'
$config = Join-Path $base 'sshd_config'
$binary = Join-Path ([Environment]::SystemDirectory) 'OpenSSH\sshd.exe'
# Windows may come with the optional feature preinstalled but never started.
$fresh = !(Test-Path -LiteralPath $config)
if (!$service) {
    $stage = 'installation'
    $rule = 'OpenSSH-Server-In-TCP'
    $hadRule = Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue
    try {
        $install = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
    } catch {
        'INSTALL_FAILED 0x{0:X8}' -f $_.Exception.HResult; return
    } finally {
        # Windows adds an inbound rule when installing the optional feature.
        if (!$hadRule) {
            $addedRule = Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue
            if ($addedRule) { $addedRule | Disable-NetFirewallRule | Out-Null }
        }
    }
    if ($install.RestartNeeded) { 'RESTART_NEEDED'; return }
    $service = Get-Service -Name sshd -ErrorAction SilentlyContinue
    if (!$service) { 'INSTALL_FAILED'; return }
    if ($service.StartType -eq 'Disabled' -and !$EnableDisabled) { 'DISABLED'; return }
}
$stage = 'configuration'
$image = (Get-CimInstance Win32_Service -Filter "Name='sshd'").PathName
if (!$image -or [Environment]::ExpandEnvironmentVariables($image.Trim().Trim('"')) -ne $binary -or
    !(Test-Path -LiteralPath $binary -PathType Leaf)) { 'CUSTOM_SERVICE'; return }
# A stopped portproxy can reserve a port without showing up in Get-NetTCPConnection.
# Read the IP Helper portproxy configuration; do not change or restart it.
$reserved = @(foreach ($mode in 'v4tov4','v4tov6','v6tov4','v6tov6') {
    $path = "HKLM:\SYSTEM\CurrentControlSet\Services\PortProxy\$mode\tcp"
    if (Test-Path -LiteralPath $path) {
        foreach ($name in (Get-Item -LiteralPath $path).GetValueNames()) {
            if ($name -match '/(\d+)$') { [int]$matches[1] }
        }
    }
})
$configExists = Test-Path -LiteralPath $config -PathType Leaf
$stock = $null
$defaults = $null
$listening = @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | ForEach-Object Port)
if ($configExists -and ($fresh -or ($wasDisabled -and $EnableDisabled -and (22 -in $reserved -or 22 -in $listening)))) {
    $template = Join-Path (Split-Path -Parent $binary) 'sshd_config_default'
    if ((Test-Path -LiteralPath $template -PathType Leaf) -and (Get-Item -LiteralPath $config).Length -le 65536) {
        $defaults = [IO.File]::ReadAllBytes($template)
        $text = [Text.Encoding]::UTF8.GetString($defaults)
        # Only the unmodified Microsoft template with implicit listener defaults can
        # be assigned a relay-only port. Preserve all its other settings and comments.
        if ($text -notmatch '(?im)^\s*(?:port|listenaddress|include)\s+' -and
            [Convert]::ToBase64String($defaults) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($config))) {
            $stock = $text.TrimStart([char]0xfeff)
        }
    }
    if ($fresh -and $null -eq $stock) { 'CONFIG_CHECK'; return }
}
if ($fresh -or $null -ne $stock) {
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    if (!(Test-Path -LiteralPath $base)) {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner($admins)
        foreach ($owner in $admins,$system) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        }
        [IO.Directory]::CreateDirectory($base,$acl) | Out-Null
    }
    $writes = [Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    $paths = @($base)
    if ($null -ne $stock) { $paths += $config }
    foreach ($path in $paths) {
        $info = Get-Item -LiteralPath $path
        $security = Get-Acl -LiteralPath $path
        if (($path -eq $base -and !$info.PSIsContainer) -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $security.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $admins.Value,$system.Value) { 'CONFIG_CHECK'; return }
        foreach ($rule in $security.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writes) -and
                $rule.IdentityReference.Value -notin $admins.Value,$system.Value) { 'CONFIG_CHECK'; return }
        }
    }
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
        try { $listener.Start(); $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
        if ($port -notin $reserved) { break }
    }
    if ($port -in $reserved) { 'PORT_BUSY'; return }
    $lines = @("Port $port", 'ListenAddress 127.0.0.1', 'PermitEmptyPasswords no',
        'Subsystem sftp sftp-server.exe', 'Match Group administrators',
        '    AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys')
    $body = ($lines -join "`n") + "`n"
    if ($null -ne $stock) { $body = "Port $port`nListenAddress 127.0.0.1`n" + $stock }
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    if (!(Write-CheckedConfig $config $bytes $defaults)) { 'CONFIG_CHECK'; return }
} elseif ($service.Status -ne 'Running') {
    # Starting a previously configured server must not claim a WSL/direct SSH port.
    $effective = @(& $binary -T -f $config 2>$null)
    if ($LASTEXITCODE -ne 0) { 'CONFIG_CHECK'; return }
    $ports = @($effective | ForEach-Object { if ($_ -match '^port (\d+)$') { [int]$matches[1] }
            elseif ($_ -match '^listenaddress .*:(\d+)$') { [int]$matches[1] } } | Sort-Object -Unique)
    if (!$ports) { 'CONFIG_CHECK'; return }
    if (@($ports | Where-Object { $_ -in $reserved -or $_ -in $listening }).Count -gt 0) {
        'PORT_BUSY'; return
    }
}
$changed = $false
$stage = 'startup'
try {
    if ((Get-Service sshd).StartType -eq 'Disabled') {
        if (!$EnableDisabled) { 'DISABLED'; return }
        Set-Service -Name sshd -StartupType Manual
        $changed = $true
    }
    Start-Service -Name sshd
    (Get-Service sshd).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
} catch {
    if ($changed -and (Get-Service sshd).Status -eq 'Stopped') { Set-Service -Name sshd -StartupType Disabled }
    if ($null -ne $stock -and (Get-Service sshd).Status -eq 'Stopped') { $null = Write-CheckedConfig $config $defaults $bytes }
    throw
}
'READY'
