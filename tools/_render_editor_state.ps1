# Scratch diagnostic: render the editor window in several states/sizes/themes.
# Guards against the single-instance relay race: verifies no resident app before launch, deletes
# the stale PNG first, and re-checks its write time after (retrying once on mismatch).
param(
    [Parameter(Mandatory)][string]$Name,
    [int]$W = 0,
    [int]$H = 0,
    [switch]$Effects,
    [string]$Select = '',
    [string]$Tool = '',
    [string]$Fx = '',
    [string]$Theme = '',
    [string]$Lang = ''
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path (Get-Location) "src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe"
$out = (Resolve-Path 'tools\verify_out').Path + "\$Name.png"

$settingsPath = Join-Path $env:APPDATA 'Modern-ScreenShot\settings.json'
$obj = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($Theme -ne '') {
    if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme }
    else { $obj | Add-Member -NotePropertyName Theme -NotePropertyValue $Theme -Force }
}
if ($Lang -ne '') {
    if ($obj.PSObject.Properties.Name -contains 'Language') { $obj.Language = $Lang }
    else { $obj | Add-Member -NotePropertyName Language -NotePropertyValue $Lang -Force }
}
if ($Theme -ne '' -or $Lang -ne '') {
    ($obj | ConvertTo-Json -Depth 20) | Set-Content -Path $settingsPath -Encoding UTF8
}

function Invoke-Render {
    Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue | Stop-Process -Force
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 100
        if (-not (Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue)) { break }
    }
    if (Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue) { throw 'resident app would not die' }
    if (Test-Path $out) { Remove-Item $out -Force }

    $env:MSS_RENDER_OUT = $out
    $env:MSS_RENDER_WAIT_MS = '3500'
    if ($W -gt 0) { $env:MSS_RENDER_W = "$W" } else { Remove-Item Env:MSS_RENDER_W -ErrorAction SilentlyContinue }
    if ($H -gt 0) { $env:MSS_RENDER_H = "$H" } else { Remove-Item Env:MSS_RENDER_H -ErrorAction SilentlyContinue }
    if ($Effects) { $env:MSS_RENDER_EDITOR_EFFECTS = '1' } else { Remove-Item Env:MSS_RENDER_EDITOR_EFFECTS -ErrorAction SilentlyContinue }
    if ($Select -ne '') { $env:MSS_RENDER_EDITOR_SELECT = $Select } else { Remove-Item Env:MSS_RENDER_EDITOR_SELECT -ErrorAction SilentlyContinue }
    if ($Tool -ne '') { $env:MSS_RENDER_EDITOR_TOOL = $Tool } else { Remove-Item Env:MSS_RENDER_EDITOR_TOOL -ErrorAction SilentlyContinue }
    if ($Fx -ne '') { $env:MSS_RENDER_EDITOR_FX = $Fx } else { Remove-Item Env:MSS_RENDER_EDITOR_FX -ErrorAction SilentlyContinue }

    $p = Start-Process -FilePath $exe -ArgumentList '--render-editor' -Wait -PassThru -WindowStyle Hidden
    if (-not (Test-Path $out)) { return $false }
    $written = (Get-Item $out).LastWriteTimeUtc
    return ($written -gt (Get-Date).ToUniversalTime().AddSeconds(-90))
}

$ok = Invoke-Render
if (-not $ok) { Start-Sleep -Milliseconds 800; $ok = Invoke-Render }
Write-Output ("exit=0 out=$ok")
