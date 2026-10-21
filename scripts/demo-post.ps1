<#
.SYNOPSIS
  Turns one demo-record.ps1 run (raw.mp4 and steps.json) into dist\demos\<name>.mp4 with burned-in captions and click
  highlights, <name>.gif for the web (under -MaxGifMB), <name>-poster.png, and a PNG per step for the website guide.

.DESCRIPTION
  Captions: one per step, from the demo's data file (scripts\demos\<name>.json, matched by step id, so a caption can be
  reworded without recording again), shown from the moment the step began until the next one began; the demo's title
  shows before the first step. They sit in a dark band added BELOW the recorded screen, not over it, so a caption never
  hides the taskbar, the tray icon or a dialog being clicked. The text is large (5.2 % of the screen's height: 47 px at
  900), white on near-black, centred, at most two lines, with "Step n of m" above it (demo-common.ps1).

  Highlights: every click and "point" the step log records gets a yellow rounded rectangle with a dark edge around the
  element (from UI Automation's rectangle), from a second before the click until shortly after it (a "point" keeps it
  to the end of its step); a click made by coordinates (no rectangle) gets a circle. A click made through a UI
  Automation pattern (no pointer: a recorded fallback) gets none.

  The step times are wall-clock times in steps.json; ffmpeg's gdigrab logs the wall-clock time of its first frame
  ("start:"), which puts both on one clock.

.PARAMETER Run
  A run folder from demo-record.ps1 (raw.mp4, steps.json, job.json).
.PARAMETER Demo
  The demo's data file. Default: scripts\demos\<demoFile from job.json>.
.PARAMETER Out
  Default: dist\demos. Writes <name>.mp4, <name>.gif, <name>-poster.png and <name>\ (frames, steps.json, summary.json).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\demo-post.ps1 -Run dist\demos\runs\20261021-181542-change-model
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Run,
    [string]$Demo,
    [string]$Out,
    [string]$Ffmpeg,
    [double]$BandFraction = 0.24,
    [int]$GifWidth = 800,
    [int]$GifFps = 8,
    [double]$MaxGifMB = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "demo-common.ps1")
$root = Split-Path -Parent $PSScriptRoot
$Run = (Resolve-Path -LiteralPath $Run).Path
if (-not $Out) { $Out = Join-Path $root "dist\demos" }
$Ffmpeg = Find-Ffmpeg $Ffmpeg
$ffprobe = Join-Path (Split-Path -Parent $Ffmpeg) "ffprobe.exe"
$steps = Get-Content -LiteralPath (Join-Path $Run "steps.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$job = Get-Content -LiteralPath (Join-Path $Run "job.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $Demo) { $Demo = Join-Path $PSScriptRoot "demos\$($job.demoFile)" }
$data = if (Test-Path -LiteralPath $Demo) { Get-Content -LiteralPath $Demo -Raw -Encoding UTF8 | ConvertFrom-Json } else { $job.demo }
$name = $steps.demo
$raw = Join-Path $Run "raw.mp4"
if (-not (Test-Path -LiteralPath $raw)) { throw "$Run holds no raw.mp4." }
if (-not $steps.videoStartEpoch) { throw "steps.json has no videoStartEpoch (ffmpeg's first-frame time), so the captions cannot be placed." }
$inv = [Globalization.CultureInfo]::InvariantCulture

# The video's size: the sandbox's desktop, cropped to even sides for H.264.
$size = (& $ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of default=noprint_wrappers=1:nokey=1 $raw | Out-String).Trim() -split "\r?\n"
$videoW = [int]$size[0]; $videoH = [int]$size[1]
$screenW = [int]$steps.screen.width; $screenH = [int]$steps.screen.height
$cropW = [math]::Min($videoW, $screenW - ($screenW % 2)); $cropH = [math]::Min($videoH, $screenH - ($screenH % 2))
$cropTop = 0
$scale = $cropW / $screenW # screen pixels to video pixels (1 unless the capture was scaled)
$duration = Get-MediaSeconds $ffprobe $raw
function VideoTime([double]$Epoch) { return [math]::Max(0.0, [math]::Round($Epoch - [double]$steps.videoStartEpoch, 3)) } # 0.0, not 0: in Windows PowerShell 5.1 [math]::Max(0, 7.3) returns 7
function Between([double]$From, [double]$To) { return "enable='between(t,{0},{1})'" -f $From.ToString($inv), $To.ToString($inv) }

# Captions, by step id from the data file (the step log's copy when the file has no such id).
$captionById = @{}
foreach ($s in @(Get-Prop $data "steps" @())) { if (Get-Prop $s "id") { $captionById[$s.id] = Get-Prop $s "caption" } }
$shown = @($steps.steps | Where-Object { -not $_.skipped -and $_.start })
if (-not $shown) { throw "steps.json records no step that ran." }

