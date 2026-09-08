param([switch]$Medium)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$output = Join-Path $workspace 'artifacts\persistent-startup-20260908\medium-launch'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class RemoteDeskStartupTokenProbe {
 [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct SI {
  public int cb; public string reserved, desktop, title; public int x,y,cx,cy,charsX,charsY,fill,flags;
  public short show, reservedBytes; public IntPtr reservedPtr, stdin, stdout, stderr;
 }
 [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr process, thread; public int pid, tid; }
 [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
 [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr p,uint access,out IntPtr token);
 [DllImport("advapi32.dll", SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr token,uint access,IntPtr attrs,int level,int type,out IntPtr copy);
 [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool CreateProcessWithTokenW(IntPtr token,uint flags,string exe,StringBuilder args,uint creation,IntPtr env,string cwd,ref SI si,out PI pi);
 [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,out int value,int size,out int returned);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static bool Elevated(int pid) {
  using(var process=Process.GetProcessById(pid)) {
   if(!OpenProcessToken(process.Handle,8,out var token)) throw new Win32Exception();
   try { if(!GetTokenInformation(token,20,out var value,4,out _)) throw new Win32Exception(); return value!=0; }
   finally { CloseHandle(token); }
  }
 }
 public static int Launch(string exe,string arguments,string cwd) {
  GetWindowThreadProcessId(GetShellWindow(),out var pid);
  if(pid==0 || Elevated(pid)) return 0; // No real normal-permission shell; do not change UAC policy.
  using(var shell=Process.GetProcessById(pid)) {
   if(!OpenProcessToken(shell.Handle,0xB,out var token)) throw new Win32Exception();
   try {
    if(!DuplicateTokenEx(token,0xF01FF,IntPtr.Zero,2,1,out var primary)) throw new Win32Exception();
    try {
     var si=new SI { cb=Marshal.SizeOf<SI>(), desktop="winsta0\\default", flags=1, show=0 };
     if(!CreateProcessWithTokenW(primary,0,exe,new StringBuilder("\""+exe+"\" "+arguments),0x08000000,IntPtr.Zero,cwd,ref si,out var child)) throw new Win32Exception();
     CloseHandle(child.process); CloseHandle(child.thread); return child.pid;
    } finally { CloseHandle(primary); }
   } finally { CloseHandle(token); }
  }
 }
}
'@
if (-not $Medium) {
    $shellExe = (Get-Process -Id $PID).Path
    $arguments = '-NoProfile -File "' + $PSCommandPath + '" -Medium'
    $childId = [RemoteDeskStartupTokenProbe]::Launch($shellExe, $arguments, $workspace)
    if ($childId -eq 0) {
        $skipped = @{Skipped=$true; Passed=$false; Reason='No normal-permission shell is available. UAC/system policy was not modified.'}
        $skipped | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
        $skipped | ConvertTo-Json
        exit
    }
    $child = Get-Process -Id $childId
    if (-not $child.WaitForExit(20000)) {
        Stop-Process -Id $childId -Force
        throw 'Normal-permission startup probe did not complete in 20 seconds; the owned test process was stopped.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $output 'result.json'))) {
        throw ('Reduced-permission PowerShell exited before the probe ran: ' + $child.ExitCode)
    }
    Get-Content -LiteralPath (Join-Path $output 'result.json')
    exit
}
$result = [ordered]@{ CallerElevated=[RemoteDeskStartupTokenProbe]::Elevated($PID) }
try {
    if ($result.CallerElevated) { throw 'The test must run without elevation.' }
    @{output=$output} | ConvertTo-Json -Compress | & dotnet (Join-Path $workspace 'experiments\InteropProbe\bin\Release\net8.0-windows\RemoteDesk.InteropProbe.dll') startup-status
    if ($LASTEXITCODE -ne 0) { throw 'Startup status probe failed.' }
    $status = Get-Content (Join-Path $output 'status.json') -Raw | ConvertFrom-Json
    $result.Installed = $status.persistent.IsInstalled
    $result.Enabled = $status.persistent.IsEnabled
    $exe = $status.persistent.ExecutablePath
    if (-not $exe) { throw 'No managed installation found.' }
    $result.ProtectedFromWrite = $false
    try {
        $file = [IO.File]::Open($exe,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        $file.Dispose()
    } catch [UnauthorizedAccessException] { $result.ProtectedFromWrite = $true }
    if (-not $result.ProtectedFromWrite) { throw 'The normal user can write the elevated executable.' }
    $started = Start-Process -FilePath $exe -ArgumentList '--tray' -WindowStyle Hidden -PassThru
    if (-not $started.WaitForExit(10000)) { throw 'Portable launcher did not hand off to the elevated task.' }
    $result.LauncherExitCode = $started.ExitCode
    $deadline = [DateTime]::UtcNow.AddSeconds(6)
    do {
        $instances = @(Get-Process RemoteDesk -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
        if ($instances.Count -gt 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $result.ManagedProcesses = $instances.Count
    $result.ManagedElevated = $instances.Count -eq 1 -and [RemoteDeskStartupTokenProbe]::Elevated($instances[0].Id)
    $result.Passed = $result.Installed -and $result.Enabled -and $result.ManagedElevated -and $result.LauncherExitCode -eq 0
} catch {
    $result.Passed = $false
    $result.Error = $_.Exception.Message
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
if (-not $result.Passed) { exit 1 }
