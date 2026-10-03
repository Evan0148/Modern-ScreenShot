#requires -version 5
<#
.SYNOPSIS
    Title-bar probe for ModernScreenShot: geometry / state pixels / WM_NCHITTEST / residue.

.DESCRIPTION
    Drives the app's diagnostic switch --titlebar-probe (registered in App.Features.cs before the
    single-instance handshake, like --render-*) once per window/state and asserts the Windows 11
    native title-bar contract. Four sub-scenarios, each independently reported; any failed assertion
    makes the script exit non-zero.

      geometry : MSS_DUMP_TREE + physical geometry for settings / editor / history / ocr / oobe /
                 hotkey. Asserts bar height == 32 DIP, content top offset == 32 DIP, no content
                 overlapping the bar band, and the close button's right edge within 1 physical px
                 of the VISIBLE window right edge (layered dialogs: the card's inner edge, since
                 their 20 DIP shadow margin is a deliberate design choice).
      state    : hover / pressed / close-hover / inactive pixel colours and text contrast. Fills are
                 compared against the app's own resolved theme tokens composited over the rest
                 background; text uses the extreme-tail mean (mean of the extreme 0.5% by luminance),
                 never percentiles.
      hit      : SendMessage(WM_NCHITTEST) on the real HWND. Blank caption -> HTCAPTION(2),
                 maximize-button centre -> HTMAXBUTTON(9), NoResize windows -> non-HTMAXBUTTON,
                 inactive window -> non-HTMAXBUTTON.
      residue  : hover -> deactivate / open modal / WM_NCMOUSELEAVE must return the button
                 background to transparent (equal to the rest sample).

    All coordinates are DPI-normalised by (bitmapWidth / windowDIPwidth); nothing is hard-coded to
    96 DPI. Residual ModernScreenShot.App instances are killed before every probe run (single
    instance forwarding would otherwise swallow the arguments). Probe-process launches are retried
    once (PowerShell AMSI is intermittently flaky on this machine).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\titlebar_probe.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\titlebar_probe.ps1 -Scenario state
#>
param(
    [string]$ExePath,
    [ValidateSet('all', 'geometry', 'state', 'hit', 'residue')]
    [string]$Scenario = 'all',
    [string]$EvidenceDir,
    [int]$TimeoutMs = 60000
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $root 'src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe'
}
$outDir = Join-Path $PSScriptRoot 'verify_out\titlebar'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (-not $EvidenceDir) { $EvidenceDir = Join-Path $root '.omo\evidence' }
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

$script:fail = 0
function Check($cond, $name, $detail) {
    if ($cond) { Write-Host "[PASS] $name $detail" }
    else { Write-Host "[FAIL] $name $detail"; $script:fail++ }
}

function Get-HitName([int]$code) {
    switch ($code) {
        0 { 'HTNOWHERE' } 1 { 'HTCLIENT' } 2 { 'HTCAPTION' } 3 { 'HTSYSMENU' }
        8 { 'HTMINBUTTON' } 9 { 'HTMAXBUTTON' } 10 { 'HTLEFT' } 11 { 'HTRIGHT' }
        12 { 'HTTOP' } 13 { 'HTTOPLEFT' } 14 { 'HTTOPRIGHT' } 15 { 'HTBOTTOM' }
        16 { 'HTBOTTOMLEFT' } 17 { 'HTBOTTOMRIGHT' } 18 { 'HTBORDER' } 20 { 'HTCLOSE' }
        -1 { 'HTTRANSPARENT' } -2 { 'HTERROR' }
        default { "HT_?($code)" }
    }
}

# ---------------------------------------------------------------------------
# Cursor save/restore only - the probe process itself performs the real
# WM_NCHITTEST calls (same process, same thread, real HWND).
# ---------------------------------------------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TitleBarProbeNative {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@

$savedCursor = New-Object TitleBarProbeNative+POINT
[void][TitleBarProbeNative]::GetCursorPos([ref]$savedCursor)

function Kill-ResidualApp {
    Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 450
}

