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

function New-PrivateSshDirectory([string]$path) {
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner($admins)
    foreach ($owner in $admins,$system) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    [IO.Directory]::CreateDirectory($path,$acl) | Out-Null
}

function Install-OpenSshFromMedia {
    $capability = 'OpenSSH.Server~~~~0.0.1.0'
    $name = 'OpenSSH-Server-Package~31bf3856ad364e35~amd64~~.cab'
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { return }
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        if ($drive.DriveType -ne 'CDRom' -or !$drive.IsReady) { continue }
        foreach ($source in @((Join-Path $drive.RootDirectory.FullName 'LanguagesAndOptionalFeatures'),$drive.RootDirectory.FullName)) {
            if ([IO.File]::Exists((Join-Path $source $name))) {
                return Add-WindowsCapability -Online -Name $capability -Source $source -LimitAccess
            }
        }
    }
    # Honor configured servicing sources and restrictions on public downloads.
    $wu = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
    $policy = Get-ItemProperty -LiteralPath $wu -ErrorAction SilentlyContinue
    $au = Get-ItemProperty -LiteralPath "$wu\AU" -ErrorAction SilentlyContinue
    $servicing = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing' -ErrorAction SilentlyContinue
    if ($policy.DoNotConnectToWindowsUpdateInternetLocations -or $policy.DisableWindowsUpdateAccess -or
        $policy.SetPolicyDrivenUpdateSourceForOtherUpdates -or $au.UseWUServer -or
        $servicing.LocalSourcePath -or $servicing.RepairContentServerSource -or $servicing.UseWindowsUpdate) { return }
    $os = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    if ($os.InstallationType -ne 'Client') { return }
    # Microsoft publishes these immutable x64 FoD images at
    # https://learn.microsoft.com/azure/virtual-desktop/windows-11-language-packs
    # Each range is the complete, contiguous OpenSSH Server CAB in that image.
    if ($os.CurrentBuildNumber -in '22621','22631') {
        $image = '988969d5-f34g-4e03-ac9d-1f9786c66749/22621.1.220506-1250.ni_release_amd64fre_CLIENT_LOF_PACKAGES_OEM.iso'
        $total = 7205206016L; $offset = 6535450624L; $length = 1518826
        $digest = 'fa6645d152be885a2027197b3295ad5c7b9244f3921a8d5d967fe5a2505dc28c'
    } elseif ($os.CurrentBuildNumber -in '26100','26200') {
        $image = '888969d5-f34g-4e03-ac9d-1f9786c66749/26100.1.240331-1435.ge_release_amd64fre_CLIENT_LOF_PACKAGES_OEM.iso'
        $total = 6251841536L; $offset = 5576660992L; $length = 2329588
        $digest = 'fc40c1573a6dacd58c57e2d85fb6fe9b75a7883b054cc541aa9929453cf7c5cc'
    } else { return }
    $last = $offset + $length - 1
    $request = [Net.HttpWebRequest]::Create("https://software-static.download.prss.microsoft.com/dbazure/$image")
    $request.AllowAutoRedirect = $false
    $request.Timeout = 20000; $request.ReadWriteTimeout = 20000
    $request.Headers['Accept-Encoding'] = 'identity'
    $request.AddRange([long]$offset,[long]$last)
    $response = $request.GetResponse()
    try {
        if ([int]$response.StatusCode -ne 206 -or $response.Headers['Content-Range'] -ne "bytes $offset-$last/$total" -or
            ($response.ContentLength -ge 0 -and $response.ContentLength -ne $length)) { throw 'Invalid FoD media range.' }
        $reader = [IO.BinaryReader]::new($response.GetResponseStream())
        try { $data = $reader.ReadBytes($length); if ($data.Length -ne $length -or $reader.BaseStream.ReadByte() -ne -1) { throw 'Invalid FoD length.' } }
        finally { $reader.Dispose() }
    } finally { $response.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($data)).Replace('-','') } finally { $sha.Dispose() }
    if ($actual -ne $digest) { throw 'Invalid FoD digest.' }
    $temp = Join-Path (Join-Path ([Environment]::GetFolderPath('Windows')) 'Temp') ('RemoteDesk-FoD-'+[Guid]::NewGuid().ToString('N'))
    New-PrivateSshDirectory $temp
    $cab = Join-Path $temp $name
    try {
        $file = [IO.File]::Open($cab,'CreateNew','Write','None')
        try { $file.Write($data,0,$length); $file.Flush($true) } finally { $file.Dispose() }
        # Windows servicing verifies package identity, applicability and catalog signature.
        return Add-WindowsCapability -Online -Name $capability -Source $temp -LimitAccess
    } finally {
        if ([IO.File]::Exists($cab)) { [IO.File]::Delete($cab) }
        [IO.Directory]::Delete($temp,$false)
    }
}

