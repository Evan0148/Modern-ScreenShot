#requires -version 5
# UI verification renderer for Modern-ScreenShot.
#
# For each theme it writes settings.json (backing up the previous Theme), launches the app with the
# hidden diagnostic switch --render-settings|--render-editor|--render-history, and the app renders
# the window to a PNG via RenderTargetBitmap into tools\verify_out\ui\. Screen-lock safe.
# The original Theme value is restored at the end.
param(
    [string[]]$Themes = @('Light','Dark'),
    [string[]]$Targets = @('settings','editor','history')
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot "..\src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe"
$settingsPath = Join-Path $env:APPDATA 'Modern-ScreenShot\settings.json'
$outDir = Join-Path $PSScriptRoot "verify_out\ui"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$originalTheme = $null
if (Test-Path $settingsPath) {
    $obj = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($obj.PSObject.Properties.Name -contains 'Theme') { $originalTheme = $obj.Theme }
}

function Write-Theme($theme) {
    $obj = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $theme }
    else { $obj | Add-Member -NotePropertyName Theme -NotePropertyValue $theme -Force }
    ($obj | ConvertTo-Json -Depth 20) | Set-Content -Path $settingsPath -Encoding UTF8
}

$fail = 0
try {
    foreach ($theme in $Themes) {
        Write-Theme $theme
        foreach ($target in $Targets) {
            Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Milliseconds 500
            $out = Join-Path $outDir ("{0}_{1}.png" -f $target, $theme.ToLowerInvariant())
            if (Test-Path $out) { Remove-Item $out }
            $env:MSS_RENDER_OUT = $out
            $p = Start-Process -FilePath $exe -ArgumentList "--render-$target" -PassThru -Wait
            if (-not (Test-Path $out)) {
                Write-Host "[FAIL] ${target}/${theme}: no render (exit=$($p.ExitCode))"
                $fail++
            } else {
                Write-Host "[OK]   ${target}/${theme} -> $out ($((Get-Item $out).Length) bytes)"
            }
        }
    }
} finally {
    Remove-Item Env:MSS_RENDER_OUT -ErrorAction SilentlyContinue
    if ($originalTheme -ne $null) { Write-Theme $originalTheme }
    Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
if ($fail -gt 0) { Write-Host "RESULT: FAIL ($fail)"; exit 1 }
Write-Host "RESULT: PASS"
exit 0
