# Promote a training checkpoint into Assets/Agents as a named, documented brain.
#
#   pwsh -Command "& ./Tools/promote_brain.ps1 -Run boxer_locomotion23 -Name Locomotion_gen23 -Notes notes.txt"
#   pwsh -Command "& ./Tools/promote_brain.ps1 -Run boxer_locomotion23 -Name Locomotion_gen23 -Step 30000000"
#
# Brain folder names in this project have historically LIED about which
# generation they contain, which is why every folder carries a SOURCE.txt and why
# this writes one rather than leaving it to be remembered. The recorded step
# count comes from the checkpoint filename, not from what anyone believed the run
# had reached.

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Run,
    [Parameter(Mandatory)] [string] $Name,
    # Which checkpoint. Defaults to the highest step the run has exported.
    [int] $Step = 0,
    # File whose contents are appended to SOURCE.txt as the rationale.
    [string] $Notes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$checkpointDir = Join-Path $root "results\$Run\Boxer"
if (-not (Test-Path $checkpointDir)) { throw "no checkpoints at $checkpointDir" }

$all = Get-ChildItem "$checkpointDir\*.onnx" |
    Sort-Object { [int]($_.BaseName -replace '\D', '0') }
if ($all.Count -eq 0) { throw "no .onnx under $checkpointDir" }

if ($Step -gt 0) {
    $source = $all | Where-Object { [int]($_.BaseName -replace '\D', '0') -eq $Step }
    if ($null -eq $source) {
        throw "no checkpoint at step $Step. Available: $(($all | ForEach-Object { $_.BaseName }) -join ', ')"
    }
} else {
    $source = $all | Select-Object -Last 1
}
$actualStep = [int]($source.BaseName -replace '\D', '0')

$destDir = Join-Path $root "Assets\Agents\$Name"
New-Item -ItemType Directory -Force $destDir | Out-Null
$destOnnx = Join-Path $destDir "$Name.onnx"
Copy-Item $source.FullName $destOnnx -Force

$sourceText = @"
$Name

From run '$Run', checkpoint $($source.Name) -- step $actualStep.
Promoted $(Get-Date -Format 'yyyy-MM-dd').

Config: Config/$(($Run -replace 'boxer_locomotion', 'BoxerLocomotion'))`.yaml
Observation count 127 (ground-relative heights, locomotion command observed).

"@
if ($Notes -and (Test-Path $Notes)) {
    $sourceText += (Get-Content $Notes -Raw)
}
Set-Content -Path (Join-Path $destDir 'SOURCE.txt') -Value $sourceText -Encoding UTF8

Write-Host "promoted $($source.Name) (step $actualStep) -> Assets/Agents/$Name/$Name.onnx"
Write-Host "NOTE: Unity must import the .onnx before any scene can reference it."
