# Two new rungs, one after the other, from the match's final policies, with the house rules a training
# start has to keep. Started detached; it outlives the terminal.
#
#   tools\rungs.ps1                               defend (75 min of training), then getup (150 min)
#   tools\rungs.ps1 -Only getup -GetupIters 3000
#
#   defend   the match again, with a consequence for being hit (--daze: head shots weaken the legs, enough
#            of them take the legs away) and a payment for stopping a punch on a glove or forearm. What it
#            is for: fighters that guard, and knockdowns that somebody caused.
#   getup    each fighter learns to stand back up after a knockdown (envs/getup.py).
#
# A rung is given a number of iterations, not a length of time: the Unity editor in Play mode takes four
# fifths of the GPU, and a rung timed by the clock would lose whatever the editor took. -MaxHours is the
# stop if that goes on too long.
#
# House rules, in order: finished runs nobody compares against are moved out of TensorBoard's folder,
# TensorBoard is started if it is not up, and MuJoCo's viewer is opened on each rung once it has a checkpoint.
param(
    [string]$Only = "",
    [string]$From = "match",
    [string]$A = "matt",
    [string]$B = "zombie",
    [int]$DefendIters = 2400,
    # Where the defend rung starts from: the folder and the file stem of its two checkpoints.
    [string]$DefendFrom = "defend_a\model_010500",
    [int]$GetupIters = 5400,
    [double]$DefendMaxHours = 1.9,
    [double]$GetupMaxHours = 3.4,
    [double]$DazeHi = 42,
    [double]$BlockW = 0.05,
    # TensorBoard runs to move out of the way before starting, beside the ones always moved.
    [string[]]$Archive = @(),
    [switch]$NoViewer
)
$ErrorActionPreference = "Stop"
$tr = Split-Path $PSScriptRoot -Parent
$py = Join-Path (Split-Path (Split-Path $tr -Parent) -Parent) "PoDecath\training\.venv\Scripts\python.exe"
if (-not (Test-Path $py)) { throw "Python not found at $py" }
$log = "$tr\logs\rungs.log"
function Say([string]$m) { $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m"; $line; Add-Content $log $line }

New-Item -ItemType Directory -Force "$tr\logs\tb_archive" | Out-Null
foreach ($old in @("match2", "match3", "bag_*", "smoke*") + $Archive) {
    Get-ChildItem "$tr\logs\tb" -Directory -Filter $old -ErrorAction SilentlyContinue | ForEach-Object {
        $dest = "$tr\logs\tb_archive\$($_.Name)"
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Move-Item $_.FullName $dest -Force
        Say "tensorboard: archived $($_.Name) (an experiment that was not kept)"
    }
}
if (-not (Test-NetConnection -ComputerName localhost -Port 6006 -InformationLevel Quiet -WarningAction SilentlyContinue)) {
    Start-Process -FilePath $py -ArgumentList @("-m", "tensorboard.main", "--logdir", "$tr\logs\tb", "--port", "6006", "--bind_all") -WindowStyle Hidden
    Say "tensorboard: started on http://localhost:6006"
} else { Say "tensorboard: already up on http://localhost:6006" }

function Rung([string]$Run, [string]$Stem, [string[]]$Extra, [double]$MaxHours, [int]$Iters) {
    $trainArgs = @("train_box.py", "--xml", "models\${A}_vs_${B}_spar.xml", "--run-name", $Run, "--max-hours", "$MaxHours",
                   "--more-iters", "$Iters", "--num-envs", "2048",
                   "--resume", "checkpoints\${Stem}_$A.pt", "checkpoints\${Stem}_$B.pt",
                   "--keep-old-runs", "--no-tensorboard") + $Extra
    Say "start  $Run  $($trainArgs -join ' ')"
    $p = Start-Process -FilePath $py -ArgumentList $trainArgs -WorkingDirectory $tr -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput "$tr\logs\$Run.log" -RedirectStandardError "$tr\logs\$Run.err.log"
    Set-Content "$tr\logs\$Run.pid" $p.Id
    $viewer = $null
    while (-not $p.HasExited) {
        Start-Sleep -Seconds 10
        if (-not $NoViewer -and $null -eq $viewer -and (Test-Path "$tr\checkpoints\$Run\latest_$A.pt")) {
            $viewer = Start-Process -FilePath $py -ArgumentList @("view_box.py", "--run", $Run) -WorkingDirectory $tr -PassThru
            Say "viewer on $Run (pid $($viewer.Id))"
        }
    }
    Say "end    $Run  exit $($p.ExitCode)"
    # The last viewer is left open: the owner asked to be able to watch after training too. An earlier
    # rung's is closed so that two do not sit on top of each other.
    return $viewer
}

$v = $null
if ($Only -eq "" -or $Only -eq "defend") {
    # No more exploration than the policies arrive with, no entropy bonus to push it up, and a gentle
    # learning rate: this rung refines two fighters that already box (see PPOConfig.max_std).
    $v = Rung "defend" $DefendFrom @("--daze", "--daze-hi", "$DazeHi", "--block-w", "$BlockW", "--ko-bonus", "6", "--survivor-bootstrap",
                                     "--max-std", "0.32", "--entropy-coef", "0", "--lr", "3e-4", "--lr-max", "5e-4") $DefendMaxHours $DefendIters
}
if ($Only -eq "" -or $Only -eq "getup") {
    if ($null -ne $v -and -not $v.HasExited) { Stop-Process -Id $v.Id -Force -ErrorAction SilentlyContinue }
    $v = Rung "getup" "$From\latest" @("--stage", "getup", "--reset-std", "0.5", "--max-std", "0.6") $GetupMaxHours $GetupIters
}
Say "done"
