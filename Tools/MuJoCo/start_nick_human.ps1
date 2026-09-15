param([int]$Iterations = 2000, [int]$Worlds = 256)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location -LiteralPath $projectRoot
$editor = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'Unity.exe' -and $_.CommandLine -like "*$projectRoot*" -and
    $_.CommandLine -notlike '*AssetImportWorker*'
}
if ($editor) { throw 'Save and close this Unity project before starting the long training run.' }
$python = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
$env:NICK_TIMESTEP = '0.02'
$env:NICK_DECIMATION = '1'
& $python -u (Join-Path $PSScriptRoot 'train_nick_loop.py') `
    --run-name nick_human01 --num-envs $Worlds --until-iteration $Iterations `
    --resume-latest --warm-start-onnx Assets/Agents/Nick_Balance002/nick_balance_002.onnx `
    --save-interval 25 --tensorboard-port 6007 `
    --env model_path=Tools/MuJoCo/nick_human.xml --env episode_seconds=30 `
    --env stand_still_fraction=0.5 --env speed_range=0.5,1.0 `
    --env push_scale_when_moving=0 --env push_force_range=50,150 `
    --env exact_start_fraction=0.5 --env foot_clearance_target=0.06 `
    --env w_overlift=0.2 --env overlift_target=0.15 `
    --train init_noise_std=0.3 --train learning_rate=0.0002 --train gamma=0.995
exit $LASTEXITCODE
