param(
    [Parameter(Mandatory = $true)][string]$Adb,
    [Parameter(Mandatory = $true)][string]$Serial,
    [ValidateSet('Dump', 'TapText', 'SetField')][string]$Action = 'Dump',
    [string]$Text,
    [string]$Value
)

$ErrorActionPreference = 'Stop'
$deviceDump = '/data/local/tmp/remotedesk-test-ui.xml'
& $Adb -s $Serial shell uiautomator dump $deviceDump | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect test-device UI' }
[xml]$uiTree = ((& $Adb -s $Serial exec-out cat $deviceDump) -join "`n")
$allowedPackages = @('com.remotedesk.agent', 'com.remotedesk.codecprobe', 'com.remotedesk.viewerprobe',
    'com.remotedesk.relayhostprobe',
    'com.android.settings', 'com.android.systemui', 'com.android.permissioncontroller',
    'com.google.android.permissioncontroller',
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
$target = $matches[0]
if ($Action -eq 'SetField') {
    if ($Serial -notmatch '^emulator-\d+$' -or $target.package -ne 'com.remotedesk.agent') {
        throw 'Synthetic field entry is restricted to the owned product in an emulator.'
    }
    if ($Value -notmatch '^[a-zA-Z0-9.:-]{1,128}$') { throw 'Only simple synthetic ASCII test values are accepted.' }
    $target = $target.SelectSingleNode("following-sibling::node[@class='android.widget.EditText'][1]")
    if ($null -eq $target -or $target.enabled -ne 'true') { throw 'The labeled editable field is not available.' }
    $hints = @{ '本机访问口令' = '本机被控口令'; '远端地址' = 'IP / 主机名（端口可省略）'; '连接口令' = '远端口令' }
    if ($target.text -and $target.text -ne $Value -and $target.text -ne $hints[$Text]) {
        throw 'Refusing to overwrite a non-empty test field.'
    }
    if ($target.text -eq $Value) { 'The synthetic field is already populated.'; exit 0 }
}
$bounds = @([regex]::Matches($target.bounds, '\d+') | ForEach-Object { [int]$_.Value })
if ($bounds.Count -ne 4 -or $bounds[2] -le $bounds[0] -or $bounds[3] -le $bounds[1]) {
    throw 'Control does not have visible bounds'
}
& $Adb -s $Serial shell input tap ([int](($bounds[0] + $bounds[2]) / 2)) ([int](($bounds[1] + $bounds[3]) / 2))
if ($LASTEXITCODE -ne 0) { throw 'Tap failed' }
if ($Action -eq 'SetField') {
    & $Adb -s $Serial shell input text $Value
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic field entry failed' }
    & $Adb -s $Serial shell input keyevent KEYCODE_BACK
    "Populated synthetic field: $Text"
    exit 0
}
"Tapped: $Text"
