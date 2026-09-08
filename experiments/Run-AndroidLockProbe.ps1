param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$TargetAddress,
    [Security.SecureString]$ConnectionPassword,
    [int]$Port = 56565,
    [int]$Seconds = 25,
    [int]$WakeAtSeconds = -1,
    [string]$EditorText = ''
)
$ErrorActionPreference = 'Stop'
$rdRoot = Split-Path $PSScriptRoot -Parent
$rdSettings = Get-Content -LiteralPath (Join-Path $env:APPDATA 'RemoteDesk/settings.json') -Raw | ConvertFrom-Json
$rdViewer = $rdSettings.Viewer
if ($null -eq $ConnectionPassword -and $rdViewer.Host -ne $TargetAddress) {
    throw 'Saved viewer target differs from the owned test device; supply its password explicitly.'
}
$rdSecret = $null
$rdConfig = $null
$rdPasswordBuffer = [IntPtr]::Zero
try {
    if ($null -ne $ConnectionPassword) {
        $rdPasswordBuffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ConnectionPassword)
        $rdPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($rdPasswordBuffer)
    } else {
        $rdSecret = [Security.Cryptography.ProtectedData]::Unprotect(
            [Convert]::FromBase64String($rdViewer.ProtectedPassword),
            [Text.Encoding]::UTF8.GetBytes('RemoteDesk.HostPassword.v1'),
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $rdPlain = [Text.Encoding]::UTF8.GetString($rdSecret)
        $Port = $rdViewer.Port
    }
    $rdConfig = @{
        host = $TargetAddress; port = $Port
        password = $rdPlain
        seconds = $Seconds; output = [IO.Path]::GetFullPath($OutputDirectory)
    }
    if ($EditorText) { $rdConfig.text = $EditorText }
    if ($WakeAtSeconds -ge 0) { $rdConfig.wakeAtSeconds = $WakeAtSeconds }
    $rdConfig | ConvertTo-Json -Compress -EscapeHandling EscapeNonAscii | & dotnet (Join-Path $rdRoot 'experiments/InteropProbe/bin/Release/net8.0-windows/RemoteDesk.InteropProbe.dll') android-lock
    if ($LASTEXITCODE -ne 0) { throw 'Android lock continuity probe failed; inspect its private report.' }
} finally {
    if ($null -ne $rdSecret) { [Array]::Clear($rdSecret, 0, $rdSecret.Length) }
    if ($rdPasswordBuffer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($rdPasswordBuffer) }
    $rdPlain = $null
    if ($null -ne $rdConfig) { $rdConfig.password = $null }
}