$work = Join-Path $env:TEMP ("dictum-demo-post-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    Copy-CaptionFont $work
    $layout = Get-CaptionLayout $cropW $cropH $BandFraction
    $total = $shown.Count
    $filters = @("[0:v]crop=${cropW}:${cropH}:0:${cropTop},pad=${cropW}:$($cropH + $layout.Band):0:0:color=0x111418[base]")
    $last = "base"
    $inputs = @("-i", $raw)
    # The demo's title in the band until the first step begins.
    $firstStart = VideoTime $shown[0].start
    if ((Get-Prop $data "title") -and $firstStart -gt 0.5) {
        Write-Utf8 (Join-Path $work "title.txt") ([string]$data.title)
        $filters += "[$last]drawtext=fontfile=caption.ttf:expansion=none:textfile=title.txt:fontsize=$([int]($layout.Font * 1.1)):fontcolor=0xFFD400:x=(w-text_w)/2:y=${cropH}+($($layout.Band)-text_h)/2:$(Between 0 $firstStart)[title]"
        $last = "title"
    }
    $highlights = @()
    for ($i = 0; $i -lt $total; $i++) {
        $step = $shown[$i]
        $from = VideoTime $step.start
        $to = if ($i + 1 -lt $total) { VideoTime $shown[$i + 1].start } else { $duration }
        $text = if ($step.id -and $captionById.ContainsKey($step.id)) { $captionById[$step.id] } else { $step.caption }
        $lines = Split-Caption $text $layout.MaxChars
        if ($lines.Count -gt 2) { Write-Warning "Step $($step.index) ($($step.id)): the caption needs $($lines.Count) lines at this size; shorten it." }
        Write-Utf8 (Join-Path $work ("cap{0:00}.txt" -f $i)) ($lines -join "`n")
        Write-Utf8 (Join-Path $work ("num{0:00}.txt" -f $i)) "Step $($i + 1) of $total"
        $filters += "[$last]$(Get-CaptionFilter $i $cropH $layout (Between $from $to))[c$i]"
        $last = "c$i"
        foreach ($click in @($step.clicks)) {
            if (-not $click -or $null -eq $click.x) { continue }
            # A click's ring ends 0.3 s after the click: the click often closes the element's window or scrolls the page, and a
            # ring left there circles unrelated content (lens on 7900e49). steps.json does not say when a window went, so the
            # short fixed tail stands in for it. A "point" ring lasts until the next step's caption starts, never into it.
            $until = if ($click.kind -eq "point") { $to } else { [math]::Min($to, (VideoTime ([double]$click.epoch + 0.3))) }
            $highlights += [pscustomobject]@{ Click = $click; From = [math]::Max($from, (VideoTime ([double]$click.epoch - 1.0))); To = $until }
        }
    }

    # One highlight image per click: a rounded rectangle around the element, or a circle where only a point is known.
    Add-Type -AssemblyName System.Drawing
    $n = 0
    foreach ($h in $highlights) {
        $n++
        $edge = 7
        $c = $h.Click
        $box = $c.rect -and $c.x -ge $c.rect.x - 1 -and $c.x -le $c.rect.x + $c.rect.w + 1 -and $c.y -ge $c.rect.y - 1 -and $c.y -le $c.rect.y + $c.rect.h + 1
        # The ring sits 4 px outside the element; a small control (under 24 px tall) gets pad 8 so the ring does not cut into the words; on the panel's Ready label it covers the status dot beside it
        $pad = if ($box -and $c.rect.h -lt 24) { 8 } else { 4 }
        if ($box) {
            $w = [int]($c.rect.w * $scale) + 2 * ($pad + $edge); $hh = [int]($c.rect.h * $scale) + 2 * ($pad + $edge)
            $x = [int]($c.rect.x * $scale) - $pad - $edge; $y = [int]($c.rect.y * $scale) - $pad - $edge
        } else {
            $w = 84; $hh = 84; $x = [int]($c.x * $scale) - 42; $y = [int]($c.y * $scale) - 42
        }
        $bmp = New-Object System.Drawing.Bitmap $w, $hh
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $dark = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 17, 20, 24)), ($edge + 4)
        $yellow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 212, 0)), $edge
        $inner = New-Object System.Drawing.RectangleF ($edge / 2 + 2), ($edge / 2 + 2), ($w - $edge - 4), ($hh - $edge - 4)
        if ($box) {
            $radius = [math]::Min(18.0, [math]::Min([math]::Min($inner.Width, $inner.Height) / 2, ($c.rect.h * $scale) / 4)) # a quarter of the element height at most: no stadium ring on a small control
            $d = 2 * $radius
            $path = New-Object System.Drawing.Drawing2D.GraphicsPath
            $path.AddArc($inner.X, $inner.Y, $d, $d, 180, 90); $path.AddArc($inner.Right - $d, $inner.Y, $d, $d, 270, 90)
            $path.AddArc($inner.Right - $d, $inner.Bottom - $d, $d, $d, 0, 90); $path.AddArc($inner.X, $inner.Bottom - $d, $d, $d, 90, 90)
            $path.CloseFigure()
            $g.DrawPath($dark, $path); $g.DrawPath($yellow, $path)
        } else {
            $g.DrawEllipse($dark, $inner); $g.DrawEllipse($yellow, $inner)
        }
        $g.Dispose()
        $bmp.Save((Join-Path $work ("hl{0:00}.png" -f $n)), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $inputs += @("-loop", "1", "-i", ("hl{0:00}.png" -f $n))
        $filters += "[$last][${n}:v]overlay=x=${x}:y=${y}:shortest=1:$(Between $h.From $h.To)[h$n]"
        $last = "h$n"
    }
    $filters += "[$last]format=yuv420p[v]"
    Write-Utf8 (Join-Path $work "filter.txt") ($filters -join ";`n")

    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $Out = (Resolve-Path -LiteralPath $Out).Path
    $mp4 = Join-Path $Out "$name.mp4"
    Invoke-Ffmpeg $Ffmpeg (@("-hide_banner", "-y") + $inputs + @("-/filter_complex", "filter.txt", "-map", "[v]", "-c:v", "libx264", "-preset", "slow", "-crf", "20",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", $mp4)) $work
    $gif = Join-Path $Out "$name.gif"
    New-DemoGif $Ffmpeg $mp4 $gif $GifWidth $GifFps $MaxGifMB $work

    # Stills: one per step just before its click (or halfway through a step without one), captioned and raw; the poster.
    $folder = Join-Path $Out $name
    $frames = Join-Path $folder "frames"
    New-Item -ItemType Directory -Force -Path (Join-Path $frames "raw") | Out-Null
    Get-ChildItem -LiteralPath $frames -Filter "*.png" -Recurse | Remove-Item -Force
    $posterTime = $null
    for ($i = 0; $i -lt $total; $i++) {
        $step = $shown[$i]
        $from = VideoTime $step.start
        $to = if ($i + 1 -lt $total) { VideoTime $shown[$i + 1].start } else { $duration }
        $click = @($step.clicks | Where-Object { $_ -and $null -ne $_.epoch }) | Select-Object -First 1
        # Just before the click: the pointer rests on the target and the highlight is on; the click itself may close it. A step that types is taken after the typing.
        # When typing ended (every "type" action, with or without a field to click into): the still shows the typed text, not the empty field.
        $typed = Get-Prop $click "typedEpoch"
        if ($null -eq $typed) { $typed = Get-Prop $step "typedEpoch" }
        $at = if ($null -ne $typed) { [math]::Min([math]::Max($from, (VideoTime ([double]$typed)) + 0.3), $to - 0.1) }
              elseif ($click) { [math]::Max($from, (VideoTime ([double]$click.epoch)) - 0.35) } else { ($from + $to) / 2 }
        $at = [math]::Max(0.0, [math]::Min($at, $duration - 0.1))
        $label = "{0:00}-{1}" -f ($i + 1), $(if ($step.id) { $step.id } else { "step" })
        $stamp = $at.ToString("0.000", $inv)
        Invoke-Ffmpeg $Ffmpeg @("-hide_banner", "-y", "-ss", $stamp, "-i", $mp4, "-frames:v", "1", (Join-Path $frames "$label.png")) $work
        Invoke-Ffmpeg $Ffmpeg @("-hide_banner", "-y", "-ss", $stamp, "-i", $raw, "-frames:v", "1", "-vf", "crop=${cropW}:${cropH}:0:${cropTop}", (Join-Path $frames "raw\$label.png")) $work
        if (@(@(Get-Prop $data "steps" @()) | Where-Object { (Get-Prop $_ "id") -eq $step.id -and (Get-Prop $_ "poster") })) { $posterTime = $stamp }
    }
    if (-not $posterTime) { $posterTime = ([math]::Max(0.0, $duration - 1.5)).ToString("0.000", $inv) }
    $poster = Join-Path $Out "$name-poster.png"
    Invoke-Ffmpeg $Ffmpeg @("-hide_banner", "-y", "-ss", $posterTime, "-i", $mp4, "-frames:v", "1", $poster) $work
    Copy-Item -LiteralPath (Join-Path $Run "steps.json") -Destination (Join-Path $folder "steps.json") -Force

    # summary.json holds paths relative to $Out (Get-OutRelative), so no host profile path is in it.
    $summary = [ordered]@{
        demo = $name; result = $steps.result; mp4 = (Get-OutRelative $mp4 $Out); seconds = [math]::Round((Get-MediaSeconds $ffprobe $mp4), 1); mp4Bytes = (Get-Item -LiteralPath $mp4).Length
        gif = (Get-OutRelative $gif $Out); gifBytes = (Get-Item -LiteralPath $gif).Length; poster = (Get-OutRelative $poster $Out); frames = (Get-OutRelative $frames $Out); steps = $total
        fallbacks = @($steps.fallbacks | Where-Object { $_ }).Count; size = "${cropW}x$($cropH + $layout.Band)"; run = (Get-OutRelative $Run $Out)
    }
    Write-Utf8 (Join-Path $folder "summary.json") ($summary | ConvertTo-Json)
    [pscustomobject]$summary | Format-List | Out-String | Write-Host
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
