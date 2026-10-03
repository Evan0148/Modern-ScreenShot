#requires -version 5
<#
.SYNOPSIS
    Empirically proves the WPF HwndSource hook invocation order and captures the
    WM_NCHITTEST baseline for the app's custom title bar (Windows 11 native work, task 2).

.DESCRIPTION
    Two independent empirical checks, neither of which modifies the repo solution:

    1. HOOK ORDER  - Generates a MINIMAL WPF diagnostic project OUTSIDE the repo (under
       $env:TEMP\mss_hook_order_probe), builds it, runs it. The diagnostic adds Hook #1
       (first) and Hook #2 (second) to a real HwndSource, then SendMessage()s numbered
       probe messages to its own window and logs which hook fires first. This proves or
       disproves the claim "PublicHooksFilterMessage iterates handlers last-to-first".
       It also records whether a first-running hook setting handled=true short-circuits
       the rest (load-bearing for the planned TitleBarSnapHook).

    2. WM_NCHITTEST BASELINE - Kills residual ModernScreenShot.App instances (single
       instance forwards arguments otherwise), launches the REAL app with
       --show-settings, finds the visible top-level window and SendMessage()s
       WM_NCHITTEST at three client-relative points under the CURRENT code
       (WindowChrome + TitleBarSnapHook on resizable windows):
         - blank caption point
         - maximize-button center
         - edge resize point
       Coordinates are computed from live GetWindowRect/GetClientRect/GetDpiForWindow,
       never hardcoded for 96 DPI.

    Raw output is written to tools\verify_out\hook_order\ and the baseline evidence file
    to .omo\evidence\titlebar-nchittest-baseline.txt.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\hook_order_probe.ps1
#>
param(
    [string]$ExePath,
    [switch]$SkipHookProbe,
    [switch]$SkipAppBaseline
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $root 'src\ModernScreenShot.App\bin\Release\net10.0-windows\win-x64\ModernScreenShot.App.exe'
}
$outDir = Join-Path $PSScriptRoot 'verify_out\hook_order'
$evidenceDir = Join-Path $root '.omo\evidence'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

$script:fail = 0
function Check($cond, $name, $detail) {
    if ($cond) { Write-Host "[PASS] $name $detail" }
    else { Write-Host "[FAIL] $name $detail"; $script:fail++ }
}

