# A picture of TensorBoard's own charts, taken with a headless browser.
#
#   tools\tb_shot.ps1 -Tags 'env/fall_rate$' -Out ..\DOCS\reports\img\fall_rate.png
#
# House rule: after a long run, the three charts that matter most are screenshotted from TensorBoard and
# explained. This uses the classic SCALARS page, which draws with SVG; the newer TIME SERIES page draws
# with WebGL and comes out blank in a headless capture. -Tags is a regular expression over tag names.
param(
    [Parameter(Mandatory = $true)][string]$Tags,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Runs = "",
    [int]$Port = 6006,
    [int]$Width = 1500,
    [int]$Height = 470,
    [double]$Scale = 2,
    [double]$Smoothing = 0.6
)
$browser = @("C:\Program Files\Google\Chrome\Application\chrome.exe",
             "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
             "C:\Program Files\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "No Chrome or Edge found." }
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$profileDir = Join-Path $env:TEMP "pobox-tb-shot"
$url = "http://localhost:$Port/?darkMode=false#scalars&tagFilter=$([uri]::EscapeDataString($Tags))&_smoothingWeight=$Smoothing"
if ($Runs) { $url += "&regexInput=$([uri]::EscapeDataString($Runs))" }
$a = @("--headless=new", "--hide-scrollbars", "--no-first-run", "--user-data-dir=`"$profileDir`"",
       "--window-size=$Width,$Height", "--force-device-scale-factor=$Scale", "--virtual-time-budget=45000",
       "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--screenshot=`"$Out`"", "`"$url`"")
$before = if (Test-Path $Out) { (Get-Item $Out).LastWriteTime } else { [datetime]::MinValue }
$p = Start-Process -FilePath $browser -ArgumentList $a -PassThru
$null = $p.Handle
if (-not $p.WaitForExit(120000)) { & taskkill /PID $p.Id /T /F | Out-Null; throw "The browser did not finish." }
if (-not (Test-Path $Out) -or (Get-Item $Out).LastWriteTime -le $before) { throw "No screenshot was written." }
"wrote $Out ($([int]((Get-Item $Out).Length / 1024)) kB)"
