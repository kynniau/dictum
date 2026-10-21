<#
.SYNOPSIS
  Builds a stills walkthrough (scripts\demos\<name>.json, kind "stills") from frames of Dictum's own windows rendered on
  Avalonia's headless platform with Skia: dist\demos\<name>.mp4, .gif, -poster.png and a PNG per slide.

.DESCRIPTION
  Windows Sandbox has no microphone, so a dictation cannot be recorded there (docs\DEMOS.md). The pill can still be
  drawn by Dictum's own code: tests\Dictum.Tests\PillStillsTests renders each pill state through Skia and, with
  DICTUM_STILLS_DIR set, saves pill-<state>.png. This script builds and runs that test, then lays each slide's pill on a
  plain background, at the bottom centre where Dictum shows it, with the same caption band as demo-post.ps1. The video
  is an illustration of the indicator, not a recording of a dictation: its data file says so.

.PARAMETER Demo
  The stills data file's name (scripts\demos\<name>.json) or path. Default: dictation-stills.
.PARAMETER Stills
  A folder that already holds the pill-<state>.png frames; skips building and running the test.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\demo-stills.ps1
#>
[CmdletBinding()]
param(
    [string]$Demo = "dictation-stills",
    [string]$Stills,
    [string]$Out,
    [string]$Ffmpeg,
    [int]$Width = 1600,
    [int]$Height = 900,
    [double]$BandFraction = 0.24,
    [int]$GifWidth = 800,
    [int]$GifFps = 8,
    [double]$MaxGifMB = 15
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "demo-common.ps1")
. (Join-Path $PSScriptRoot "build-common.ps1")
$root = Split-Path -Parent $PSScriptRoot
$file = if (Test-Path -LiteralPath $Demo) { (Resolve-Path -LiteralPath $Demo).Path } else { Join-Path $PSScriptRoot "demos\$Demo.json" }
$data = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
if ($data.kind -ne "stills") { throw "$file is not a stills walkthrough (kind ""stills""): record it with demo-record.ps1." }
if (-not $Out) { $Out = Join-Path $root "dist\demos" }
$Ffmpeg = Find-Ffmpeg $Ffmpeg
$ffprobe = Join-Path (Split-Path -Parent $Ffmpeg) "ffprobe.exe"
$inv = [Globalization.CultureInfo]::InvariantCulture

