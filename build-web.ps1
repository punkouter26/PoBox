# PoBox WebGL build launcher.
#
# Rebuilds WEB/ — the deployment artifact for the Azure Static Web App.
# The output is committed to git (see .github/workflows/azure-static-web-apps.yml)
# and uploaded unchanged by the workflow, so a working local build is a
# prerequisite for a working production build.
#
# Usage:
#   .\build-web.ps1                       # build into .\WEB using Unity Hub
#   .\build-web.ps1 -UnityPath "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe"
#   .\build-web.ps1 -Out Builds\WebGL     # custom output dir
#
# The script defaults to project version 6000.5.6f1 (matches ProjectVersion.txt).
# Pass -UnityPath to point at a different editor install.

param(
    [string]$Out = "WEB",
    [string]$UnityPath = "",
    [string]$ProjectVersion = "6000.5.6f1",
    [int]$TimeoutSeconds = 0   # 0 = no timeout; Unity WebGL builds take a while
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Locate Unity if not given. Order: explicit path, env var, Hub default install.
if (-not $UnityPath) {
    if ($env:UNITY_PATH) {
        $UnityPath = $env:UNITY_PATH
    } else {
        $hubRoot = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$ProjectVersion\Editor\Unity.exe"
        if (Test-Path $hubRoot) {
            $UnityPath = $hubRoot
        }
    }
}
if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
    Write-Error ("Unity not found. Pass -UnityPath or set `$env:UNITY_PATH. " +
                 "Default searched: $hubRoot")
    exit 1
}

# Sanity check: project version pin (the WebGL build needs the same editor
# that wrote the assets, otherwise serialization drift is the smallest of
# our worries).
$pvFile = Join-Path $root "ProjectSettings\ProjectVersion.txt"
if (Test-Path $pvFile) {
    $pv = (Get-Content $pvFile | Select-String -Pattern "^m_EditorVersion:\s*(\S+)" |
           ForEach-Object { $_.Matches[0].Groups[1].Value }) | Select-Object -First 1
    if ($pv -and $pv -ne $ProjectVersion) {
        Write-Warning "ProjectVersion.txt says $pv but -ProjectVersion is $ProjectVersion. Pass the matching -ProjectVersion (or -UnityPath) to avoid editor-upgrade drift."
    }
}

# Resolve output dir absolute for the sanity check below; relative for Unity.
$outAbs = Join-Path $root $Out
if (Test-Path $outAbs) {
    Write-Host "Wiping previous $Out/ (Build/ and StreamingAssets/ only — keeps index.html, staticwebapp.config.json, README.md)."
    foreach ($sub in @("Build", "StreamingAssets", "TemplateData")) {
        $p = Join-Path $outAbs $sub
        if (Test-Path $p) {
            Remove-Item -Recurse -Force $p
        }
    }
}

Write-Host ""
Write-Host "Unity   : $UnityPath"
Write-Host "Project : $root"
Write-Host "Output  : $Out"
Write-Host ""

# BuildPlayer options are written to Editor.log under Library/, not the console,
# so we tee Unity's stdout/stderr to a local log so failures are diagnosable
# without opening the Editor.
$logPath = Join-Path $root "Logs\build-web.log"
$logDir = Split-Path $logPath -Parent
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir | Out-Null
}

# -nographics because there's no display; -quit to exit on completion;
# -batchmode is implied by -nographics but stated for clarity.
$unityArgs = @(
    "-batchmode",
    "-nographics",
    "-quit",
    "-projectPath", $root,
    "-buildTarget", "WebGL",
    "-executeMethod", "PoBox.Editor.Build_WebGL.Build",
    "-buildOutput", $Out,
    "-logFile", $logPath
)

Write-Host "Running Unity WebGL build (logs: $logPath) ..."
$proc = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -NoNewWindow -PassThru -Wait
if ($proc.ExitCode -ne 0) {
    Write-Host ""
    Write-Error "Unity build failed (exit $($proc.ExitCode)). Tail of $logPath :"
    Get-Content $logPath -Tail 60 | Write-Host
    exit $proc.ExitCode
}

Write-Host ""
Write-Host "WebGL build complete -> $Out\"
Write-Host "Deploy with: swa deploy $Out --deployment-token <token> --env production"