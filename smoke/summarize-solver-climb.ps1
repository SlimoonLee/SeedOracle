param(
    [string]$RuntimeDir = (Join-Path (Join-Path $PSScriptRoot '..') '.smoke\planning-audit')
)
$ErrorActionPreference = 'Stop'

$reports = Get-ChildItem -LiteralPath $RuntimeDir -Directory -Filter 'climb-*' |
    ForEach-Object { Join-Path $_.FullName 'climb.json' } |
    Where-Object { Test-Path -LiteralPath $_ }
if (-not $reports) { throw "No climb.json found under $RuntimeDir" }

$runs = foreach ($report in $reports) {
    $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    [pscustomobject]@{ Path = $report; Data = $json }
}

foreach ($run in $runs | Sort-Object { $_.Data.Mode }, { $_.Data.Seed }) {
    $data = $run.Data
    $total = ($data.Fights | Measure-Object -Property Loss -Sum).Sum
    $deaths = @($data.Fights | Where-Object { $_.Death }).Count
    Write-Output ("== seed {0} mode {1} character {2} fights {3} totalLoss {4} deaths {5} outcome {6} runSeconds {7}" -f `
        $data.Seed, $data.Mode, $data.Character, $data.Fights.Count, $total, $deaths, $data.Outcome, $data.RunSeconds)
    foreach ($fight in $data.Fights) {
        Write-Output ("   floor {0,2} coord {1,-5} {2,-7} {3,-28} deck {4,2} hp {5,3}->{6,-3} loss {7,3} exit {8,-3} turn {9} conf {10}" -f `
            $fight.Floor, $fight.Coord, $fight.RoomType, $fight.EncounterId,
            $fight.DeckSize, $fight.HpEntry, $fight.MaxHp,
            $(if ($null -ne $fight.Loss) { $fight.Loss } else { '-' }),
            $(if ($null -ne $fight.HpExit) { $fight.HpExit } else { '-' }),
            $(if ($null -ne $fight.EndTurn) { $fight.EndTurn } else { '-' }),
            $(if ($fight.Confidence) { $fight.Confidence } else { '-' }))
    }
}

Write-Output ''
Write-Output '== aggregate by mode and room type (forecasted losses only) =='
foreach ($mode in $runs.Data.Mode | Sort-Object -Unique) {
    $rows = $runs | Where-Object { $_.Data.Mode -eq $mode } | ForEach-Object { $_.Data.Fights } |
        Where-Object { $null -ne $_.Loss }
    Write-Output ("-- mode {0}: runs {1} fights {2} mean loss {3:N1}" -f `
        $mode, (@($runs | Where-Object { $_.Data.Mode -eq $mode }).Count), $rows.Count,
        (($rows | Measure-Object -Property Loss -Average).Average))
    $rows | Group-Object RoomType | Sort-Object Name | ForEach-Object {
        $stat = $_.Group | Measure-Object -Property Loss -Average -Minimum -Maximum -Sum
        Write-Output ("   {0,-8} n={1,2}  mean {2,6:N1}  min {3,3}  max {4,3}  sum {5,4}" -f `
            $_.Name, $stat.Count, $stat.Average, $stat.Minimum, $stat.Maximum, $stat.Sum)
    }
    $depth = $runs | Where-Object { $_.Data.Mode -eq $mode } | ForEach-Object {
        $f = $_.Data.Fights
        if ($f.Count -eq 0) { 0 } else { ($f | Select-Object -Last 1).Floor }
    }
    Write-Output ("   depth (last floor reached): {0}" -f (($depth | Sort-Object) -join ','))
    $seconds = $runs | Where-Object { $_.Data.Mode -eq $mode } | ForEach-Object { $_.Data.RunSeconds }
    if ($seconds.Count -gt 0) {
        $stat = $seconds | Measure-Object -Average -Minimum -Maximum
        Write-Output ("   runSeconds: mean {0:N0} min {1:N0} max {2:N0}" -f $stat.Average, $stat.Minimum, $stat.Maximum)
    }
}
