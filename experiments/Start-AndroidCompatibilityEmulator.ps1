#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Name,
    [Parameter(Mandatory)][string]$SystemImage,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(5554, 5682)][int]$Port = 5580
)
$ErrorActionPreference = 'Stop'
if ($Port % 2) { throw 'Emulator console port must be even.' }
$sdkRoot = Join-Path $env:LOCALAPPDATA 'Android/Sdk'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$avdRoot = Join-Path $output 'avds'
$avdPath = Join-Path $avdRoot "$Name.avd"
New-Item -ItemType Directory -Path $avdRoot -Force | Out-Null
if (Get-NetTCPConnection -LocalPort $Port, ($Port + 1) -State Listen -ErrorAction SilentlyContinue) {
    throw 'The requested emulator port is already in use; existing devices were not touched.'
}
$previousAvdRoot = $env:ANDROID_AVD_HOME
try {
    $env:ANDROID_AVD_HOME = $avdRoot
    if (-not (Test-Path -LiteralPath $avdPath)) {
        'no' | & (Join-Path $env:JAVA_HOME 'bin/java.exe') "-Dcom.android.sdkmanager.toolsdir=$sdkRoot/cmdline-tools/latest" `
            -classpath (Join-Path $sdkRoot 'cmdline-tools/latest/lib/avdmanager-classpath.jar') `
            com.android.sdklib.tool.AvdManagerCli create avd -n $Name -k $SystemImage -p $avdPath -d pixel_5
        if ($LASTEXITCODE) { throw 'Could not create the isolated compatibility AVD.' }
    }
    $process = Start-Process -FilePath (Join-Path $sdkRoot 'emulator/emulator.exe') -WindowStyle Hidden -PassThru `
        -ArgumentList @('-avd', $Name, '-port', $Port, '-no-window', '-no-audio', '-no-snapshot', '-no-boot-anim',
            '-gpu', 'swiftshader_indirect', '-memory', '2048', '-cores', '2') `
        -RedirectStandardOutput (Join-Path $output "$Name.stdout.log") -RedirectStandardError (Join-Path $output "$Name.stderr.log")
    [pscustomobject]@{ ProcessId = $process.Id; Serial = "emulator-$Port"; Name = $Name;
        SystemImage = $SystemImage; AvdPath = $avdPath } | ConvertTo-Json
} finally { $env:ANDROID_AVD_HOME = $previousAvdRoot }
