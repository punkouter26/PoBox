# Benchmark training checkpoints against the brains that currently ship.
#
# A run writes its checkpoints to results/<run-id>/Boxer/Boxer-<step>.onnx, but
# Build_EvalEnv can only stage brains that live under Assets/Agents -- a player
# cannot import a .onnx at runtime, so they have to be in the project at BUILD
# time. This copies the checkpoints in under a gitignored folder, rebuilds the
# evaluation player, runs the matrix, and cleans up.
#
#   pwsh -Command "& ./Tools/eval_candidates.ps1 -Runs @('boxer_locomotion21','boxer_locomotion23')"
#   pwsh -Command "& ./Tools/eval_candidates.ps1 -Runs @('boxer_locomotion22') -Speeds 0,1 -Episodes 4"
#
# Use -Command, NOT -File, for the array parameters. Under -File every argument
# arrives as a plain string, so "-Runs a,b" binds the whole thing to Runs[0] as
# one run named "a,b" -- which then merely warns that it has no checkpoints and
# silently measures the baselines only.
#
# Safe to run WHILE training continues: it builds to EvalBuild, which nothing
# else holds open, and only reads from results/.

[CmdletBinding()]
param(
    [string[]] $Runs = @(),
    # Commanded speeds to benchmark at. 0 is the balance ring, 1 the walk race.
    [double[]] $Speeds = @(0),
    [int] $Episodes = 3,
    # Brains already under Assets/Agents to measure alongside, plus the
    # code-driven PD bot, which is the floor a policy has to clear.
    [string[]] $Baselines = @('heuristic', 'Locomotion_gen20'),
    # Turn the per-agent shover on. The balance ring runs hazards and shoves
    # while SCN_TRAIN_LOCOMOTION disables them, so the default measures
    # UNDISTURBED standing -- not the question the contest asks.
    [switch] $Shove,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$unity = 'C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe'
$staging = Join-Path $root 'Assets\Agents\_Candidates'
$evalDir = Join-Path $root 'eval'
New-Item -ItemType Directory -Force $evalDir | Out-Null

# ---------------------------------------------------------------- stage
$candidates = @()
if (-not $SkipBuild) {
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force $staging | Out-Null
}
foreach ($run in $Runs) {
    $checkpointDir = Join-Path $root "results\$run\Boxer"
    if (-not (Test-Path $checkpointDir)) {
        Write-Warning "no checkpoints for $run at $checkpointDir"
        continue
    }
    # Highest step number, which is the newest checkpoint the run has exported.
    $latest = Get-ChildItem "$checkpointDir\*.onnx" -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.BaseName -replace '\D', '0') } | Select-Object -Last 1
    if ($null -eq $latest) {
        Write-Warning "no .onnx in $checkpointDir yet"
        continue
    }
    $step = [int]($latest.BaseName -replace '\D', '0')
    # Name carries the step so a report is self-describing months later.
    $name = "{0}_{1}" -f ($run -replace 'boxer_', ''), $step
    $candidates += $name
    if (-not $SkipBuild) {
        Copy-Item $latest.FullName (Join-Path $staging "$name.onnx")
        Write-Host "staged $name  (from $($latest.Name))"
    }
}

# ---------------------------------------------------------------- build
if (-not $SkipBuild) {
    $log = Join-Path $root 'Logs\evalbuild.log'
    Write-Host 'building EvalBuild...'
    $build = Start-Process -FilePath $unity -ArgumentList @(
        '-batchmode', '-quit', '-nographics', '-projectPath', $root,
        '-buildTarget', 'Win64', '-executeMethod', 'PoBox.Editor.Build_EvalEnv.Build',
        '-buildOutput', 'EvalBuild', '-logFile', $log) -Wait -PassThru
    if ($build.ExitCode -ne 0) {
        Select-String -Path $log -Pattern 'error CS' | Select-Object -Last 20 |
            ForEach-Object { $_.Line }
        throw "eval build failed (exit $($build.ExitCode))"
    }
    (Select-String -Path $log -Pattern 'staged \d+ brains' | Select-Object -Last 1).Line
    # The staging folder is a build artifact; Build_EvalEnv has already copied
    # what it needs into the player.
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- measure
$reports = @()
foreach ($speed in $Speeds) {
    foreach ($brain in (@($Baselines) + $candidates)) {
        $suffix = if ($Shove) { 'shove' } else { '' }
        $out = Join-Path $evalDir ("{0}_speed{1}{2}.json" -f $brain, $speed, $suffix)
        $argv = @('-batchmode', '-nographics', '-evalBrain', $brain, '-evalEpisodes', $Episodes,
                  '-evalSpeed', $speed, '-evalOutput', $out,
                  '-logFile', (Join-Path $root 'Logs\eval_run.log'))
        if ($Shove) { $argv += @('-evalShove', '1') }
        $run = Start-Process -FilePath (Join-Path $root 'EvalBuild\PoBoxEval.exe') -ArgumentList $argv -Wait -PassThru
        if ($run.ExitCode -ne 0 -or -not (Test-Path $out)) {
            Write-Warning "$brain @ $speed produced no report (exit $($run.ExitCode))"
            continue
        }
        Write-Host ("measured {0} @ {1}{2}" -f $brain, $speed, $(if ($Shove) { ' +shove' } else { '' }))
        $reports += $out
    }
}

if ($reports.Count -eq 0) { throw 'no reports produced' }
& (Join-Path $root '.venv\Scripts\python.exe') (Join-Path $root 'Tools\eval_compare.py') @reports
