<#
.SYNOPSIS
  Downloads a speech model (whisper.cpp ggml) or a cleanup model (GGUF) into %LOCALAPPDATA%\Dictum\models.

.DESCRIPTION
  Same hosts, commits, file names, sizes and SHA-256 hashes as the app (a unit test keeps the tables below in sync
  with ModelCatalog and CleanupCatalog). Every URL is pinned to a repository commit, so it keeps serving the bytes the
  hashes describe. Downloads to a .part file and resumes a dropped or stalled connection (30 s without data counts as
  stalled; up to 8 attempts, waiting 1, 2, 4, 8, 16 s), verifies the byte size and SHA-256, then renames; a mismatch
  deletes the .part file. If all 8 attempts fail, the .part file is kept: run the same command again to carry on from
  where it stopped. Commits and hashes are from the Hugging Face API, read 15/10/2026.
  Gated models (Gemma) need a Hugging Face account that has accepted the licence on huggingface.co. Set HF_TOKEN for
  this session, or leave it unset and the script asks for the token (typing hidden). The token is sent only to
  huggingface.co and only for a gated model, in a header file that is deleted afterwards; it is never logged. -Token
  still works but warns: a token on the command line can be read by other programs running as you.
  `Dictum.exe --download-model <name>` / `--download-cleanup-model <name>` do the same from the app (no tokens).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\download-model.ps1 -Model medium.en
  powershell -ExecutionPolicy Bypass -File scripts\download-model.ps1 -Cleanup qwen2.5-1.5b
  powershell -ExecutionPolicy Bypass -File scripts\download-model.ps1 -Cleanup gemma-3-1b    (asks for the token)
#>
param(
    [string]$Model,
    [string]$Cleanup,
    [string]$ModelsDir = (Join-Path $env:LOCALAPPDATA "Dictum\models"),
    [string]$Token
)

$ErrorActionPreference = "Stop"
if (-not $Model -and -not $Cleanup) { $Model = "medium.en" }
if ($Model -and $Cleanup) { throw "Use either -Model or -Cleanup, not both." }
if ($Token) { Write-Warning "-Token puts the token on the command line, where other programs running as you can read it. Set HF_TOKEN for this session instead, or leave both out to be asked." }
elseif ($env:HF_TOKEN) { $Token = $env:HF_TOKEN }

