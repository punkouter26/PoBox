param(
    [Parameter(Mandatory = $true)][ValidateSet('grandma', 'grandpa', 'nick')][string]$Creature,
    [int]$Iterations = 2000,
    [int]$Worlds = 256,
    [int]$TensorboardPort = 6007,
    [string]$WarmStart = 'Assets/Agents/Nick_Balance002/nick_balance_002.onnx'
)
# Train one creature's bounded ("human") body in MuJoCo Warp, at the contest's
# 0.02 s step, with the crash-recovery loop and the viewer, per AGENTS.md.
# Mirrors start_nick_human.ps1; the model is Tools/MuJoCo/<creature>_human.xml,
# which prepare_human_limits.py writes from the Unity export.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location -LiteralPath $projectRoot
$editor = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'Unity.exe' -and $_.CommandLine -like "*$projectRoot*" -and
    $_.CommandLine -notlike '*AssetImportWorker*'
}
if ($editor) { throw 'Save and close this Unity project before starting the long training run.' }
$model = "Tools/MuJoCo/$($Creature)_human.xml"
if (-not (Test-Path $model)) { throw "Missing $model -- export the body from Unity and run prepare_human_limits.py first." }
$python = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
$env:NICK_TIMESTEP = '0.02'
$env:NICK_DECIMATION = '1'
$runName = "$($Creature)_human01"
& $python -u (Join-Path $PSScriptRoot 'train_nick_loop.py') `
    --run-name $runName --num-envs $Worlds --until-iteration $Iterations `
    --resume-latest --warm-start-onnx $WarmStart `
    --save-interval 25 --tensorboard-port $TensorboardPort `
    --env model_path=$model --env episode_seconds=30 `
    --env stand_still_fraction=0.5 --env speed_range=0.5,1.0 `
    --env push_scale_when_moving=0 --env push_force_range=50,150 `
    --env exact_start_fraction=0.5 --env foot_clearance_target=0.06 `
    --env w_overlift=0.2 --env overlift_target=0.15 `
    --train init_noise_std=0.3 --train learning_rate=0.0002 --train gamma=0.995
exit $LASTEXITCODE
