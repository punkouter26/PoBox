# PoBox training launcher.
# Rules honored: TensorBoard ALWAYS starts with training; TensorBoard is
# killed BEFORE a --force wipe (Windows file handles silently fail the wipe);
# headless runs pass --env --no-graphics with an explicit --base-port.
# Usage:
#   .\train.ps1                                  # Stage 1, Editor mode (press Play)
#   .\train.ps1 -Resume                          # continue a stopped run
#   .\train.ps1 -Force                           # wipe and restart the run id
#   .\train.ps1 -Env Builds\BoxerEnv\PoBox.exe -NumEnvs 4   # headless build mode
param(
    [string]$Config = "BoxerBalance01.yaml",
    [string]$RunId = "boxer_balance01",
    [string]$Env = "",
    [int]$NumEnvs = 4,
    [int]$BasePort = 5010,
    [switch]$Resume,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$venvScripts = Join-Path $root ".venv\Scripts"
$configPath = Join-Path $root "Config\$Config"

if (-not (Test-Path $configPath)) {
    Write-Error "Config not found: $configPath"
    exit 1
}

# The behavior key must match BehaviorParameters ("Boxer") or the env
# connects and then trains nothing — a classic 3 a.m. discovery.
if (-not (Select-String -Path $configPath -Pattern '^\s{2}Boxer:\s*$' -Quiet)) {
    Write-Error "Config $Config has no 'Boxer:' behavior key. The BehaviorParameters name is 'Boxer'."
    exit 1
}

if ($Force) {
    Write-Host "Killing TensorBoard before --force wipe (Windows file-handle rule)..."
    Get-Process tensorboard -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
}

Write-Host "Starting TensorBoard at http://localhost:6006 ..."
Start-Process -FilePath (Join-Path $venvScripts "tensorboard.exe") `
    -ArgumentList "--logdir", (Join-Path $root "results") `
    -WindowStyle Minimized

$mlagentsArgs = @($configPath, "--run-id=$RunId", "--timeout-wait", "300")
if ($Env -ne "") {
    $envPath = Join-Path $root $Env
    if (-not (Test-Path $envPath)) {
        Write-Error "Env build not found: $envPath"
        exit 1
    }
    $mlagentsArgs += @("--env", $envPath, "--no-graphics", "--num-envs", "$NumEnvs", "--base-port", "$BasePort")
    Write-Host "Headless: $NumEnvs envs, ports $BasePort-$($BasePort + $NumEnvs - 1). num-envs alters batching - recorded in run log."
}
if ($Resume) { $mlagentsArgs += "--resume" }
if ($Force)  { $mlagentsArgs += "--force" }

Write-Host "Run ID : $RunId"
Write-Host "Config : $configPath"
if ($Env -eq "") {
    Write-Host ""
    Write-Host ">>> When 'Listening on port 5004' appears, press Play in the Unity Editor. <<<"
    Write-Host ""
}

& (Join-Path $venvScripts "mlagents-learn.exe") @mlagentsArgs
