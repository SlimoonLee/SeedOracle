param(
    [Parameter(Mandatory)][string]$Sts2Dir,
    [Parameter(Mandatory)][string]$AccountDataRoot,
    [string]$RitsuLibDir,
    [string]$RandomForeseerDir,
    [string]$CombatSolverDir,
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 120,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Seed = 'CAMPFIRE_RNG_A01')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime = Join-Path $repository '.smoke\planning-audit'
$game = Join-Path $runtime 'game'
$audit = Join-Path $runtime "rest-rng-$Seed"

# The existing driver owns isolated runtime/profile setup and the bounded
# AutoSlay prefix. Neither process is launched in the installed game directory.
& (Join-Path $PSScriptRoot 'run-planning-smoke.ps1') @PSBoundParameters -RestRngAudit
$branches = Get-Content -LiteralPath (Join-Path $audit 'branches.json') -Raw | ConvertFrom-Json -AsHashtable

function Json-Value($Value) { ConvertTo-Json -InputObject $Value -Depth 50 -Compress }
function Changed-Streams($Before, $After) {
    @($Before.Run.Keys | Where-Object { (Json-Value $Before.Run[$_]) -ne (Json-Value $After.Run[$_]) })
}

$openings = @{}
foreach ($branch in $branches.Branches) {
    $output = Join-Path $audit $branch.Choice
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $openingPath = Join-Path $output 'opening.json'
    if (Test-Path -LiteralPath $openingPath) { Remove-Item -LiteralPath $openingPath -Force }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $game 'SlayTheSpire2.exe'))
    $start.WorkingDirectory = $game
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('--headless', '--force-steam=off', '--seed-oracle-smoke',
        '--seed-oracle-rest-rng-audit', '--rest-rng-snapshot', $branch.Snapshot,
        '--rest-rng-output', $output, '--log-file', (Join-Path $output 'godot.log'))) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['APPDATA'] = Join-Path $runtime 'Roaming'
    $start.Environment['LOCALAPPDATA'] = Join-Path $runtime 'Local'
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "Native $($branch.Choice) boss opening exceeded $TimeoutSeconds seconds. Inspect $output"
        }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $openingPath)) {
            throw "Native $($branch.Choice) boss opening failed (exit $($process.ExitCode)). Inspect $output"
        }
        $opening = Get-Content -LiteralPath $openingPath -Raw | ConvertFrom-Json -AsHashtable
        if ((Json-Value $branch.Rng) -ne (Json-Value $opening.Before.Rng)) {
            throw "Loading $($branch.Choice) changed RNG before entering the boss."
        }
        $openings[$branch.Choice] = $opening
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

$heal = $branches.Branches | Where-Object Choice -EQ 'HEAL'
$smith = $branches.Branches | Where-Object Choice -EQ 'SMITH'
if ($heal.Hp -le $branches.BeforeHp) { throw 'The HEAL fixture did not restore any HP.' }
$upgraded = @($branches.SmithSlots | Where-Object {
    $smith.Deck[$_].CurrentUpgradeLevel -eq $heal.Deck[$_].CurrentUpgradeLevel + 1
})
if ($upgraded.Count -ne $branches.SmithSlots.Count) { throw 'SMITH did not upgrade the scripted deck slots.' }
$summary = [ordered]@{
    Seed = $Seed; Character = $branches.Character; Encounter = $branches.Encounter
    CampFloor = $branches.ActFloor; BossFloor = $openings.HEAL.ActFloor
    BeforeHp = $branches.BeforeHp; HealHp = $heal.Hp; SmithHp = $smith.Hp
    HealChangedRunStreams = @(Changed-Streams $branches.BeforeRng $heal.Rng)
    SmithChangedRunStreams = @(Changed-Streams $branches.BeforeRng $smith.Rng)
    AfterRestAllRngEqual = (Json-Value $heal.Rng) -eq (Json-Value $smith.Rng)
    BossOpeningAllRngEqual = (Json-Value $openings.HEAL.Rng) -eq (Json-Value $openings.SMITH.Rng)
    BossOpeningChangedRunStreams = @(Changed-Streams $openings.HEAL.Rng $openings.SMITH.Rng)
    HandSlotsEqual = (Json-Value @($openings.HEAL.Hand.Slot)) -eq (Json-Value @($openings.SMITH.Hand.Slot))
    DrawSlotsEqual = (Json-Value @($openings.HEAL.Draw.Slot)) -eq (Json-Value @($openings.SMITH.Draw.Slot))
    EnemiesIncludingHpMoveAndRngEqual = (Json-Value $openings.HEAL.Enemies) -eq (Json-Value $openings.SMITH.Enemies)
    SnapshotRestorationPreservedRng = $true; SourceRunUnchanged = $branches.SourceRunUnchanged
}
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $audit 'comparison.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 15
