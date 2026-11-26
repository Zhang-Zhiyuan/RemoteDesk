$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '../scripts/Prepare-RemoteDeskWindowsSsh.ps1'
$errors = $null
$tree = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors)
if ($errors) { throw ($errors -join [Environment]::NewLine) }
$function = $tree.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-OpenSshInstallResult'
}, $false)
. ([scriptblock]::Create($function.Extent.Text))

# Exercise the production status check without running the installer.
$testFile = $PSCommandPath
function Get-Service { param($Name,$ErrorAction) if ($registered) { 'sshd' } }
function Start-Sleep { param($Milliseconds) }
function Get-WindowsCapability {
    param([switch]$Online,$Name,$ErrorAction)
    if (!$Online -or $Name -ne 'OpenSSH.Server~~~~0.0.1.0') { throw 'Unexpected capability query' }
    [pscustomobject]@{State=$state}
}
function Join-Path { param($Path,$ChildPath) if ($serverFile) { $testFile } else { "$testFile.missing" } }

foreach ($case in @(
    @('Installed',       $true,  $true,  $false, 'REGISTERED'),
    @('Installed',       $false, $true,  $false, 'NO_SERVICE'),
    @('Installed',       $false, $false, $false, 'MISSING_BINARY'),
    @('InstallPending',  $false, $false, $false, 'RESTART_NEEDED'),
    @('UninstallPending',$false, $false, $false, 'RESTART_NEEDED'),
    @('NotPresent',      $false, $false, $true,  'RESTART_NEEDED'),
    @('NotPresent',      $false, $false, $false, 'NEEDS_SOURCE'),
    @('Staged',          $false, $false, $false, 'NEEDS_SOURCE'),
    @('Unknown',         $false, $false, $false, 'STATE_UNKNOWN')
)) {
    $state,$registered,$serverFile,$restart,$expected = $case
    $actual = Get-OpenSshInstallResult ([pscustomobject]@{RestartNeeded=$restart})
    if ($actual -ne $expected) { throw "OpenSSH state $state returned $actual, expected $expected." }
}