function Clear-ProbeEnv {
    Remove-Item Env:MSS_TITLEBAR_PROBE_OUT, Env:MSS_TITLEBAR_PROBE_STATE, Env:MSS_TITLEBAR_PROBE_WAIT_MS, `
        Env:MSS_RENDER_OUT, Env:MSS_DUMP_TREE -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Probe invocation + output parsing
# ---------------------------------------------------------------------------
function Read-ProbeOutput([string]$path) {
    $kv = @{}; $tokens = @{}; $hits = @(); $buttons = @{}
    foreach ($line in (Get-Content -LiteralPath $path)) {
        if ($line -notmatch '^TBPROBE ') { continue }
        $body = $line.Substring(8)
        if ($body -match '^token=([^|]+)\|(#[0-9A-Fa-f]{8})$') { $tokens[$Matches[1]] = $Matches[2]; continue }
        if ($body -match '^hittest=([^|]+)\|([^|]+)\|(-?\d+)$') {
            $hits += [pscustomobject]@{ Name = $Matches[1]; XY = $Matches[2]; Code = [int]$Matches[3] }
            continue
        }
        if ($body -match '^button=([^|]+)\|([^|]+)\|([^|]+)$') {
            $buttons[$Matches[1]] = [pscustomobject]@{ Dip = $Matches[2]; Phys = $Matches[3] }
            continue
        }
        if ($body -match '^([A-Za-z][A-Za-z0-9]*)=(.*)$') { $kv[$Matches[1]] = $Matches[2] }
    }
    return [pscustomobject]@{ Kv = $kv; Tokens = $tokens; Hits = $hits; Buttons = $buttons }
}

function Invoke-ProbeRun {
    param([string]$Target = 'settings', [string]$State = 'rest', [switch]$Png, [switch]$Tree, [int]$WaitMs = 1200)

    $tag = "$Target-$State" + $(if ($Png) { '-png' } else { '' }) + $(if ($Tree) { '-tree' } else { '' })
    $txt = Join-Path $outDir "probe-$tag.txt"
    $pngPath = Join-Path $outDir "$tag.png"
    $treePath = "$pngPath.tree.txt"

    $exited = $false
    $exitCode = -999
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Kill-ResidualApp
        # Park the cursor in the primary monitor's top-left corner before every launch: rest runs
        # must not start already hovered, and WPF's CenterScreen then always centres the probe
        # window on the same monitor (deterministic runs).
        [void][TitleBarProbeNative]::SetCursorPos(2, 2)
        Start-Sleep -Milliseconds 120
        Remove-Item -LiteralPath $txt -Force -ErrorAction SilentlyContinue
        if ($Png) { Remove-Item -LiteralPath $pngPath -Force -ErrorAction SilentlyContinue }
        if ($Tree) { Remove-Item -LiteralPath $treePath -Force -ErrorAction SilentlyContinue }

        $env:MSS_TITLEBAR_PROBE_OUT = $txt
        $env:MSS_TITLEBAR_PROBE_STATE = $State
        $env:MSS_TITLEBAR_PROBE_WAIT_MS = "$WaitMs"
        if ($Png) { $env:MSS_RENDER_OUT = $pngPath } else { Remove-Item Env:MSS_RENDER_OUT -ErrorAction SilentlyContinue }
        if ($Tree) { $env:MSS_DUMP_TREE = '1' } else { Remove-Item Env:MSS_DUMP_TREE -ErrorAction SilentlyContinue }

        $proc = Start-Process -FilePath $ExePath -ArgumentList "--titlebar-probe=$Target" -PassThru
        $exited = $proc.WaitForExit($TimeoutMs)
        if (-not $exited) { try { $proc.Kill() } catch { } }
        Clear-ProbeEnv
        if ($exited) { $exitCode = $proc.ExitCode }
        if ($exited -and (Test-Path -LiteralPath $txt)) { break }
        Write-Host "  (probe $tag attempt $attempt did not produce output; retrying)"
        Start-Sleep -Milliseconds 600
    }

    $parsed = $null
    if (Test-Path -LiteralPath $txt) { $parsed = Read-ProbeOutput $txt }
    return [pscustomobject]@{
        Target = $Target; State = $State; Exit = $exitCode; TimedOut = (-not $exited)
        Txt = $txt
        Png = $(if ($Png) { $pngPath } else { $null })
        Tree = $(if ($Tree) { $treePath } else { $null })
        Kv = $(if ($parsed) { $parsed.Kv } else { @{} })
        Tokens = $(if ($parsed) { $parsed.Tokens } else { @{} })
        Hits = $(if ($parsed) { $parsed.Hits } else { @() })
        Buttons = $(if ($parsed) { $parsed.Buttons } else { @{} })
    }
}

$script:probeCache = @{}
function Get-ProbeRun {
    param([string]$Target = 'settings', [string]$State = 'rest', [switch]$Png, [switch]$Tree, [int]$WaitMs = 1200)
    $key = "$Target|$State|$($Png.IsPresent)|$($Tree.IsPresent)"
    if ($script:probeCache.ContainsKey($key)) { return $script:probeCache[$key] }
    $res = Invoke-ProbeRun -Target $Target -State $State -Png:$Png -Tree:$Tree -WaitMs $WaitMs
    $script:probeCache[$key] = $res
    return $res
}

function Test-ProbeRun($run, $name) {
    if ($run.Exit -eq 0 -and -not $run.TimedOut -and $run.Kv.Count -gt 0) { return $true }
    Check $false "$name probe ran" "exit=$($run.Exit) timedOut=$($run.TimedOut) output=$($run.Txt)"
    return $false
}

# ---------------------------------------------------------------------------
# Dump-tree parsing (flat lines with 2-space indentation = hierarchy)
# ---------------------------------------------------------------------------
function Parse-Tree([string]$path) {
    $items = New-Object System.Collections.Generic.List[object]
    $i = 0
    foreach ($line in (Get-Content -LiteralPath $path)) {
        if ($line -match '^(\s*)(\S+) Name=([^\[]*)\[(-?\d+),(-?\d+) (\d+)x(\d+)\]') {
            $items.Add([pscustomobject]@{
                Index = $i; Indent = $Matches[1].Length; Type = $Matches[2]; Name = $Matches[3].Trim()
                X = [double]$Matches[4]; Y = [double]$Matches[5]; W = [double]$Matches[6]; H = [double]$Matches[7]
            })
        } elseif ($line -match '^(\s*)(\S+)') {
            $items.Add([pscustomobject]@{ Index = $i; Indent = $Matches[1].Length; Type = $Matches[2]; Name = ''; X = $null; Y = $null; W = $null; H = $null })
        }
        $i++
    }
    return $items
}

function Find-BarOverlaps($items, $bar) {
    $overlaps = New-Object System.Collections.Generic.List[string]
    $barBottom = $bar.Y + $bar.H
    foreach ($it in $items) {
        if ($null -eq $it.X) { continue }
        if ($it.Index -eq $bar.Index) { continue }
        if ($it.Indent -lt $bar.Indent) { continue }                                   # ancestor of the bar
        if ($it.Index -gt $bar.Index -and $it.Indent -gt $bar.Indent) { continue }     # inside the bar
        if ($it.H -le 0) { continue }
        if ($it.Y -ge $barBottom) { continue }
        if (($it.Y + $it.H) -le $bar.Y) { continue }
        if (($it.X -lt ($bar.X + $bar.W)) -and (($it.X + $it.W) -gt $bar.X)) {
            $overlaps.Add("$($it.Type) [$($it.X),$($it.Y) $($it.W)x$($it.H)]")
        }
    }
    return ,$overlaps
}

# ---------------------------------------------------------------------------
# Pixel helpers
# ---------------------------------------------------------------------------
function Get-Pixel($bmp, [double]$px, [double]$py) {
    $x = [Math]::Max(0, [Math]::Min($bmp.Width - 1, [int][Math]::Round($px)))
    $y = [Math]::Max(0, [Math]::Min($bmp.Height - 1, [int][Math]::Round($py)))
    return $bmp.GetPixel($x, $y)
}

function HexOf($c) { '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B }

function Diff-Channels($c1, $c2) {
    return [Math]::Max([Math]::Abs($c1.R - $c2.R), [Math]::Max([Math]::Abs($c1.G - $c2.G), [Math]::Abs($c1.B - $c2.B)))
}

# Composites #AARRGGBB over an opaque background colour.
function Compose-Argb([string]$fgHex, $bgColor) {
    $a = [Convert]::ToInt32($fgHex.Substring(1, 2), 16) / 255.0
    $r = [Convert]::ToInt32($fgHex.Substring(3, 2), 16)
    $g = [Convert]::ToInt32($fgHex.Substring(5, 2), 16)
    $b = [Convert]::ToInt32($fgHex.Substring(7, 2), 16)
    return [pscustomobject]@{
        R = [int][Math]::Round($r * $a + $bgColor.R * (1 - $a))
        G = [int][Math]::Round($g * $a + $bgColor.G * (1 - $a))
        B = [int][Math]::Round($b * $a + $bgColor.B * (1 - $a))
    }
}

function Linearize([double]$v) {
    $s = $v / 255.0
    if ($s -le 0.04045) { return $s / 12.92 }
    return [Math]::Pow(($s + 0.055) / 1.055, 2.4)
}

function RelLum($c) { 0.2126 * (Linearize $c.R) + 0.7152 * (Linearize $c.G) + 0.0722 * (Linearize $c.B) }

function ContrastRatio($c1, $c2) {
    $l1 = RelLum $c1; $l2 = RelLum $c2
    $hi = [Math]::Max($l1, $l2); $lo = [Math]::Min($l1, $l2)
    return ($hi + 0.05) / ($lo + 0.05)
}

# Extreme-tail statistics of a region: median (background) and the mean of the extreme 0.5% by
# luminance in both directions (sparse glyph strokes: percentile stats smear them away).
function Get-RegionStats($bmp, [double]$dipX, [double]$dipY, [double]$dipW, [double]$dipH, [double]$scale) {
    $x0 = [Math]::Max(0, [int][Math]::Round($dipX * $scale))
    $y0 = [Math]::Max(0, [int][Math]::Round($dipY * $scale))
    $x1 = [Math]::Min($bmp.Width, [int][Math]::Round(($dipX + $dipW) * $scale))
    $y1 = [Math]::Min($bmp.Height, [int][Math]::Round(($dipY + $dipH) * $scale))
    $samples = New-Object System.Collections.Generic.List[object]
    for ($y = $y0; $y -lt $y1; $y++) {
        for ($x = $x0; $x -lt $x1; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $samples.Add([pscustomobject]@{ R = $c.R; G = $c.G; B = $c.B; L = (0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B) })
        }
    }
    if ($samples.Count -eq 0) { return $null }
    $sorted = $samples | Sort-Object L
    $n = $sorted.Count
    $median = $sorted[[int]($n * 0.5)]
    $tail = [Math]::Max(1, [int]($n * 0.005))
    $dr = 0.0; $dg = 0.0; $db = 0.0
    for ($i = 0; $i -lt $tail; $i++) { $dr += $sorted[$i].R; $dg += $sorted[$i].G; $db += $sorted[$i].B }
    $dark = [pscustomobject]@{ R = [int][Math]::Round($dr / $tail); G = [int][Math]::Round($dg / $tail); B = [int][Math]::Round($db / $tail) }
    $br = 0.0; $bg = 0.0; $bb = 0.0
    for ($i = $n - 1; $i -ge $n - $tail; $i--) { $br += $sorted[$i].R; $bg += $sorted[$i].G; $bb += $sorted[$i].B }
    $bright = [pscustomobject]@{ R = [int][Math]::Round($br / $tail); G = [int][Math]::Round($bg / $tail); B = [int][Math]::Round($bb / $tail) }
    return [pscustomobject]@{
        Median = [pscustomobject]@{ R = [int]$median.R; G = [int]$median.G; B = [int]$median.B }
        MedianLum = $median.L
        Dark = $dark
        Bright = $bright
        Count = $n
    }
}

function Get-PngScale($bmp, $run) {
    $dipW = [double](($run.Kv['windowDip'] -split ',')[0])
    if ($dipW -le 0) { return 1.0 }
    return $bmp.Width / $dipW
}

function Get-ButtonPoint($bmp, $run, [string]$role) {
    $btn = $run.Buttons[$role]
    if (-not $btn) { return $null }
    $d = $btn.Dip -split ','
    $scale = Get-PngScale $bmp $run
    $x = ([double]$d[0] + [double]$d[2] * 0.2) * $scale
    $y = ([double]$d[1] + [double]$d[3] * 0.5) * $scale
    return @{ X = $x; Y = $y; DipX = [double]$d[0]; DipY = [double]$d[1]; DipW = [double]$d[2]; DipH = [double]$d[3]; Scale = $scale }
}

function Write-Evidence([string]$name, $lines) {
    $path = Join-Path $EvidenceDir $name
    $lines | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "  evidence: $path"
}

# ---------------------------------------------------------------------------
# Scenario: GEOMETRY
# ---------------------------------------------------------------------------
function Run-GeometryScenario {
    Write-Host ''
    Write-Host '=== GEOMETRY (bar 32 DIP, content offset 32 DIP, button right edge == visible edge) ==='
    $ev = New-Object System.Collections.Generic.List[string]
    $ev.Add('titlebar probe - GEOMETRY')
    $ev.Add("captured: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $ev.Add("exe: $ExePath")
    $ev.Add('')

    foreach ($target in @('settings', 'editor', 'history', 'ocr', 'oobe', 'hotkey')) {
        $run = Get-ProbeRun -Target $target -State rest -Png -Tree
        $ev.Add("---- $target (exit=$($run.Exit) timedOut=$($run.TimedOut)) ----")
        if (-not (Test-ProbeRun $run $target)) { $ev.Add('  probe failed'); continue }
        foreach ($k in ($run.Kv.Keys | Sort-Object)) { $ev.Add("  $k=$($run.Kv[$k])") }

        $barH = [double]$run.Kv['barHeight']
        Check ([Math]::Abs($barH - 32) -le 0.51) "$target bar height == 32 DIP" "measured $barH"
        $cto = [double]$run.Kv['contentTopOffset']
        Check ([Math]::Abs($cto - 32) -le 0.51) "$target content top offset == 32 DIP" "measured $cto"

        # MSS_DUMP_TREE cross-check + no content may overlap the bar band.
        if ($run.Tree -and (Test-Path -LiteralPath $run.Tree)) {
            $tree = Parse-Tree $run.Tree
            $bar = $tree | Where-Object { $_.Type -eq 'AppTitleBar' } | Select-Object -First 1
            if ($bar) {
                Check ([Math]::Abs($bar.H - 32) -le 0.51) "$target tree bar height == 32" "tree $($bar.W)x$($bar.H) at [$($bar.X),$($bar.Y)]"
                $overlaps = Find-BarOverlaps $tree $bar
                $detail = $(if ($overlaps.Count -gt 0) { "first overlap: $($overlaps[0])" } else { "$($tree.Count) tree nodes checked" })
                Check ($overlaps.Count -eq 0) "$target no content overlaps the bar band" $detail
                if ($overlaps.Count -gt 0) { $ev.Add("  OVERLAPS: $($overlaps -join ' | ')") }
            } else {
                Check $false "$target AppTitleBar present in tree dump" ""
            }
        } else {
            Check $false "$target tree dump exists" "$($run.Tree)"
        }

        # Button right edge vs the visible physical edge (layered dialogs: card inner edge).
        if ($run.Buttons.ContainsKey('close')) {
            $layered = ($run.Kv['layered'] -eq '1')
            if ($layered) {
                $edge = [double]$run.Kv['cardInnerRight']
                $edgeName = 'layered card inner edge'
            } else {
                $edge = [double](($run.Kv['visibleFrame'] -split ',')[2])
                $edgeName = 'visible physical window right edge'
            }
            $closeRight = [double](($run.Buttons['close'].Phys -split ',')[2])
            $gap = $edge - $closeRight
            Check ([Math]::Abs($gap) -le 1.0) "$target close button right edge within 1px of $edgeName" "gap=$([Math]::Round($gap,2))px (button=$closeRight edge=$edge)"
            $ev.Add("  right-edge gap: $([Math]::Round($gap,2))px vs $edgeName")
        } else {
            Check $false "$target close button present" ""
        }
    }
    Write-Evidence 'titlebar-geometry-probe.txt' $ev
}

# ---------------------------------------------------------------------------
# Scenario: STATE PIXELS
# ---------------------------------------------------------------------------
function Run-StateScenario {
    Write-Host ''
    Write-Host '=== STATE PIXELS (hover / pressed / close-hover / inactive) ==='
    $ev = New-Object System.Collections.Generic.List[string]
    $ev.Add('titlebar probe - STATE PIXELS')
    $ev.Add("captured: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $ev.Add('')

    $rest = Get-ProbeRun -Target settings -State rest -Png
    $hover = Get-ProbeRun -Target settings -State hover -Png
    $pressed = Get-ProbeRun -Target settings -State pressed -Png
    $closeHover = Get-ProbeRun -Target settings -State close-hover -Png
    $inactive = Get-ProbeRun -Target settings -State inactive -Png

    $runs = @{ rest = $rest; hover = $hover; pressed = $pressed; closeHover = $closeHover; inactive = $inactive }
    $ok = $true
    foreach ($key in $runs.Keys) {
        if (-not (Test-ProbeRun $runs[$key] "$key")) { $ok = $false }
        elseif (-not $runs[$key].Png -or -not (Test-Path -LiteralPath $runs[$key].Png)) {
            Check $false "$key snapshot PNG written" "$($runs[$key].Png)"; $ok = $false
        }
    }
    if (-not $ok) { Write-Evidence 'titlebar-state-pixels.txt' $ev; return }

    $bRest = $bmp = $null
    $bmps = @{}
    try {
        foreach ($key in @('rest', 'hover', 'pressed', 'closeHover', 'inactive')) {
            $bmps[$key] = [System.Drawing.Bitmap]::FromFile($runs[$key].Png)
        }
        $scale = Get-PngScale $bmps.rest $rest
        $maxPt = Get-ButtonPoint $bmps.rest $rest 'max'
        $closePt = Get-ButtonPoint $bmps.rest $rest 'close'
        $secHex = $rest.Tokens['secondary']
        $terHex = $rest.Tokens['tertiary']
        $txtTerHex = $rest.Tokens['textTertiary']
        if (-not $maxPt -or -not $closePt) { Check $false 'caption buttons reported by probe' ''; Write-Evidence 'titlebar-state-pixels.txt' $ev; return }
        if (-not $secHex -or -not $terHex -or -not $txtTerHex) { Check $false 'theme tokens reported by probe' ''; Write-Evidence 'titlebar-state-pixels.txt' $ev; return }

        $ev.Add("tokens: secondary=$secHex tertiary=$terHex textTertiary=$txtTerHex  pngScale=$scale")

        # --- hover: expected = secondary token composited over the rest background ---
        $restMax = Get-Pixel $bmps.rest $maxPt.X $maxPt.Y
        $hoverMax = Get-Pixel $bmps.hover $maxPt.X $maxPt.Y
        $expectHover = Compose-Argb $secHex $restMax
        $d = Diff-Channels $hoverMax $expectHover
        Check ($d -le 6) 'hover fill == secondary token over caption bg' "sample=$(HexOf $hoverMax) expected=$(HexOf $expectHover) diff=$d"
        Check ((Diff-Channels $hoverMax $restMax) -ge 2) 'hover state visibly differs from rest' "rest=$(HexOf $restMax) hover=$(HexOf $hoverMax)"
        $ev.Add("hover: rest=$(HexOf $restMax) hover=$(HexOf $hoverMax) expected=$(HexOf $expectHover) diff=$d")

        # --- pressed: tertiary over rest, and distinct from hover ---
        $pressedMax = Get-Pixel $bmps.pressed $maxPt.X $maxPt.Y
        $expectPressed = Compose-Argb $terHex $restMax
        $d = Diff-Channels $pressedMax $expectPressed
        Check ($d -le 6) 'pressed fill == tertiary token over caption bg' "sample=$(HexOf $pressedMax) expected=$(HexOf $expectPressed) diff=$d"
        $dHover = Diff-Channels $pressedMax $hoverMax
        Check ($dHover -ge 2) 'pressed state differs from hover' "pressed=$(HexOf $pressedMax) hover=$(HexOf $hoverMax) diff=$dHover"
        $ev.Add("pressed: sample=$(HexOf $pressedMax) expected=$(HexOf $expectPressed) diff=$d hoverDiff=$dHover")

        # --- close-hover: fixed Win11 red ---
        $closeHoverPx = Get-Pixel $bmps.closeHover $closePt.X $closePt.Y
        $expectRed = [pscustomobject]@{ R = 0xC4; G = 0x2B; B = 0x1C }
        $d = Diff-Channels $closeHoverPx $expectRed
        Check ($d -le 6) 'close-hover fill == #C42B1C' "sample=$(HexOf $closeHoverPx) diff=$d"
        $ev.Add("close-hover: sample=$(HexOf $closeHoverPx) expected=#C42B1C diff=$d")

        # --- inactive: title ink == textTertiary token; button tint cleared ---
        $inactiveMax = Get-Pixel $bmps.inactive $maxPt.X $maxPt.Y
        $dResidue = Diff-Channels $inactiveMax $restMax
        Check ($dResidue -le 6) 'inactive clears the button hover tint' "rest=$(HexOf $restMax) inactive=$(HexOf $inactiveMax) diff=$dResidue"
        $ev.Add("inactive button: rest=$(HexOf $restMax) inactive=$(HexOf $inactiveMax) diff=$dResidue")

        if ($rest.Kv.ContainsKey('title')) {
            $td = $rest.Kv['title'] -split ','
            $titleW = [Math]::Min([double]$td[2], 220)
            $statsRest = Get-RegionStats $bmps.rest ([double]$td[0]) ([double]$td[1]) $titleW ([double]$td[3]) $scale
            $statsInactive = Get-RegionStats $bmps.inactive ([double]$td[0]) ([double]$td[1]) $titleW ([double]$td[3]) $scale
            if ($statsRest -and $statsInactive) {
                # Ink is the tail away from the background (light ink on dark themes, dark on light).
                $inkRest = $(if ($statsRest.MedianLum -lt 128) { $statsRest.Bright } else { $statsRest.Dark })
                $inkInactive = $(if ($statsInactive.MedianLum -lt 128) { $statsInactive.Bright } else { $statsInactive.Dark })
                $bgForCompose = $statsRest.Median
                $expectInactive = Compose-Argb $txtTerHex $bgForCompose
                $dInactive = Diff-Channels $inkInactive $expectInactive
                Check ($dInactive -le 48) 'inactive title ink == textTertiary token over caption bg' "ink=$(HexOf $inkInactive) expected=$(HexOf $expectInactive) diff=$dInactive"
                Check ((Diff-Channels $inkInactive $inkRest) -ge 8) 'inactive title ink differs from active' "active=$(HexOf $inkRest) inactive=$(HexOf $inkInactive)"
                $c = ContrastRatio $inkInactive $bgForCompose
                Check ($c -ge 2.0) 'inactive title contrast >= 2.0:1' "contrast=$([Math]::Round($c,2))"
                $ev.Add("title ink: active=$(HexOf $inkRest) inactive=$(HexOf $inkInactive) expected=$(HexOf $expectInactive) diff=$dInactive contrast=$([Math]::Round($c,2))")
            } else { Check $false 'title region sampled' '' }
        } else { Check $false 'title region reported by probe' '' }

        # --- glyph contrast on the fill states (extreme-tail ink vs median fill) ---
        $contrastCases = @(
            @{ Name = 'hover glyph'; Bmp = $bmps.hover; X = $maxPt.DipX; Y = $maxPt.DipY; W = $maxPt.DipW; H = $maxPt.DipH; Floor = 3.0 },
            @{ Name = 'pressed glyph'; Bmp = $bmps.pressed; X = $maxPt.DipX; Y = $maxPt.DipY; W = $maxPt.DipW; H = $maxPt.DipH; Floor = 3.0 },
            @{ Name = 'close-hover glyph'; Bmp = $bmps.closeHover; X = $closePt.DipX; Y = $closePt.DipY; W = $closePt.DipW; H = $closePt.DipH; Floor = 3.0 }
        )
        foreach ($cc in $contrastCases) {
            $stats = Get-RegionStats $cc.Bmp $cc.X $cc.Y $cc.W $cc.H $scale
            $ink = $(if ($stats.MedianLum -lt 128) { $stats.Bright } else { $stats.Dark })
            $c = ContrastRatio $ink $stats.Median
            Check ($c -ge $cc.Floor) "$($cc.Name) contrast >= $($cc.Floor):1" "contrast=$([Math]::Round($c,2)) ink=$(HexOf $ink) bg=$(HexOf $stats.Median)"
            $ev.Add("$($cc.Name): ink=$(HexOf $ink) bg=$(HexOf $stats.Median) contrast=$([Math]::Round($c,2))")
        }
    } finally {
        foreach ($key in @($bmps.Keys)) { if ($bmps[$key]) { $bmps[$key].Dispose() } }
    }
    Write-Evidence 'titlebar-state-pixels.txt' $ev
}

# ---------------------------------------------------------------------------
# Scenario: HIT TEST
# ---------------------------------------------------------------------------
function Run-HitScenario {
    Write-Host ''
    Write-Host '=== HIT TEST (WM_NCHITTEST on the real HWND) ==='
    $ev = New-Object System.Collections.Generic.List[string]
    $ev.Add('titlebar probe - HIT TEST (WM_NCHITTEST, in-process SendMessage to the real HWND)')
    $ev.Add("captured: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $ev.Add('')
    $shotLines = New-Object System.Collections.Generic.List[string]

    # Resizable windows: blank caption -> HTCAPTION(2); maximize-button centre -> HTMAXBUTTON(9).
    foreach ($target in @('settings', 'editor', 'history', 'ocr')) {
        $run = Get-ProbeRun -Target $target -State rest -Png -Tree
        if (-not (Test-ProbeRun $run $target)) { continue }
        $blank = $run.Hits | Where-Object { $_.Name -eq 'blank' } | Select-Object -First 1
        $max = $run.Hits | Where-Object { $_.Name -eq 'max' } | Select-Object -First 1
        Check ($blank -and $blank.Code -eq 2) "$target blank caption -> HTCAPTION(2)" "code=$(if ($blank) { $blank.Code } else { 'missing' }) ($(if ($blank) { Get-HitName $blank.Code }))"
        Check ($max -and $max.Code -eq 9) "$target max button centre -> HTMAXBUTTON(9)" "code=$(if ($max) { $max.Code } else { 'missing' }) ($(if ($max) { Get-HitName $max.Code }))"
        if ($blank) { $shotLines.Add("$target blank @$($blank.XY) = $($blank.Code) $(Get-HitName $blank.Code)") }
        if ($max) { $shotLines.Add("$target max   @$($max.XY) = $($max.Code) $(Get-HitName $max.Code)") }
    }

    # NoResize windows: the maximize-button slot must never report HTMAXBUTTON.
    foreach ($target in @('oobe', 'hotkey')) {
        $run = Get-ProbeRun -Target $target -State rest -Png -Tree
        if (-not (Test-ProbeRun $run $target)) { continue }
        $max = $run.Hits | Where-Object { $_.Name -eq 'max' } | Select-Object -First 1
        Check ($max -and $max.Code -ne 9) "$target (NoResize) max slot != HTMAXBUTTON" "code=$(if ($max) { $max.Code } else { 'missing' }) ($(if ($max) { Get-HitName $max.Code }))"
        if ($max) { $shotLines.Add("$target noresize @$($max.XY) = $($max.Code) $(Get-HitName $max.Code)") }
    }

    # Gating: an inactive window must not answer HTMAXBUTTON for its maximize button.
    $inactive = Get-ProbeRun -Target settings -State inactive -Png
    if (Test-ProbeRun $inactive 'settings-inactive') {
        Check ($inactive.Kv['activeAfterTrigger'] -eq '0') 'inactive probe: window really deactivated' "activeAfterTrigger=$($inactive.Kv['activeAfterTrigger'])"
        $hit = $inactive.Hits | Where-Object { $_.Name -eq 'maxInactive' } | Select-Object -First 1
        Check ($hit -and $hit.Code -ne 9) 'inactive window max button != HTMAXBUTTON' "code=$(if ($hit) { $hit.Code } else { 'missing' }) ($(if ($hit) { Get-HitName $hit.Code }))"
        if ($hit) { $shotLines.Add("settings inactive max @$($hit.XY) = $($hit.Code) $(Get-HitName $hit.Code)") }
    }

    $ev.AddRange($shotLines)
    Write-Evidence 'titlebar-hitprobe.txt' $ev

    # Combined raw hit output, kept under the plan's evidence name.
    $combined = New-Object System.Collections.Generic.List[string]
    $combined.Add('titlebar probe - HIT TEST + RESIDUE (raw)')
    $combined.Add("captured: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $combined.Add('')
    $combined.Add('-- hit test --')
    $combined.AddRange($shotLines)
    return ,$combined
}

# ---------------------------------------------------------------------------
# Scenario: RESIDUE
# ---------------------------------------------------------------------------
function Run-ResidueScenario($combinedLines) {
    Write-Host ''
    Write-Host '=== RESIDUE (hover -> deactivate / modal / WM_NCMOUSELEAVE) ==='
    $ev = New-Object System.Collections.Generic.List[string]
    $ev.Add('titlebar probe - RESIDUE')
    $ev.Add("captured: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $ev.Add('')
    if ($combinedLines) { $combinedLines.Add(''); $combinedLines.Add('-- residue --') }

    $rest = Get-ProbeRun -Target settings -State rest -Png
    $hover = Get-ProbeRun -Target settings -State hover -Png
    if (-not (Test-ProbeRun $rest 'settings-rest')) { Write-Evidence 'titlebar-residue.txt' $ev; return }
    if (-not (Test-ProbeRun $hover 'settings-hover')) { Write-Evidence 'titlebar-residue.txt' $ev; return }

    $bmps = @{}
    try {
        $bmps.rest = [System.Drawing.Bitmap]::FromFile($rest.Png)
        $bmps.hover = [System.Drawing.Bitmap]::FromFile($hover.Png)
        $maxPt = Get-ButtonPoint $bmps.rest $rest 'max'
        if (-not $maxPt) { Check $false 'max button reported by probe' ''; Write-Evidence 'titlebar-residue.txt' $ev; return }
        $restPx = Get-Pixel $bmps.rest $maxPt.X $maxPt.Y
        $hoverPx = Get-Pixel $bmps.hover $maxPt.X $maxPt.Y
        # Precondition: hover must actually register, otherwise "no residue" would be vacuous.
        Check ((Diff-Channels $hoverPx $restPx) -ge 2) 'residue precondition: hover tint registered' "rest=$(HexOf $restPx) hover=$(HexOf $hoverPx)"
        if ($combinedLines) { $combinedLines.Add("hover precondition: rest=$(HexOf $restPx) hover=$(HexOf $hoverPx)") }

        foreach ($state in @('residue-deactivate', 'residue-modal', 'residue-ncleave')) {
            $run = Get-ProbeRun -Target settings -State $state -Png
            if (-not (Test-ProbeRun $run $state)) { continue }
            if ($state -eq 'residue-deactivate') {
                Check ($run.Kv['activeAfterTrigger'] -eq '0') 'residue-deactivate: window really deactivated' "activeAfterTrigger=$($run.Kv['activeAfterTrigger'])"
            }
            if (-not $run.Png -or -not (Test-Path -LiteralPath $run.Png)) { Check $false "$state snapshot PNG written" ""; continue }
            $bmp = [System.Drawing.Bitmap]::FromFile($run.Png)
            try {
                $px = Get-Pixel $bmp $maxPt.X $maxPt.Y
                $d = Diff-Channels $px $restPx
                Check ($d -le 6) "no hover residue after $state" "rest=$(HexOf $restPx) sample=$(HexOf $px) diff=$d"
                $ev.Add("${state}: rest=$(HexOf $restPx) sample=$(HexOf $px) diff=$d")
                if ($combinedLines) { $combinedLines.Add("${state}: rest=$(HexOf $restPx) sample=$(HexOf $px) diff=$d") }
            } finally { $bmp.Dispose() }
        }
    } finally {
        foreach ($key in @($bmps.Keys)) { if ($bmps[$key]) { $bmps[$key].Dispose() } }
    }
    Write-Evidence 'titlebar-residue.txt' $ev
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
Write-Host "TITLEBAR PROBE (scenario: $Scenario)"
Write-Host "exe: $ExePath"
Check (Test-Path -LiteralPath $ExePath) 'app exe exists' $ExePath
if (-not (Test-Path -LiteralPath $ExePath)) { Write-Host 'TITLEBAR PROBE: cannot run without the exe'; exit 1 }

$combinedHitResidue = $null
try {
    if ($Scenario -eq 'all' -or $Scenario -eq 'geometry') { Run-GeometryScenario }
    if ($Scenario -eq 'all' -or $Scenario -eq 'state') { Run-StateScenario }

    if ($Scenario -eq 'all' -or $Scenario -eq 'hit') {
        $combinedHitResidue = Run-HitScenario
    }
    if ($Scenario -eq 'all' -or $Scenario -eq 'residue') {
        Run-ResidueScenario $combinedHitResidue
    }
    if ($combinedHitResidue) {
        $combinedPath = Join-Path $EvidenceDir 'titlebar-hitresidue.txt'
        $combinedHitResidue | Set-Content -LiteralPath $combinedPath -Encoding UTF8
        Write-Host "  evidence: $combinedPath"
    }
} finally {
    Kill-ResidualApp
    Clear-ProbeEnv
    [void][TitleBarProbeNative]::SetCursorPos($savedCursor.X, $savedCursor.Y)
}

Write-Host ''
Write-Host '=== SUMMARY ==='
if ($script:fail -eq 0) {
    Write-Host 'TITLEBAR PROBE: ALL PASS'
    exit 0
}
Write-Host "TITLEBAR PROBE: $($script:fail) FAILURE(S)"
exit 1
