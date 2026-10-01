# One line on how a training run is doing, from the status file the trainer writes every ten iterations.
#   .\tools\status.ps1 bag
param([string]$Run = "bag", [switch]$Terms)
$path = Join-Path $PSScriptRoot "..\logs\$Run.status.json"
if (-not (Test-Path $path)) { "no status yet for run '$Run'"; return }
$s = Get-Content $path -Raw | ConvertFrom-Json
$e = $s.env
"{0} [{1}] iter {2}, {3:N1} min of {4:N0}, {5:N0} steps/s, updated {6}" -f $s.run, $s.mode, $s.iter, ($s.hours * 60), ($s.max_hours * 60), $s.fps, $s.updated
"  stays up {0:N1} s of an episode, fall rate {1:N2}, upright {2:N2}, distance to target {3:N2} m" -f $e.ep_len_s, $e.fall_rate, $e.upright, $e.distance
"  hits {0:N2}/s per fighter ({1:P0} to the head), glove arrives at {2:N1} m/s, hardest {3:N1}; knockdowns {4:N2}/min" -f $e.hits_per_s, $e.head_share, $e.hit_speed, $e.hit_speed_max, $e.knockdowns_per_min
"  return {0:N1}, glove speed {1:N1} m/s, power {2:N0} W, actions on the clip {3:P0}, noise {4:N2}, lr {5:E1}" -f $e.ep_return, $e.glove_speed, $e.power, $e.act_sat, $s.ppo.action_std, $s.ppo.lr
if ($Terms) { "  reward per step: " + (($e.PSObject.Properties | Where-Object { $_.Name -like 'rt_*' } | ForEach-Object { "{0} {1:N3}" -f $_.Name.Substring(3), $_.Value }) -join ', ') }
