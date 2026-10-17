# The follow-up to the match: a fixed length of extra training from the match's final policies, with the
# house rules a training start has to keep.
#
#   tools\followup.ps1                      60 minutes, run name match2
#   tools\followup.ps1 -Hours 0.5 -Run match3 -From match -TakenW 0.5
#
# What it does, in order:
#   1. TensorBoard: moves finished stages nobody compares against (the bag runs) out of logs\tb into
#      logs\tb_archive, and starts TensorBoard on port 6006 if it is not already up.
#   2. Starts train_box.py detached, so it outlives the terminal; its output goes to logs\<run>.log.
#   3. Waits for the first checkpoint and opens MuJoCo's viewer on the run.
#
# The reward change is the one training\README.md explains: a knockdown no longer costs the fighter who
# lands it the rest of the episode. -TakenW is what being hit costs, as a share of what the same hit pays
# the one who lands it. Leave it at 0.5: at 1.0 (match2, 2026-10-01) both fighters stopped punching.
param(
    [double]$Hours = 1.0,
    [string]$Run = "match2",
    [string]$From = "match",
    [string]$A = "matt",
    [string]$B = "zombie",
    [double]$TakenW = 0.5,
    [double]$KoBonus = 10,
    [switch]$NoViewer
)
$ErrorActionPreference = "Stop"
$tr = Split-Path $PSScriptRoot -Parent
$py = Join-Path (Split-Path (Split-Path $tr -Parent) -Parent) "PoDecath\training\.venv\Scripts\python.exe"
if (-not (Test-Path $py)) { throw "Python not found at $py" }

New-Item -ItemType Directory -Force "$tr\logs\tb_archive" | Out-Null
Get-ChildItem "$tr\logs\tb" -Directory -Filter "bag_*" | ForEach-Object {
    Move-Item $_.FullName "$tr\logs\tb_archive\$($_.Name)" -Force
    "tensorboard: archived finished stage $($_.Name)"
}
if (-not (Test-NetConnection -ComputerName localhost -Port 6006 -InformationLevel Quiet -WarningAction SilentlyContinue)) {
    Start-Process -FilePath $py -ArgumentList @("-m", "tensorboard.main", "--logdir", "$tr\logs\tb", "--port", "6006", "--bind_all") -WindowStyle Hidden
    "tensorboard: started on http://localhost:6006"
} else { "tensorboard: already up on http://localhost:6006" }

$trainArgs = @("train_box.py", "--xml", "models\${A}_vs_${B}_spar.xml", "--run-name", $Run, "--max-hours", "$Hours", "--num-envs", "2048",
               "--resume", "checkpoints\$From\latest_$A.pt", "checkpoints\$From\latest_$B.pt", "--reset-std", "0.3",
               "--keep-old-runs", "--no-tensorboard", "--taken-w", "$TakenW", "--ko-bonus", "$KoBonus", "--survivor-bootstrap")
$p = Start-Process -FilePath $py -ArgumentList $trainArgs -WorkingDirectory $tr -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$tr\logs\$Run.log" -RedirectStandardError "$tr\logs\$Run.err.log"
$stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
Add-Content "$tr\logs\match_stdout.log" "$stamp  start  $($trainArgs -join ' ')"
"training: $Run started at $stamp (pid $($p.Id)), $Hours h; log $tr\logs\$Run.log"

if (-not $NoViewer) {
    $deadline = (Get-Date).AddMinutes(8)
    while (-not (Test-Path "$tr\checkpoints\$Run\latest_$A.pt") -and (Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Seconds 5 }
    if (Test-Path "$tr\checkpoints\$Run\latest_$A.pt") {
        $v = Start-Process -FilePath $py -ArgumentList @("view_box.py", "--run", $Run) -WorkingDirectory $tr -PassThru
        Add-Content "$tr\logs\match_stdout.log" "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  viewer on $Run"
        "viewer: MuJoCo viewer open on $Run (pid $($v.Id))"
    } else { "viewer: no checkpoint yet; open it later with  view_box.py --run $Run" }
}
