<#
.SYNOPSIS
    Re-export the arena and the ring from ArtSource/BoxingRing.blend.

.DESCRIPTION
    Finds Blender, then runs Tools/blender/export_art.py against the one .blend
    that is the source for both Assets/Art/BoxingRing.glb and
    Assets/Art/Arena.glb. See Tools/blender/README.md for what is in that file
    and what must NOT be modelled into it (the ropes are simulated at runtime).

    Blender is resolved rather than named, the same way this project's other
    tooling resolves the Unity editor: a hardcoded path is a tool that works on
    exactly one machine.

.PARAMETER Blender
    Path to blender.exe. Only needed when it is somewhere this cannot find it.

.EXAMPLE
    pwsh -File Tools/export_art.ps1
#>
[CmdletBinding()]
param(
    [string]$Blender
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$blend = Join-Path $repoRoot 'ArtSource/BoxingRing.blend'
$script = Join-Path $PSScriptRoot 'blender/export_art.py'

if (-not (Test-Path $blend)) {
    throw "No .blend at $blend — is this the PoBox repository?"
}
if (-not (Test-Path $script)) {
    throw "No export script at $script."
}

function Resolve-Blender {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path $Explicit) { return $Explicit }
        throw "No blender.exe at $Explicit."
    }

    $onPath = Get-Command blender -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Newest first, so a machine with several versions uses the current one.
    $roots = @(
        "$env:ProgramFiles\Blender Foundation",
        "${env:ProgramFiles(x86)}\Blender Foundation",
        "$env:LOCALAPPDATA\Programs\Blender Foundation"
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $found = Get-ChildItem -Path $root -Filter 'blender.exe' -Recurse -ErrorAction SilentlyContinue |
                 Sort-Object FullName -Descending |
                 Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    throw @'
Blender not found. Install it and run this again:

    winget install BlenderFoundation.Blender

or pass the path: pwsh -File Tools/export_art.ps1 -Blender "C:\path\to\blender.exe"
'@
}

$exe = Resolve-Blender -Explicit $Blender
Write-Host "Blender: $exe"
Write-Host "Source:  $blend"

& $exe --background $blend --python $script
$code = $LASTEXITCODE

# The Python script prints "EXPORT RESULT: ok" as its last word on the subject.
# Blender itself exits 0 for a script that failed, so the exit code alone is not
# the outcome -- the same reason this project greps its Android builds for
# "AAB BUILD RESULT:" rather than trusting the process.
if ($code -ne 0) {
    throw "Blender exited $code."
}
Write-Host ''
Write-Host 'Done. Check the EXPORT RESULT line above, then let Unity re-import the .glb files.'
