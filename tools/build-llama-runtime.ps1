<#
.SYNOPSIS
    Fetches the llama.cpp CPU runtime that ships next to the app.

.DESCRIPTION
    Produces <repo>\src\ModernScreenShot.App\Assets\llama\ containing llama-server.exe and the
    ggml/llama DLLs. The app runs translation in that child process and talks OpenAI-compatible
    HTTP to it, so the model's chat template is applied by llama.cpp itself rather than
    re-implemented here.

    Replaces the previous Python/Argos engine: this runtime is ~42 MB against that engine's 171 MB,
    and it is permissively licensed (llama.cpp is MIT).

    Only the server binary and its libraries are kept; the other CLI tools in the release archive
    (llama-cli, llama-bench, the vision/multimodal CLIs, ...) are not needed at runtime.
#>
[CmdletBinding()]
param(
    [string] $Build        = "b11379",
    [string] $OutDir       = "",
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo "src\ModernScreenShot.App\Assets\llama" }

$work = Join-Path $env:TEMP "llama-runtime-build"
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# github.com release downloads are frequently throttled to tens of KB/s on this network; the
# ghfast.top mirror measured ~900 KB/s for the same asset. Both are tried.
function Get-File([string[]] $Urls, [string] $Destination) {
    if ((Test-Path $Destination) -and (Get-Item $Destination).Length -gt 1MB) {
        Write-Host "using cached $([IO.Path]::GetFileName($Destination))"
        return
    }
    $lastError = $null
    foreach ($url in $Urls) {
        try {
            Write-Host "downloading $url"
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            Invoke-WebRequest -Uri $url -OutFile $Destination -UseBasicParsing -TimeoutSec 1800
            $size = (Get-Item $Destination).Length
            if ($size -le 0) { throw "empty file" }
            "{0:N1} MB in {1:N0}s" -f ($size / 1MB), $sw.Elapsed.TotalSeconds | Write-Host
            return
        } catch {
            $lastError = $_
            Write-Host "  failed: $($_.Exception.Message)" -ForegroundColor Yellow
            Remove-Item $Destination -Force -ErrorAction SilentlyContinue
        }
    }
    throw "could not download the llama.cpp runtime: $lastError"
}

$asset = "llama-$Build-bin-win-cpu-x64.zip"
$direct = "https://github.com/ggml-org/llama.cpp/releases/download/$Build/$asset"

Step "Downloading llama.cpp $Build (CPU, x64)"
$zip = Join-Path $work $asset
Get-File @("https://ghfast.top/$direct", $direct) $zip

Step "Extracting the server runtime"
$extract = Join-Path $work "extract"
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $extract -Force

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Everything lives in one flat folder in the archive; the server needs its exe and its DLLs.
# The release also ships a *-impl.dll for every other CLI in the box; those are dead weight here
# (llama-cli-impl alone is 3 MB), so only the server's own dependency set is kept.
$unused = @(
    "llama-cli-impl.dll", "llama-bench-impl.dll", "llama-perplexity-impl.dll",
    "llama-completion-impl.dll", "llama-quantize-impl.dll", "llama-batched-bench-impl.dll",
    "llama-fit-params-impl.dll", "llama-gguf-split-impl.dll", "llama-imatrix-impl.dll",
    "ggml-rpc.dll"
)
$keep = Get-ChildItem $extract -Recurse -File | Where-Object {
    ($_.Name -eq "llama-server.exe" -or $_.Extension -eq ".dll") -and ($unused -notcontains $_.Name)
}
if (-not ($keep | Where-Object Name -eq "llama-server.exe")) { throw "llama-server.exe was not in the archive." }
$keep | ForEach-Object { Copy-Item $_.FullName (Join-Path $OutDir $_.Name) -Force }

@(
    "llama.cpp $Build (win-cpu-x64, MIT)",
    "built $(Get-Date -Format 'yyyy-MM-dd')"
) | Set-Content (Join-Path $OutDir "runtime-version.txt") -Encoding UTF8

Step "Verifying the server binary"
$server = Join-Path $OutDir "llama-server.exe"
& $server --version 2>&1 | Select-Object -First 3

Write-Host "`n=== Runtime ready ===" -ForegroundColor Green
$files = Get-ChildItem $OutDir -Recurse -File
"{0} files, {1:N1} MB -> {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $OutDir