function Get-OpenSshInstallResult($install) {
    if ($install.RestartNeeded) { return 'RESTART_NEEDED' }
    # DISM can return successfully before SCM registers sshd, or without adding
    # the capability at all. Confirm what Windows actually installed.
    for ($i = 0; $i -lt 9; $i++) {
        if (Get-Service -Name sshd -ErrorAction SilentlyContinue) { return 'REGISTERED' }
        if ($i -lt 8) { Start-Sleep -Milliseconds 500 }
    }
    try { $state = (Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' -ErrorAction Stop).State }
    catch { return 'STATE_UNKNOWN' }
    if ($state -in 'InstallPending','UninstallPending') { return 'RESTART_NEEDED' }
    if ($state -in 'NotPresent','Staged','Removed','Resolved','PartiallyInstalled') { return 'NEEDS_SOURCE' }
    if ($state -ne 'Installed') { return 'STATE_UNKNOWN' }
    if (Get-Service -Name sshd -ErrorAction SilentlyContinue) { return 'REGISTERED' }
    if ([IO.File]::Exists((Join-Path ([Environment]::SystemDirectory) 'OpenSSH\sshd.exe'))) { return 'NO_SERVICE' }
    return 'MISSING_BINARY'
}

$service = Get-Service -Name sshd -ErrorAction SilentlyContinue
if ($service.Status -eq 'Running') { 'READY'; return }
$newService = $false
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
    # Retrying DISM while an earlier transaction awaits a reboot cannot start sshd.
    try { $current = Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' -ErrorAction Stop }
    catch { $current = $null }
    if ($current.State -in 'InstallPending','UninstallPending') { 'RESTART_NEEDED'; return }
    $rule = 'OpenSSH-Server-In-TCP'
    $hadRule = Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue
    try {
        $code = $null
        try {
            $install = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
            $result = Get-OpenSshInstallResult $install
        } catch {
            $code = '0x{0:X8}' -f $_.Exception.HResult
            if ($code -in '0x80070005','0x800704EC') { "INSTALL_FAILED $code"; return }
            $result = 'NEEDS_SOURCE'
        }
        if ($result -eq 'NEEDS_SOURCE') {
            try { $install = Install-OpenSshFromMedia }
            catch { $code = '0x{0:X8}' -f $_.Exception.HResult; $install = $null }
            if (!$install) { ("INSTALL_FAILED $code").TrimEnd(); return }
            $result = Get-OpenSshInstallResult $install
            if ($result -eq 'NEEDS_SOURCE') { 'INSTALL_FAILED'; return }
        }
        if ($result -ne 'REGISTERED') { $result; return }
    } finally {
        # Windows adds an inbound rule when installing the optional feature.
        if (!$hadRule) {
            $addedRule = Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue
            if ($addedRule) { $addedRule | Disable-NetFirewallRule | Out-Null }
        }
    }
    $service = Get-Service -Name sshd -ErrorAction SilentlyContinue
    if (!$service) { 'STATE_UNKNOWN'; return }
    if ($service.Status -eq 'Running') { 'READY'; return }
    $newService = $true
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
if ($configExists -and ($fresh -or ($wasDisabled -and $EnableDisabled))) {
    $template = Join-Path (Split-Path -Parent $binary) 'sshd_config_default'
    if ((Test-Path -LiteralPath $template -PathType Leaf) -and (Get-Item -LiteralPath $config).Length -le 65536) {
        $defaults = [IO.File]::ReadAllBytes($template)
        $text = [Text.Encoding]::UTF8.GetString($defaults)
        # A WSL listener can reserve 22 without appearing in Windows' listener
        # or portproxy tables. A newly enabled, untouched inbox configuration
        # therefore gets its own loopback port, just like a fresh installation.
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
        New-PrivateSshDirectory $base
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
        if (!$EnableDisabled -and !$newService) { 'DISABLED'; return }
        Set-Service -Name sshd -StartupType Manual
        $changed = $true
    }
    Start-Service -Name sshd
    (Get-Service sshd).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
} catch {
    $failure = $_.Exception
    $nativeError = 0
    while ($failure) {
        if ($failure -is [ComponentModel.Win32Exception]) { $nativeError = $failure.NativeErrorCode; break }
        $failure = $failure.InnerException
    }
    $serviceExit = (Get-CimInstance Win32_Service -Filter "Name='sshd'" -ErrorAction SilentlyContinue).ExitCode
    if ($changed -and (Get-Service sshd).Status -eq 'Stopped') { Set-Service -Name sshd -StartupType Disabled }
    if ($null -ne $stock -and (Get-Service sshd).Status -eq 'Stopped') { $null = Write-CheckedConfig $config $defaults $bytes }
    'START_FAILED {0} {1}' -f [int]$nativeError,[int]$serviceExit; return
}
'READY'
