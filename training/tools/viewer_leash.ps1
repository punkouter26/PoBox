# Keeps MuJoCo's viewer (view_box.py) from taking the trainer's CPU and GPU while a league runs.
#
#   pwsh -File tools\viewer_leash.ps1            runs until logs\league.done appears
#
# The house rule wants the viewer on screen, so it is not closed. Three things, all measured 2026-10-02
# against a ring session doing 53,000 steps a second with no viewer:
#   * left alone it spreads over five of this laptop's six cores (machine at 96%): every viewer is put at
#     idle priority on two logical cores, which is plenty to show one fight in real time;
#   * drawing on the RTX 2060 beside the trainer cost 10 to 15% (45,000 to 48,000). Python's windows are
#     therefore sent to the integrated Intel GPU (Windows' per-app graphics preference, set once below;
#     CUDA is not affected by it). With that, 52,000 to 53,000 with the viewer open;
#   * NEVER minimise the viewer: a minimised window is not held to the screen's refresh rate, draws as
#     fast as it can, and took the trainer to 36,000.
# It also keeps one viewer: when a newer one opens (the next stage's), older ones are closed.
param([int]$EverySeconds = 20, [long]$Affinity = 0xC00)
$tr = Split-Path $PSScriptRoot -Parent
$py = "$env:LOCALAPPDATA\Programs\Python\Python310\python.exe"
$key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
if (Test-Path $py) {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    # 1 = power saving (the integrated GPU). To undo: delete this value, or Settings > Display > Graphics.
    Set-ItemProperty -Path $key -Name $py -Value 'GpuPreference=1;'
}
while (-not (Test-Path "$tr\logs\league.done")) {
    $viewers = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
        Where-Object { $_.CommandLine -match 'view_box\.py' -and $_.CommandLine -notmatch '--sheet' })
    # A viewer is two processes (the venv's launcher and the interpreter it starts). The newest pair stays.
    $windows = @($viewers | Where-Object { (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).MainWindowHandle -ne 0 } | Sort-Object CreationDate)
    if ($windows.Count -gt 1) {
        foreach ($old in $windows[0..($windows.Count - 2)]) {
            Stop-Process -Id $old.ProcessId -Force -ErrorAction SilentlyContinue
            Stop-Process -Id $old.ParentProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($v in $viewers) {
        try {
            $p = Get-Process -Id $v.ProcessId -ErrorAction Stop
            if ($p.PriorityClass -ne 'Idle') { $p.PriorityClass = 'Idle' }
            if ([long]$p.ProcessorAffinity -ne $Affinity) { $p.ProcessorAffinity = [IntPtr]$Affinity }
        } catch { }
    }
    Start-Sleep -Seconds $EverySeconds
}
