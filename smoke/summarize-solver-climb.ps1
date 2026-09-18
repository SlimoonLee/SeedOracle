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

foreach ($run in $runs | Sort-Object { $_.Data.Seed }) {
    $data = $run.Data
    $total = ($data.Fights | Measure-Object -Property Loss -Sum).Sum
    $bossLosses = @($data.Fights | Where-Object { $_.RoomType -eq 'Boss' } | ForEach-Object { $_.Loss }) -join ','
    $deaths = @($data.Fights | Where-Object { $_.Death }).Count
    Write-Output ("== seed {0} character {1} fights {2} totalLoss {3} bossLoss {4} deaths {5} final {6}" -f `
        $data.Seed, $data.Character, $data.Fights.Count, $total, $bossLosses, $deaths, $data.Final)
    foreach ($fight in $data.Fights) {
        Write-Output ("   floor {0,2} coord {1,-5} {2,-7} {3,-28} deck {4,2} loss {5,3} hp {6}/{7} turn {8} conf {9}" -f `
            $fight.Floor, $fight.Coord, $fight.RoomType, $fight.EncounterId,
            $fight.DeckSize,
            $(if ($null -ne $fight.Loss) { $fight.Loss } else { '-' }),
            $fight.HpEntry, $fight.MaxHp,
            $(if ($null -ne $fight.EndTurn) { $fight.EndTurn } else { '-' }),
            $(if ($fight.Confidence) { $fight.Confidence } else { '-' }))
    }
}

Write-Output ''
Write-Output '== aggregate by room type (loss; entry HP is always full except the first fight) =='
$all = $runs | ForEach-Object { $_.Data.Fights } | Where-Object { $null -ne $_.Loss }
$all | Group-Object RoomType | Sort-Object Name | ForEach-Object {
    $losses = $_.Group | ForEach-Object Loss
    $stat = $losses | Measure-Object -Average -Minimum -Maximum -Sum
    Write-Output ("{0,-8} n={1,2}  mean {2,6:N1}  min {3,3}  max {4,3}  sum {5,4}" -f `
        $_.Name, $stat.Count, $stat.Average, $stat.Minimum, $stat.Maximum, $stat.Sum)
}
$encounters = $all | Group-Object EncounterId | Sort-Object { ($_.Group | Measure-Object Loss -Average).Average } -Descending
Write-Output ''
Write-Output '== worst encounters (mean loss, n>=1) =='
foreach ($group in $encounters | Select-Object -First 12) {
    $stat = ($group.Group | Measure-Object Loss -Average -Maximum)
    Write-Output ("{0,-32} n={1,2}  mean {2,6:N1}  max {3,3}" -f `
        $group.Name, $group.Count, $stat.Average, $stat.Maximum)
}