$work = Join-Path $env:TEMP ("dictum-demo-stills-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    if (-not $Stills) {
        # The frames, from the test: build it, then run only that class with the output folder set.
        $Stills = Join-Path $work "pill"
        $dotnet = Use-DotnetSdk
        $project = Join-Path $root "tests\Dictum.Tests\Dictum.Tests.csproj"
        & $dotnet build $project -c Debug -v q -nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Building the tests failed." }
        $exe = Get-ChildItem -Path (Join-Path $root "tests\Dictum.Tests\bin\Debug") -Filter "Dictum.Tests.exe" -Recurse | Select-Object -First 1 -ExpandProperty FullName
        $env:DICTUM_STILLS_DIR = $Stills
        try { & $exe -class Dictum.Tests.PillStillsTests | Out-Host } finally { Remove-Item Env:\DICTUM_STILLS_DIR }
        if ($LASTEXITCODE -ne 0) { throw "PillStillsTests failed (exit $LASTEXITCODE): no stills." }
    }

    Copy-CaptionFont $work
    $layout = Get-CaptionLayout $Width $Height $BandFraction
    $slides = @($data.slides)
    $total = $slides.Count
    $parts = @()
    $name = $data.name
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $Out = (Resolve-Path -LiteralPath $Out).Path
    $frames = Join-Path (Join-Path $Out $name) "frames"
    New-Item -ItemType Directory -Force -Path $frames | Out-Null
    Get-ChildItem -LiteralPath $frames -Filter "*.png" | Remove-Item -Force
    for ($i = 0; $i -lt $total; $i++) {
        $slide = $slides[$i]
        $png = Join-Path $Stills "pill-$($slide.still).png"
        if (-not (Test-Path -LiteralPath $png)) { throw "No still $png (states: $((Get-ChildItem -LiteralPath $Stills -Filter 'pill-*.png' | ForEach-Object BaseName) -join ', '))." }
        Copy-Item -LiteralPath $png -Destination (Join-Path $work ("still{0:00}.png" -f $i))
        $lines = Split-Caption $slide.caption $layout.MaxChars
        if ($lines.Count -gt 2) { Write-Warning "Slide $($slide.id): the caption needs $($lines.Count) lines at this size; shorten it." }
        Write-Utf8 (Join-Path $work ("cap{0:00}.txt" -f $i)) ($lines -join "`n")
        Write-Utf8 (Join-Path $work ("num{0:00}.txt" -f $i)) "Step $($i + 1) of $total"
        $seconds = [double]$slide.seconds
        $part = Join-Path $work ("part{0:00}.mp4" -f $i)
        # The pill at the bottom centre of the picture, where Dictum shows it above the taskbar. The headless frame has an
        # opaque black background (no window transparency there), keyed out here; the pill itself is navy, never pure black.
        $filter = "[0:v]pad=${Width}:$($Height + $layout.Band):0:0:color=0x111418[bg];" +
                  "[1:v]colorkey=0x000000:0.01:0[pill];[bg][pill]overlay=x=(main_w-overlay_w)/2:y=$Height-overlay_h-60:format=auto[p];" +
                  "[p]$(Get-CaptionFilter $i $Height $layout "enable=1"),format=yuv420p[v]"
        Write-Utf8 (Join-Path $work "filter.txt") $filter
        Invoke-Ffmpeg $Ffmpeg @("-hide_banner", "-y", "-f", "lavfi", "-i", "color=c=$($data.background):s=${Width}x${Height}:r=30:d=$($seconds.ToString($inv))",
            "-loop", "1", "-framerate", "30", "-i", ("still{0:00}.png" -f $i), "-/filter_complex", "filter.txt", "-map", "[v]", "-t", $seconds.ToString($inv),
            "-c:v", "libx264", "-preset", "slow", "-crf", "20", "-pix_fmt", "yuv420p", "-an", $part) $work
        Invoke-Ffmpeg $Ffmpeg @("-hide_banner", "-y", "-ss", "1", "-i", $part, "-frames:v", "1", (Join-Path $frames ("{0:00}-{1}.png" -f ($i + 1), $slide.id))) $work
        $parts += "file '$(Split-Path -Leaf $part)'"
    }
    Write-Utf8 (Join-Path $work "parts.txt") ($parts -join "`n")
    $mp4 = Join-Path $Out "$name.mp4"
    Invoke-Ffmpeg $Ffmpeg @("-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", "parts.txt", "-c", "copy", "-movflags", "+faststart", $mp4) $work
    $gif = Join-Path $Out "$name.gif"
    New-DemoGif $Ffmpeg $mp4 $gif $GifWidth $GifFps $MaxGifMB $work
    $poster = Join-Path $Out "$name-poster.png"
    $posterFrame = Get-ChildItem -LiteralPath $frames -Filter "*.png" | Sort-Object Name | Select-Object -Last 1
    Copy-Item -LiteralPath $posterFrame.FullName -Destination $poster -Force
    $summary = [ordered]@{ demo = $name; kind = "stills"; mp4 = $mp4; seconds = [math]::Round((Get-MediaSeconds $ffprobe $mp4), 1); mp4Bytes = (Get-Item -LiteralPath $mp4).Length
        gif = $gif; gifBytes = (Get-Item -LiteralPath $gif).Length; poster = $poster; frames = $frames; slides = $total; size = "${Width}x$($Height + $layout.Band)" }
    Write-Utf8 (Join-Path (Join-Path $Out $name) "summary.json") ($summary | ConvertTo-Json)
    [pscustomobject]$summary | Format-List | Out-String | Write-Host
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
