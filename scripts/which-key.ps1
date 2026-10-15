# Shows which modifier keys Windows actually receives while you hold them.
# Use it to pick a hotkey that reaches this PC, e.g. over Remote Desktop from a Mac.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\which-key.ps1
param([int]$Seconds = 10)

Add-Type -Namespace KeyProbe -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
'@

$names = [ordered]@{
    0xA2 = 'LeftCtrl'; 0xA3 = 'RightCtrl'
    0xA4 = 'LeftAlt';  0xA5 = 'RightAlt'
    0x5B = 'LeftWin';  0x5C = 'RightWin'
    0x14 = 'CapsLock'; 0x7C = 'F13'; 0x7D = 'F14'; 0x7E = 'F15'
}

Write-Host "Hold the key you want to use as the hotkey. Watching for $Seconds seconds..."
$end = (Get-Date).AddSeconds($Seconds)
$last = ''
while ((Get-Date) -lt $end) {
    $down = @()
    foreach ($vk in $names.Keys) {
        if (([KeyProbe.Native]::GetAsyncKeyState($vk) -band 0x8000) -ne 0) { $down += $names[$vk] }
    }
    $now = ($down -join ', ')
    if ($now -ne $last) {
        if ($now) { Write-Host ("down: " + $now) } else { Write-Host "released" }
        $last = $now
    }
    Start-Sleep -Milliseconds 100
}
Write-Host "Done. Put the name shown after 'down:' into settings.json as `"hotkey`"."
