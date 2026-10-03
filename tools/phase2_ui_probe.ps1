#requires -version 5
# Phase-2 UI probe: pixel-scan the rendered windows for the known failure signatures
# (black-window, unreadable contrast, missing content) and dump the settings visual
# tree for every tab. Programmatic substitute while the visual-judge provider is
# unavailable on this machine (same approach as the earlier review rounds).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe"
$outDir = Join-Path $root "tools\verify_out\ui"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$script:fail = 0
function Check($cond, $name, $detail) {
    if ($cond) { Write-Host "[PASS] $name $detail" }
    else { Write-Host "[FAIL] $name $detail"; $script:fail++ }
}

function Get-Lum([byte]$r, [byte]$g, [byte]$b) { (0.2126 * $r + 0.7152 * $g + 0.0722 * $b) }

function Analyze-Render($path, $theme) {
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    try {
        $w = $bmp.Width; $h = $bmp.Height
        # window background: 5px inset sample points along the frame + corners of the content area
        $black = 0; $sampled = 0
        $pts = @(
            @(5, 5), @(($w - 6), 5), @(5, ($h - 6)), @(($w - 6), ($h - 6)),
            @([int]($w / 2), 5), @(5, [int]($h / 2))
        )
        foreach ($pt in $pts) {
            $c = $bmp.GetPixel($pt[0], $pt[1]); $sampled++
            if ((Get-Lum $c.R $c.G $c.B) -lt 6) { $black++ }
        }
        $isBlackWindow = ($black -ge 4)

        # content scan (avoid the 2px border): count dark-text and light-text pixels
        $darkText = 0; $lightText = 0; $total = 0
        for ($y = 20; $y -lt $h - 20; $y += 3) {
            for ($x = 20; $x -lt $w - 20; $x += 3) {
                $c = $bmp.GetPixel($x, $y); $total++
                $l = Get-Lum $c.R $c.G $c.B
                if ($l -lt 80) { $darkText++ }
                elseif ($l -gt 200) { $lightText++ }
            }
        }
        $darkPct = 100.0 * $darkText / $total
        $lightPct = 100.0 * $lightText / $total
        if ($theme -eq 'light') {
            Check (-not $isBlackWindow) "$([IO.Path]::GetFileName($path)) not black-window" ""
            Check ($lightPct -gt 0.3) "content present" "(light bg ${lightPct:F1}% bright px)"
            Check ($darkPct -gt 0.3) "text strokes present" "(${darkPct:F1}% dark px = text/icons)"
        } else {
            Check (-not $isBlackWindow) "$([IO.Path]::GetFileName($path)) not black-window" ""
            Check ($darkPct -gt 0.3) "content present" "(dark bg ${darkPct:F1}% dark px)"
            Check ($lightPct -gt 0.3) "text strokes present" "(${lightPct:F1}% bright px = text/icons)"
        }
        return @{ w = $w; h = $h }
    } finally { $bmp.Dispose() }
}

Write-Host "=== pixel scans ==="
foreach ($theme in @('light','dark')) {
    foreach ($target in @('settings','editor','history')) {
        $p = Join-Path $outDir "${target}_${theme}.png"
        if (Test-Path $p) { Analyze-Render $p $theme | Out-Null }
        else { Check $false "missing render ${target}/${theme}" "" }
    }
}

# editor canvas should show the checkerboard (two distinct non-background grays) in both themes
Write-Host "=== editor canvas ==="
foreach ($theme in @('light','dark')) {
    $bmp = [System.Drawing.Bitmap]::FromFile((Join-Path $outDir "editor_${theme}.png"))
    try {
        $cx = [int]($bmp.Width * 0.55); $cy = [int]($bmp.Height * 0.5)
        $lums = New-Object System.Collections.Generic.List[double]
        for ($y = $cy - 60; $y -lt $cy + 60; $y += 4) {
            for ($x = $cx - 80; $x -lt $cx + 80; $x += 4) {
                $c = $bmp.GetPixel($x, $y); $lums.Add((Get-Lum $c.R $c.G $c.B))
            }
        }
        $mean = ($lums | Measure-Object -Average).Average
        $var = 0.0; foreach ($l in $lums) { $var += [math]::Pow($l - $mean, 2) }
        $std = [math]::Sqrt($var / $lums.Count)
        Check ($std -gt 4.0) "editor_${theme} canvas has content variation" "(stddev $([math]::Round($std,1)))"
    } finally { $bmp.Dispose() }
}

# settings: dump the visual tree of every tab (dark theme is the working default) and
# verify the nav + content card exist with non-degenerate bounds
Write-Host "=== settings tree dumps (7 tabs) ==="
$settingsPath = Join-Path $env:APPDATA 'Modern-ScreenShot\settings.json'
$obj = Get-Content $settingsPath -Raw | ConvertFrom-Json
$origTheme = $obj.Theme
$obj.Theme = 'Dark'
($obj | ConvertTo-Json -Depth 20) | Set-Content -Path $settingsPath -Encoding UTF8

try {
    for ($tab = 0; $tab -lt 7; $tab++) {
        Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 400
        $out = Join-Path $outDir "settings_tab${tab}.png"
        $env:MSS_RENDER_OUT = $out
        $env:MSS_DUMP_TREE = '1'
        $env:MSS_RENDER_TAB = "$tab"
        $null = Start-Process -FilePath $exe -ArgumentList '--render-settings' -PassThru -Wait
        Remove-Item Env:MSS_DUMP_TREE, Env:MSS_RENDER_TAB -ErrorAction SilentlyContinue
        $treeFile = "$out.tree.txt"
        if (-not (Test-Path $treeFile)) { Check $false "tab${tab} tree dumped" ""; continue }
        $tree = Get-Content $treeFile -Raw
        $okNav = $tree -match 'Nav\w+ \[([1-9]\d*)'
        # content card: at least one sizable child region below the nav column
        $okContent = ($tree -match 'Page\w+|\[\d{2,},\d{2,} \d{3,}x\d{2,}\]')
        Check ($okNav -and $okContent) "tab${tab} nav+content in tree" ""
        # title bar present (AppTitleBar 880x32-ish row, Win11-native caption height)
        Check ($tree -match 'AppTitleBar Name= \[\d+,\d+ \d+x32\]') "tab${tab} title bar in place" ""
    }
} finally {
    $obj.Theme = $origTheme
    ($obj | ConvertTo-Json -Depth 20) | Set-Content -Path $settingsPath -Encoding UTF8
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "PHASE2 PROBE: ALL PASS"; exit 0 }
Write-Host "PHASE2 PROBE: $script:fail FAILURE(S)"
exit 1