function Kill-ResidualApp {
    Get-Process -Name ModernScreenShot.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Get-HitName([int]$code) {
    switch ($code) {
        0  { 'HTNOWHERE' }
        1  { 'HTCLIENT' }
        2  { 'HTCAPTION' }
        3  { 'HTSYSMENU' }
        4  { 'HTGROWBOX/HTSIZE' }
        5  { 'HTMENU' }
        6  { 'HTHSCROLL' }
        7  { 'HTVSCROLL' }
        8  { 'HTMINBUTTON/HTREDUCE' }
        9  { 'HTMAXBUTTON/HTZOOM' }
        10 { 'HTLEFT' }
        11 { 'HTRIGHT' }
        12 { 'HTTOP' }
        13 { 'HTTOPLEFT' }
        14 { 'HTTOPRIGHT' }
        15 { 'HTBOTTOM' }
        16 { 'HTBOTTOMLEFT' }
        17 { 'HTBOTTOMRIGHT' }
        18 { 'HTBORDER' }
        20 { 'HTCLOSE' }
        21 { 'HTHELP' }
        -1 { 'HTTRANSPARENT' }
        -2 { 'HTERROR' }
        default { "HT_?($code)" }
    }
}

# =====================================================================================
# C# interop helpers (compiled in-memory; no repo files touched)
# =====================================================================================
$nativeSrc = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ProbeNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    public static IntPtr FindTopWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid != (uint)pid) return true;
            if (!IsWindowVisible(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static string ClassName(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string WindowTitle(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static IntPtr MakeLParam(int x, int y)
    {
        return (IntPtr)((y << 16) | (x & 0xFFFF));
    }
}
'@

Add-Type -TypeDefinition $nativeSrc -Language CSharp

$WmNcHitTest = 0x0084

function Send-HitTest([IntPtr]$hwnd, [int]$x, [int]$y) {
    $lp = [ProbeNative]::MakeLParam($x, $y)
    $r = [ProbeNative]::SendMessage($hwnd, $WmNcHitTest, [IntPtr]::Zero, $lp)
    return [int]$r.ToInt64()
}

# =====================================================================================
# PART 1 - HwndSource hook order
# =====================================================================================
$hookLog = Join-Path $outDir 'hook_order_probe.log'
$reverseHolds = $null
$handledShortCircuits = $null

if (-not $SkipHookProbe) {
    Write-Host '=== PART 1: HwndSource hook order ==='
    $projDir = Join-Path $env:TEMP 'mss_hook_order_probe'
    New-Item -ItemType Directory -Force -Path $projDir | Out-Null

    $csproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>HookOrderProbe</AssemblyName>
    <RootNamespace>HookOrderProbe</RootNamespace>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
'@

    $program = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HookOrderProbe;

/// <summary>
/// Minimal WPF diagnostic proving HwndSource hook invocation order.
/// Adds Hook #1 first, Hook #2 second, then SendMessage()s numbered probe messages to the
/// window and logs which hook fires first for each. Writes a raw log to args[0] and stdout.
/// </summary>
internal static class Program
{
    private const int WM_NULL = 0x0000;
    private const int WM_APP = 0x8000;
    private const int ProbeOrderMsg = WM_APP + 0x11;   // 0x8011 - neither hook sets handled
    private const int ProbeHandledMsg = WM_APP + 0x12; // 0x8012 - Hook #2 sets handled = true

    [STAThread]
    private static int Main(string[] args)
    {
        string logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "hook_order_probe.log");
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

        void L(string s)
        {
            log.WriteLine(s);
            Console.Out.WriteLine(s);
            Console.Out.Flush();
        }

        L("=========================================================");
        L("[probe] HwndSource hook-order diagnostic");
        L("[probe] started  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        L("[probe] log file : " + logPath);
        L("[probe] OS       : " + Environment.OSVersion.VersionString + " (build " + Environment.OSVersion.Version.Build + ")");
        L("[probe] CLR      : " + Environment.Version);
        L("[probe] arch     : " + RuntimeInformation.ProcessArchitecture);
        L("=========================================================");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = new Window
        {
            Title = "HookOrderProbe",
            Width = 360,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        win.Loaded += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            L("[probe] hwnd = 0x" + hwnd.ToInt64().ToString("X") + "  source = " + (source == null ? "null" : "ok"));
            if (source == null)
            {
                L("[probe] FATAL: HwndSource.FromHwnd returned null");
                app.Shutdown(2);
                return;
            }

            int seq = 0;

            // Added FIRST -> expected to run LAST if WPF iterates handlers last-to-first.
            source.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
            {
                if (msg == ProbeOrderMsg || msg == ProbeHandledMsg)
                {
                    seq++;
                    L("[HOOK #1] ran   msg=0x" + msg.ToString("X4") + "  global_call_seq=" + seq);
                }
                return IntPtr.Zero;
            });
            L("[probe] added Hook #1 (added FIRST)");

            // Added SECOND -> expected to run FIRST if WPF iterates handlers last-to-first.
            source.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (msg == ProbeOrderMsg || msg == ProbeHandledMsg)
                {
                    seq++;
                    L("[HOOK #2] ran   msg=0x" + msg.ToString("X4") + "  global_call_seq=" + seq);
                    if (msg == ProbeHandledMsg)
                    {
                        L("[HOOK #2] setting handled = true for 0x" + msg.ToString("X4"));
                        handled = true;
                    }
                }
                return IntPtr.Zero;
            });
            L("[probe] added Hook #2 (added SECOND)");

            L("");
            L("--- trigger: SendMessage(WM_NULL 0x0000) x1 ---");
            SendMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            L("");
            L("--- trigger: SendMessage(ProbeOrderMsg 0x8011) x3 (wParam=1..3) ---");
            SendMessage(hwnd, ProbeOrderMsg, new IntPtr(1), IntPtr.Zero);
            SendMessage(hwnd, ProbeOrderMsg, new IntPtr(2), IntPtr.Zero);
            SendMessage(hwnd, ProbeOrderMsg, new IntPtr(3), IntPtr.Zero);

            L("");
            L("--- trigger: SendMessage(ProbeHandledMsg 0x8012) x1 (Hook #2 sets handled=true) ---");
            SendMessage(hwnd, ProbeHandledMsg, IntPtr.Zero, IntPtr.Zero);

            L("");
            L("[probe] all probe messages sent; shutting down");
            log.Flush();
            win.Dispatcher.BeginInvoke(new Action(() => app.Shutdown(0)));
        };

        app.Run(win);
        L("[probe] Application.Run returned; log closed");
        return 0;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
}
'@

    Set-Content -Path (Join-Path $projDir 'HookOrderProbe.csproj') -Value $csproj -Encoding UTF8
    Set-Content -Path (Join-Path $projDir 'Program.cs') -Value $program -Encoding UTF8

    Write-Host "  generating + building minimal WPF diagnostic in $projDir"
    $buildLog = Join-Path $outDir 'hook_order_probe.build.log'
    $buildOut = & dotnet build $projDir -c Release -nologo -v q 2>&1
    $buildOut | Set-Content -Path $buildLog -Encoding UTF8
    Check ($LASTEXITCODE -eq 0) 'diagnostic builds' "(exit $LASTEXITCODE; see $buildLog)"
    if ($LASTEXITCODE -ne 0) { $buildOut | ForEach-Object { Write-Host "    $_" } }

    $probeExe = Join-Path $projDir 'bin\Release\net10.0-windows\HookOrderProbe.exe'
    Check (Test-Path $probeExe) 'diagnostic exe exists' $probeExe

    if (Test-Path $probeExe) {
        if (Test-Path $hookLog) { Remove-Item $hookLog -Force }
        & $probeExe $hookLog | Out-Null
        Start-Sleep -Milliseconds 200
        $logText = Get-Content $hookLog -Raw
        Write-Host ''
        Write-Host '---- raw hook-order log ----'
        Write-Host $logText
        Write-Host '----------------------------'

        $orderLines = @($logText -split "`r?`n" | Where-Object { $_ -match '^\[HOOK #\d\] ran\s+msg=0x8011' })
        $handledLines = @($logText -split "`r?`n" | Where-Object { $_ -match '^\[HOOK #\d\] ran\s+msg=0x8012' })

        if ($orderLines.Count -ge 2) {
            $firstIsSecond = ($orderLines[0] -match '^\[HOOK #2\]')
            $reverseHolds = $firstIsSecond
            Check $firstIsSecond 'later-added hook runs first (reverse order holds)' "first executor: $($orderLines[0])"
        } else {
            Check $false 'order probe produced >=2 hook invocations' "(got $($orderLines.Count))"
        }

        if ($handledLines.Count -ge 1) {
            $handledShortCircuits = ($handledLines.Count -eq 1 -and $handledLines[0] -match '^\[HOOK #2\]')
            Write-Host "  info: 0x8012 invocations = $($handledLines.Count) (Hook #2 sets handled=true; 1 means short-circuit)"
        }
    }
}

# =====================================================================================
# PART 2 - WM_NCHITTEST baseline for the real app window
# =====================================================================================
if (-not $SkipAppBaseline) {
    Write-Host ''
    Write-Host '=== PART 2: WM_NCHITTEST baseline (current production code) ==='
    Check (Test-Path $ExePath) 'app exe exists' $ExePath

    if (Test-Path $ExePath) {
        Kill-ResidualApp

        $proc = Start-Process -FilePath $ExePath -ArgumentList '--show-settings' -PassThru
        Write-Host "  launched $ExePath --show-settings (pid $($proc.Id))"

        $hwnd = [IntPtr]::Zero
        for ($i = 0; $i -lt 80; $i++) {
            Start-Sleep -Milliseconds 250
            $hwnd = [ProbeNative]::FindTopWindow($proc.Id)
            if ($hwnd -ne [IntPtr]::Zero) { break }
        }
        Check ($hwnd -ne [IntPtr]::Zero) 'app window appeared' "(hwnd $hwnd)"

        if ($hwnd -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 400
            $null = [ProbeNative]::SetForegroundWindow($hwnd)
            Start-Sleep -Milliseconds 200

            $wr = New-Object ProbeNative+RECT
            $cr = New-Object ProbeNative+RECT
            $null = [ProbeNative]::GetWindowRect($hwnd, [ref]$wr)
            $null = [ProbeNative]::GetClientRect($hwnd, [ref]$cr)

            $pt0 = New-Object ProbeNative+POINT
            $pt0.X = 0; $pt0.Y = 0
            $null = [ProbeNative]::ClientToScreen($hwnd, [ref]$pt0)
            $clientLeft = $pt0.X
            $clientTop = $pt0.Y
            $clientWidth = $cr.Right
            $clientHeight = $cr.Bottom
            $clientRight = $clientLeft + $clientWidth

            $dpi = [ProbeNative]::GetDpiForWindow($hwnd)
            if ($dpi -eq 0) { $dpi = 96 }
            $scale = $dpi / 96.0

            $fg = [ProbeNative]::GetForegroundWindow()
            $cls = [ProbeNative]::ClassName($hwnd)
            $title = [ProbeNative]::WindowTitle($hwnd)

            # Current production constants (read from source, not guessed):
            #   AppTitleBar.CaptionHeight = 32 DIP (TitleBarMetrics); caption buttons 46x32 DIP,
            #   right-aligned (min | max | close), vertically centered in the 32 DIP band.
            $captionHeightDip = 32
            $buttonWidthDip = 46
            $buttonCenterYDip = 16

            $blankX = $clientLeft + [int][Math]::Round(300 * $scale)
            $blankY = $clientTop + [int][Math]::Round($buttonCenterYDip * $scale)

            # close occupies the rightmost 46 DIP; max center sits 46 + 46/2 = 69 DIP from the right edge.
            $maxX = $clientRight - [int][Math]::Round(69 * $scale)
            $maxY = $clientTop + [int][Math]::Round($buttonCenterYDip * $scale)

            # visible left window edge, vertically centered (within the 6 DIP resize border).
            $edgeX = $wr.Left + [int][Math]::Round(2 * $scale)
            $edgeY = [int][Math]::Round(($wr.Top + $wr.Bottom) / 2)

            $blankCode = Send-HitTest $hwnd $blankX $blankY
            $maxCode = Send-HitTest $hwnd $maxX $maxY
            $edgeCode = Send-HitTest $hwnd $edgeX $edgeY

            $lines = New-Object System.Collections.Generic.List[string]
            $lines.Add('WM_NCHITTEST baseline - ModernScreenShot custom title bar (CURRENT code: WindowChrome +')
            $lines.Add('TitleBarSnapHook on resizable windows; caption buttons 46x32 DIP, caption height 32 DIP)')
            $lines.Add('captured : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'))
            $lines.Add('machine  : ' + $env:COMPUTERNAME + '  OS build ' + [Environment]::OSVersion.Version.Build)
            $lines.Add('exe      : ' + $ExePath)
            $lines.Add('process  : pid ' + $proc.Id)
            $lines.Add('hwnd     : 0x' + $hwnd.ToInt64().ToString('X'))
            $lines.Add('class    : ' + $cls)
            $lines.Add('title    : ' + $title)
            $lines.Add('isForeground : ' + ($fg -eq $hwnd))
            $lines.Add('')
            $lines.Add('geometry:')
            $lines.Add('  windowRect (screen) : L=' + $wr.Left + ' T=' + $wr.Top + ' R=' + $wr.Right + ' B=' + $wr.Bottom + '  (W=' + ($wr.Right - $wr.Left) + ' H=' + ($wr.Bottom - $wr.Top) + ')')
            $lines.Add('  clientRect          : W=' + $clientWidth + ' H=' + $clientHeight)
            $lines.Add('  clientOrigin (screen): ' + $clientLeft + ',' + $clientTop)
            $lines.Add('  clientRight (screen) : ' + $clientRight)
            $lines.Add('  dpi                 : ' + $dpi + '  scale=' + $scale)
            $lines.Add('')
            $lines.Add('points (physical screen pixels):')
            $lines.Add('')
            $lines.Add('  [1] blank caption   x=' + $blankX + ' y=' + $blankY)
            $lines.Add('      SendMessage(hwnd, WM_NCHITTEST, 0, MAKELPARAM(' + $blankX + ',' + $blankY + ')) = ' + $blankCode + ' (' + (Get-HitName $blankCode) + ')')
            $lines.Add('')
            $lines.Add('  [2] max-btn center  x=' + $maxX + ' y=' + $maxY)
            $lines.Add('      SendMessage(hwnd, WM_NCHITTEST, 0, MAKELPARAM(' + $maxX + ',' + $maxY + ')) = ' + $maxCode + ' (' + (Get-HitName $maxCode) + ')')
            $lines.Add('')
            $lines.Add('  [3] edge resize     x=' + $edgeX + ' y=' + $edgeY)
            $lines.Add('      SendMessage(hwnd, WM_NCHITTEST, 0, MAKELPARAM(' + $edgeX + ',' + $edgeY + ')) = ' + $edgeCode + ' (' + (Get-HitName $edgeCode) + ')')
            $lines.Add('')
            $lines.Add('hit-test code reference: 1=HTCLIENT 2=HTCAPTION 8=HTMINBUTTON 9=HTMAXBUTTON 10=HTLEFT 11=HTRIGHT 12=HTTOP 13=HTTOPLEFT 14=HTTOPRIGHT 15=HTBOTTOM 16=HTBOTTOMLEFT 17=HTBOTTOMRIGHT 20=HTCLOSE')

            $baselineText = ($lines -join [Environment]::NewLine) + [Environment]::NewLine
            $baselinePath = Join-Path $evidenceDir 'titlebar-nchittest-baseline.txt'
            Set-Content -Path $baselinePath -Value $baselineText -Encoding UTF8
            $baselinePath2 = Join-Path $outDir 'titlebar-nchittest-baseline.txt'
            Set-Content -Path $baselinePath2 -Value $baselineText -Encoding UTF8
            Write-Host $baselineText
            Write-Host "  written: $baselinePath"
        }

        Kill-ResidualApp
    }
}

# =====================================================================================
# Summary
# =====================================================================================
Write-Host ''
Write-Host '=== SUMMARY ==='
if ($reverseHolds -eq $true) {
    Write-Host 'HOOK ORDER: REVERSE ORDER HOLDS (later-added hook runs first)'
} elseif ($reverseHolds -eq $false) {
    Write-Host 'HOOK ORDER: REVERSE ORDER DOES NOT HOLD (earlier-added hook ran first)'
} else {
    Write-Host 'HOOK ORDER: INDETERMINATE (no order log captured)'
}
if ($handledShortCircuits -eq $true) {
    Write-Host 'HANDLED:    first-running hook setting handled=true SHORT-CIRCUITS remaining hooks'
} elseif ($handledShortCircuits -eq $false) {
    Write-Host 'HANDLED:    handled=true did NOT short-circuit (remaining hooks still ran)'
}
Write-Host "ARTIFACTS:  $outDir"

if ($script:fail -eq 0) {
    Write-Host 'HOOK ORDER PROBE: PASS'
    exit 0
}
Write-Host "HOOK ORDER PROBE: $($script:fail) FAILURE(S)"
exit 1
