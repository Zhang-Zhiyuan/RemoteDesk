# Runs locally for an enrolled host. No endpoint, command or account comes from the relay.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$service = Get-Service -Name sshd -ErrorAction SilentlyContinue
if ($service.Status -eq 'Running') { 'READY'; return }
if ($service.StartType -eq 'Disabled') { 'Windows 已禁用 sshd 服务，请在本机检查服务策略。'; return }
$base = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ssh'
$config = Join-Path $base 'sshd_config'
$binary = Join-Path ([Environment]::SystemDirectory) 'OpenSSH\sshd.exe'
if ($service -and (Get-CimInstance Win32_Service -Filter "Name='sshd'").PathName.Trim('"') -ne $binary) {
    '请在本机启动已有的自定义 sshd 服务。'; return
}
# Windows may come with the optional feature preinstalled but never started.
$fresh = !(Test-Path -LiteralPath $config)
if (!$service) {
    $rule = 'OpenSSH-Server-In-TCP'
    $hadRule = Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue
    try {
        $install = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
    } catch {
        'INSTALL_FAILED 0x{0:X8}' -f $_.Exception.HResult; return
    } finally {
        # Windows adds an inbound rule when installing the optional feature.
        if (!$hadRule) {
            Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Disable-NetFirewallRule | Out-Null
        }
    }
    if ($install.RestartNeeded) { 'RESTART_NEEDED'; return }
    $service = Get-Service -Name sshd
}
if ($fresh -and (Test-Path -LiteralPath $config)) {
    'Windows 安装程序已创建 sshd_config。请在本机确认监听地址后启动 sshd。'; return
}
if ($fresh) {
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
    $info = Get-Item -LiteralPath $base
    $security = Get-Acl -LiteralPath $base
    if (!$info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $security.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $admins.Value,$system.Value) {
        throw 'OpenSSH 配置目录所有者或类型异常。'
    }
    $writes = [Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    foreach ($rule in $security.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writes) -and
            $rule.IdentityReference.Value -notin $admins.Value,$system.Value) { throw 'OpenSSH 配置目录可以被其他账户写入。' }
    }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    try { $listener.Start(); $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $lines = @("Port $port", 'ListenAddress 127.0.0.1', 'PermitEmptyPasswords no',
        'Subsystem sftp sftp-server.exe', 'Match Group administrators',
        '    AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys')
    $file = [IO.File]::Open($config,'CreateNew','Write','None')
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes(($lines -join "`n") + "`n")
        $file.Write($bytes,0,$bytes.Length); $file.Flush($true)
    } finally { $file.Dispose() }
} elseif ($service.Status -ne 'Running') {
    # Starting a previously configured server must not claim a WSL/direct SSH port.
    if (!(Test-Path -LiteralPath $binary)) { '请在本机启动已配置的 OpenSSH 服务。'; return }
    $effective = @(& $binary -T 2>$null)
    if ($LASTEXITCODE -ne 0) { '请在本机检查现有 sshd_config 和主机密钥。'; return }
    $ports = @($effective | ForEach-Object { if ($_ -match '^port (\d+)$') { [int]$matches[1] }
            elseif ($_ -match '^listenaddress .*:(\d+)$') { [int]$matches[1] } } | Sort-Object -Unique)
    if (!$ports -or (Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue)) {
        '现有 SSH 监听端口已被占用或无法确定，请在本机检查。'; return
    }
}
Start-Service -Name sshd
'READY'