# Speech models: https://huggingface.co/ggerganov/whisper.cpp/resolve/<commit>/ggml-<name>.bin  (name = bytes, sha256)
$speechRevision = "5359861c739e955e79d9a303bcbc70fb988958b1"
$speech = [ordered]@{
    "tiny.en"             = @(77704715, "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f")
    "base.en"             = @(147964211, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002")
    "small.en"            = @(487614201, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d")
    "small.en-q5_1"       = @(190098681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30")
    "small.en-q8_0"       = @(264477561, "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067")
    "medium.en"           = @(1533774781, "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356")
    "medium.en-q5_0"      = @(539225533, "76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0")
    "medium.en-q8_0"      = @(823382461, "43fa2cd084de5a04399a896a9a7a786064e221365c01700cea4666005218f11c")
    "large-v3-turbo"      = @(1624555275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69")
    "large-v3-turbo-q5_0" = @(574041195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")
    "small"               = @(487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b")
    "small-q5_1"          = @(190085487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb")
    "medium"              = @(1533763059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208")
    "medium-q5_0"         = @(539212467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f")
    "large-v3"            = @(3095033483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2")
    "large-v3-q5_0"       = @(1081140203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1")
}

# Cleanup models (vendor organisations): name = repo, file, bytes, sha256 ($null where the API masks it), gated, commit
$cleanupModels = [ordered]@{
    "qwen2.5-0.5b" = @("Qwen/Qwen2.5-0.5B-Instruct-GGUF", "qwen2.5-0.5b-instruct-q4_k_m.gguf", 491400032, "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db", $false, "9217f5db79a29953eb74d5343926648285ec7e67")
    "qwen2.5-1.5b" = @("Qwen/Qwen2.5-1.5B-Instruct-GGUF", "qwen2.5-1.5b-instruct-q4_k_m.gguf", 1117320736, "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e", $false, "91cad51170dc346986eccefdc2dd33a9da36ead9")
    "smollm2-1.7b" = @("HuggingFaceTB/SmolLM2-1.7B-Instruct-GGUF", "smollm2-1.7b-instruct-q4_k_m.gguf", 1055609536, "decd2598bc2c8ed08c19adc3c8fdd461ee19ed5708679d1c54ef54a5a30d4f33", $false, "2d4a76a30b4af41ecd395c35725ac11688d4cfe4")
    "gemma-3-1b"   = @("google/gemma-3-1b-it-qat-q4_0-gguf", "gemma-3-1b-it-q4_0.gguf", 1003541152, $null, $true, "d1be121d36172a4b0b964657e2ee859d61138593")
    "gemma-3-4b"   = @("google/gemma-3-4b-it-qat-q4_0-gguf", "gemma-3-4b-it-q4_0.gguf", 3155051328, $null, $true, "15f73f5eee9c28f53afefef5723e29680c2fc78a")
}

$gated = $false
if ($Cleanup) {
    if (-not $cleanupModels.Contains($Cleanup)) { throw "Unknown cleanup model '$Cleanup'. Choose one of: $($cleanupModels.Keys -join ', ')." }
    $entry = $cleanupModels[$Cleanup]
    $repo = $entry[0]; $file = $entry[1]; $size = $entry[2]; $hash = $entry[3]; $gated = $entry[4]; $revision = $entry[5]
    if ($gated -and -not $Token) {
        try { $Token = [System.Net.NetworkCredential]::new("", (Read-Host "Hugging Face token for $repo (typing is hidden)" -AsSecureString)).Password }
        catch { $Token = $null }   # a non-interactive session cannot ask
    }
    if ($gated -and -not $Token) {
        throw "$Cleanup requires a Hugging Face login to download: accept its licence on https://huggingface.co/$repo while logged in, then set HF_TOKEN or enter the token when asked, or copy the file in with install.ps1 -ModelsFrom."
    }
} else {
    if ($Model -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $Model.Contains("..")) { throw "Invalid model name '$Model'." }
    $repo = "ggerganov/whisper.cpp"; $revision = $speechRevision; $size = $null; $hash = $null
    $file = "ggml-$Model.bin"
    if ($speech.Contains($Model)) { $size = $speech[$Model][0]; $hash = $speech[$Model][1] }
    else { Write-Warning "'$Model' is not in Dictum's catalogue; its size and SHA-256 cannot be verified." }
}
$url = "https://huggingface.co/$repo/resolve/$revision/$file"
if (-not $gated) { $Token = $null }   # the token goes only where it is needed

$target = Join-Path $ModelsDir $file
$part = "$target.part"
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

# A gated repo's API masks the hash for anonymous callers; with the token, read it from the same API the catalogue uses.
if ($gated -and -not $hash) {
    try {
        $tree = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$repo/tree/$revision" -Headers @{ Authorization = "Bearer $Token" } -UseBasicParsing
        $hash = ($tree | Where-Object { $_.path -eq $file }).lfs.oid
    } catch { $hash = $null }
    if ($hash -notmatch '^[0-9a-f]{64}$') { $hash = $null; Write-Warning "Could not read the SHA-256 of $file with this token; only its size will be checked." }
}

if (Test-Path $target) {
    $existing = (Get-Item $target).Length
    if (-not $size -or ($existing -eq $size -and (-not $hash -or (Get-Sha256 $target) -eq $hash))) {
        Write-Host "$target already exists and matches ($existing bytes). Nothing to do."
        exit 0
    }
    Write-Warning "$target does not match the catalogue (size or SHA-256); downloading again."
}

Write-Host "Downloading $url"
Write-Host "        to $target"
$curl = Join-Path $env:SystemRoot "System32\curl.exe"
if (Test-Path $curl) {
    # Resume with curl's -C - (offset = the .part file's size) on every attempt; HTTP errors are not retried. --fail keeps an
    # HTTP error page out of the .part file; 30 s without data (or without a connection) ends the attempt so the loop resumes.
    $headerFile = $null
    if ($Token) { $headerFile = [IO.Path]::GetTempFileName(); Set-Content -LiteralPath $headerFile -Value "Authorization: Bearer $Token" -Encoding ascii }
    $keepPart = $false
    try {
        for ($attempt = 1; ; $attempt++) {
            # TLS 1.2 as in the app: Windows TLS 1.3 dropped large CDN downloads far more often here (DECISIONS.md).
            $curlArgs = @("--location", "--fail", "--silent", "--show-error", "--tlsv1.2", "--tls-max", "1.2", "--continue-at", "-", "--output", $part, "--write-out", "%{http_code}",
                "--connect-timeout", "30", "--speed-limit", "1", "--speed-time", "30")
            if ($headerFile) { $curlArgs += @("--header", "@$headerFile") }
            $code = & $curl @curlArgs $url
            $exit = $LASTEXITCODE
            if ($exit -eq 0 -and $code -match '^(200|206)$') { break }
            if ($code -match '^(401|403)$') { throw "HTTP $code from huggingface.co: $file requires a Hugging Face login that has accepted its licence (check HF_TOKEN or the token you entered)." }
            if ($code -eq '416' -and $size -and (Test-Path $part) -and (Get-Item $part).Length -eq $size) { break }   # already complete
            if ($code -match '^4\d\d$') { throw "HTTP $code from huggingface.co for $file." }
            if ($attempt -ge 8) {
                $keepPart = $true
                throw "Download of $file failed after 8 attempts (curl exit $exit, HTTP $code). The partial file was kept: check your network, then run the same command again to carry on from where it stopped."
            }
            $wait = [Math]::Min(16, [Math]::Pow(2, $attempt - 1))
            Write-Warning "Attempt $attempt failed (curl exit $exit, HTTP $code) at $((Get-Item $part -ErrorAction SilentlyContinue).Length) bytes; resuming in $wait s."
            Start-Sleep -Seconds $wait
        }
    } catch {
        if (-not $keepPart -and (Test-Path $part)) { Remove-Item -LiteralPath $part -Force }
        throw
    } finally {
        if ($headerFile) { Remove-Item -LiteralPath $headerFile -Force -ErrorAction SilentlyContinue }
    }
} else {
    $ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is far faster without the progress bar
    $headers = @{}; if ($Token) { $headers.Authorization = "Bearer $Token" }
    try { Invoke-WebRequest -Uri $url -OutFile $part -UseBasicParsing -Headers $headers }
    catch { if (Test-Path $part) { Remove-Item -LiteralPath $part -Force }; throw }
}

$got = (Get-Item $part).Length
if ($size -and $got -ne $size) {
    Remove-Item -LiteralPath $part -Force
    throw "Downloaded $got bytes but $file should be $size bytes. Partial file removed."
}
if ($hash) {
    $actual = Get-Sha256 $part
    if ($actual -ne $hash) {
        Remove-Item -LiteralPath $part -Force
        throw "SHA-256 of $file is $actual, expected $hash. File removed."
    }
}
Move-Item -LiteralPath $part -Destination $target -Force
Write-Host "Done: $target ($got bytes$(if ($hash) { ', size and SHA-256 verified' } else { ', size verified; no SHA-256 available' }))."
