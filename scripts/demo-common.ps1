# Shared by demo-record.ps1, demo-post.ps1 and demo-stills.ps1 (dot-sourced): finding ffmpeg, running it, the caption
# layout and the GIF. Host side only; nothing here runs inside the sandbox.

function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false)) }

function Find-Ffmpeg([string]$Given) {
    if ($Given) { return $Given }
    $link = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\ffmpeg.exe"
    $found = if (Test-Path -LiteralPath $link) { $link } else { (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source }
    if (-not $found) { throw "ffmpeg.exe was not found: winget install Gyan.FFmpeg, or pass -Ffmpeg." }
    return $found
}

function Invoke-Ffmpeg([string]$Exe, [string[]]$Arguments, [string]$WorkDir) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }) -join ' '
    $info.WorkingDirectory = $WorkDir
    $info.UseShellExecute = $false
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($info)
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "ffmpeg failed (exit $($p.ExitCode)): $($info.Arguments)`n$err" }
}

function Get-MediaSeconds([string]$Ffprobe, [string]$File) {
    return [double]((& $Ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $File | Out-String).Trim())
}

<#
  The caption layout: a dark band of BandFraction of the screen's height added below the picture; "Step n of m" in small
  grey type at its top, the caption below in white at 5.2 % of the screen's height (47 px at 900), centred, word-wrapped
  (drawtext does not wrap) to at most two lines. Returns the font size, the band height and the wrap width.
#>
function Get-CaptionLayout([int]$Width, [int]$Height, [double]$BandFraction) {
    $font = [int][math]::Round($Height * 0.052)
    return [pscustomobject]@{
        Band = [int]([math]::Round($Height * $BandFraction / 2) * 2)
        Font = $font
        Small = [int][math]::Round($font * 0.58)
        MaxChars = [int][math]::Floor(($Width * 0.92) / ($font * 0.5))
    }
}

function Split-Caption([string]$Text, [int]$MaxChars) {
    $lines = @(); $line = ""
    foreach ($word in ($Text -split '\s+')) {
        if (($line + " " + $word).Trim().Length -gt $MaxChars -and $line) { $lines += $line; $line = $word } else { $line = ($line + " " + $word).Trim() }
    }
    if ($line) { $lines += $line }
    return , $lines
}

# The drawtext pair for one caption (its text files must already be in the work folder: num<i>.txt, cap<i>.txt), shown
# while $Enable holds, on a picture $Height tall with the band below it.
function Get-CaptionFilter([int]$Index, [int]$Height, $Layout, [string]$Enable) {
    $top = [int]($Layout.Band * 0.08)
    $textTop = [int]($Layout.Band * 0.08 + $Layout.Small * 1.5)
    $textRoom = [int]($Layout.Band * 0.92 - $Layout.Small * 1.5)
    return ("drawtext=fontfile=caption.ttf:expansion=none:textfile=num{0:00}.txt:fontsize={1}:fontcolor=0xB8C2D6:x=(w-text_w)/2:y={2}+{3}:{4}," +
            "drawtext=fontfile=caption.ttf:expansion=none:textfile=cap{0:00}.txt:fontsize={5}:fontcolor=white:line_spacing={6}:text_align=C:x=(w-text_w)/2:y={2}+{7}+({8}-text_h)/2:{4}") -f
        $Index, $Layout.Small, $Height, $top, $Enable, $Layout.Font, [int]($Layout.Font * 0.22), $textTop, $textRoom
}

# Segoe UI Semibold (else Segoe UI Bold, Segoe UI, Arial), copied into the work folder as caption.ttf: a relative name
# needs no drive-colon escaping inside a filtergraph.
function Copy-CaptionFont([string]$WorkDir) {
    $font = @("seguisb.ttf", "segoeuib.ttf", "segoeui.ttf", "arial.ttf") | ForEach-Object { Join-Path $env:SystemRoot "Fonts\$_" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $font) { throw "No caption font (Segoe UI or Arial) in $env:SystemRoot\Fonts." }
    Copy-Item -LiteralPath $font -Destination (Join-Path $WorkDir "caption.ttf")
}

# A GIF with a palette made for the video, made smaller (width, then frame rate) until it is under $MaxMB.
function New-DemoGif([string]$Exe, [string]$Mp4, [string]$Gif, [int]$Width, [int]$Fps, [double]$MaxMB, [string]$WorkDir) {
    foreach ($try in @(@($Width, $Fps), @([int]($Width * 0.8), $Fps), @([int]($Width * 0.8), [math]::Max(5, $Fps - 3)), @([int]($Width * 0.6), 5))) {
        Invoke-Ffmpeg $Exe @("-hide_banner", "-y", "-i", $Mp4, "-vf",
            "fps=$($try[1]),scale=$($try[0]):-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff:max_colors=128[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle", $Gif) $WorkDir
        if ((Get-Item -LiteralPath $Gif).Length -le $MaxMB * 1MB) { return }
    }
    Write-Warning "$Gif is still over $MaxMB MB at the smallest setting."
}
