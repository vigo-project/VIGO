# Native AOT milestone check: Release publish, IL warnings vs baseline, startup probe.
# Usage: pwsh -NoProfile -File .claude/skills/aot-check/aot-check.ps1 [-Runs 3] [-SkipPublish] [-UpdateBaseline]
param(
    [int]$Runs = 3,
    [switch]$SkipPublish,
    [switch]$UpdateBaseline
)
$ErrorActionPreference = 'Stop'

$root = (git rev-parse --show-toplevel).Trim()
$out = Join-Path $root 'artifacts/publish/aot-x64'
$exe = Join-Path $out 'Vigo.exe'
$baselineFile = Join-Path $PSScriptRoot 'il-baseline.txt'
$log = Join-Path $root 'artifacts/publish/aot-publish.log'

if (-not $SkipPublish) {
    Write-Host '== dotnet publish (Release, win-x64, Native AOT)'
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    dotnet publish (Join-Path $root 'src/Vigo.App') -c Release -r win-x64 -p:Platform=x64 -o $out *> $log
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) {
        Write-Host "PUBLISH FAILED (exit $publishExit). Errors:"
        Select-String -Path $log -Pattern ': error ' | Select-Object -First 30 | ForEach-Object { $_.Line.Trim() }
        exit 1
    }

    # Normalize "path(line,col): warning IL2104: message [project]" to "IL2104: message".
    $warnings = Select-String -Path $log -Pattern 'warning (IL\d{4}): (.*?)(\s+\[[^\]]+\])?$' |
        ForEach-Object { "$($_.Matches[0].Groups[1].Value): $($_.Matches[0].Groups[2].Value.Trim())" } |
        Sort-Object -Unique

    if ($UpdateBaseline) {
        $warnings | Set-Content -Encoding utf8 $baselineFile
        Write-Host "Baseline updated ($($warnings.Count) warning(s)) -> $baselineFile"
    }

    $baseline = if (Test-Path $baselineFile) { Get-Content $baselineFile | Where-Object { $_ -and -not $_.StartsWith('#') } } else { @() }
    $new = $warnings | Where-Object { $_ -notin $baseline }
    $gone = $baseline | Where-Object { $_ -notin $warnings }

    Write-Host "== IL warnings: $($warnings.Count) total, $(@($new).Count) new, $(@($gone).Count) resolved"
    $new | ForEach-Object { Write-Host "  NEW   $_" }
    $gone | ForEach-Object { Write-Host "  GONE  $_" }
}

if (-not (Test-Path $exe)) { Write-Host "No published exe at $exe"; exit 1 }

$sizeMb = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "== Publish size: $sizeMb MB ($out)"

Write-Host "== Startup probe ($Runs run(s); the first one after a publish is the closest to cold)"
$times = @()
for ($i = 1; $i -le $Runs; $i++) {
    $probe = Join-Path $env:TEMP "vigo-probe-$i.txt"
    Remove-Item $probe -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $exe -ArgumentList '--startup-probe', $probe -PassThru
    if (-not $p.WaitForExit(30000)) { $p.Kill(); Write-Host "  run $i : TIMEOUT (killed after 30 s)"; continue }
    if (Test-Path $probe) {
        $ms = (Get-Content $probe -Raw).Trim()
        $times += [double]$ms
        Write-Host "  run $i : $ms ms (exit $($p.ExitCode))"
    } else {
        Write-Host "  run $i : no probe file (exit $($p.ExitCode)) - check %LOCALAPPDATA%\VIGO\logs"
    }
}
if ($times.Count) { Write-Host ("== First frame: min {0} ms, max {1} ms (target < 1000 ms)" -f ($times | Measure-Object -Minimum).Minimum, ($times | Measure-Object -Maximum).Maximum) }

if (-not $SkipPublish -and @($new).Count -gt 0) { exit 3 }
exit 0
