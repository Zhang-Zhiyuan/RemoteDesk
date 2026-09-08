param(
    [Parameter(Mandatory = $true)][string]$Adb,
    [Parameter(Mandatory = $true)][string]$Serial,
    [ValidateSet('Dump', 'TapText')][string]$Action = 'Dump',
    [string]$Text
)

$ErrorActionPreference = 'Stop'
$deviceDump = '/data/local/tmp/remotedesk-test-ui.xml'
& $Adb -s $Serial shell uiautomator dump $deviceDump | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect test-device UI' }
[xml]$uiTree = ((& $Adb -s $Serial exec-out cat $deviceDump) -join "`n")
$allowedPackages = @('com.remotedesk.agent', 'com.remotedesk.codecprobe', 'com.remotedesk.viewerprobe',
    'com.remotedesk.relayhostprobe',
    'com.android.settings', 'com.android.systemui', 'com.android.permissioncontroller',
    'com.oplus.appdetail', 'com.oplus.safecenter', 'com.android.packageinstaller', 'com.google.android.packageinstaller')
$nodes = @($uiTree.SelectNodes('//node') | Where-Object { $_.package -in $allowedPackages })
if ($Action -eq 'Dump') {
    # Password text can be exposed by an unmasked EditText, so never print any
    # editable contents from the product app, even during a failed masking test.
    $nodes | Where-Object { $_.text -and $_.class -ne 'android.widget.EditText' } |
        ForEach-Object { @{ text = $_.text; bounds = $_.bounds; package = $_.package } } |
        ConvertTo-Json -Compress
    exit 0
}
if ([string]::IsNullOrWhiteSpace($Text)) { throw 'TapText requires a visible exact label' }
$matches = @($nodes | Where-Object { $_.text -ceq $Text -and $_.enabled -eq 'true' })
if ($matches.Count -ne 1) { throw "Expected one enabled '$Text' control, got $($matches.Count)" }
$bounds = @([regex]::Matches($matches[0].bounds, '\d+') | ForEach-Object { [int]$_.Value })
if ($bounds.Count -ne 4 -or $bounds[2] -le $bounds[0] -or $bounds[3] -le $bounds[1]) {
    throw 'Control does not have visible bounds'
}
& $Adb -s $Serial shell input tap ([int](($bounds[0] + $bounds[2]) / 2)) ([int](($bounds[1] + $bounds[3]) / 2))
if ($LASTEXITCODE -ne 0) { throw 'Tap failed' }
"Tapped: $Text"
