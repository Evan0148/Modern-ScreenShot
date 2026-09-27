# Compares localization keys between the two string dictionaries.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-i18n.ps1  (exit 1 on mismatch)
$ErrorActionPreference = "Stop"

function Get-Keys($path) {
    $content = Get-Content -Path $path -Raw -Encoding UTF8
    $keys = [regex]::Matches($content, 'x:Key="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    return ,$keys
}

$root = Split-Path -Parent $PSScriptRoot
$zh = Get-Keys (Join-Path $root "src\ModernScreenShot.App\Localization\Strings.zh-CN.xaml")
$en = Get-Keys (Join-Path $root "src\ModernScreenShot.App\Localization\Strings.en-US.xaml")

$zhSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$zh)
$enSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$en)

$missingInZh = $enSet | Where-Object { -not $zhSet.Contains($_) }
$missingInEn = $zhSet | Where-Object { -not $enSet.Contains($_) }

Write-Host ("zh-CN keys: " + $zhSet.Count)
Write-Host ("en-US keys: " + $enSet.Count)

$failed = $false
foreach ($k in $missingInZh) { Write-Host "MISSING in zh-CN: $k"; $failed = $true }
foreach ($k in $missingInEn) { Write-Host "MISSING in en-US: $k"; $failed = $true }

# Empty values would render blank UI.
foreach ($file in @("src\ModernScreenShot.App\Localization\Strings.zh-CN.xaml", "src\ModernScreenShot.App\Localization\Strings.en-US.xaml")) {
    $full = Join-Path $root $file
    Select-String -Path $full -Pattern 'x:Key="([^"]+)">\s*</sys:String>' | ForEach-Object {
        Write-Host ("EMPTY VALUE in " + $file + ": " + $_.Matches[0].Groups[1].Value)
        $failed = $true
    }
}

if ($failed) { Write-Host "i18n check FAILED"; exit 1 }
Write-Host "i18n check OK"
exit 0
