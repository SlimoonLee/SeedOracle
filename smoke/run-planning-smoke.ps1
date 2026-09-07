param(
    [Parameter(Mandatory)][string]$Sts2Dir,
    [Parameter(Mandatory)][string]$AccountDataRoot,
    [string]$RitsuLibDir,
    [string]$RandomForeseerDir,
    [string]$CombatSolverDir,
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 120,
    [string]$Seed = 'SEEDORACLE', [switch]$FullRun, [switch]$SelfTest,
    [ValidateSet('normal', 'unknown', 'event')][string]$SimulationCase = 'normal',
    [string]$SimulationEvent = 'DenseVegetation')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime = Join-Path $repository '.smoke\planning-audit'
$game = Join-Path $runtime 'game'
$sourceGame = (Resolve-Path -LiteralPath $Sts2Dir).Path
$account = (Resolve-Path -LiteralPath $AccountDataRoot).Path
$roaming = Join-Path $runtime 'Roaming'
$local = Join-Path $runtime 'Local'

function Resolve-ModSource([string]$Override, [string]$Id, [string]$WorkshopId) {
    $steamApps = Split-Path -Parent (Split-Path -Parent $sourceGame)
    $candidates = if ($Override) { @($Override) } else {
        @((Join-Path $sourceGame "mods\$Id"), (Join-Path $steamApps "workshop\content\2868840\$WorkshopId"))
    }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        foreach ($root in @($resolved, (Split-Path -Parent (Split-Path -Parent $resolved)))) {
            if ((Test-Path -LiteralPath (Join-Path $root "$Id.json")) -and
                (Test-Path -LiteralPath (Join-Path $root "$Id.dll"))) {
                return $root
            }
        }
    }
    throw "Could not locate $Id. Supply its mod directory explicitly."
}

$mods = [ordered]@{
    'STS2-RitsuLib' = Resolve-ModSource $RitsuLibDir 'STS2-RitsuLib' '3747602295'
    'RandomForeseer' = Resolve-ModSource $RandomForeseerDir 'RandomForeseer' '3747531952'
    'CombatSolver' = Resolve-ModSource $CombatSolverDir 'CombatSolver' '3790899961'
}
foreach ($name in @('SlayTheSpire2.exe', 'data_sts2_windows_x86_64\sts2.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceGame $name))) { throw "Missing game file: $name" }
}
foreach ($name in @('settings.save', 'profile.save', 'modded')) {
    if (-not (Test-Path -LiteralPath (Join-Path $account $name))) { throw "Missing profile input: $name" }
}

function Copy-RuntimeTree([string]$Source, [string]$Destination, [bool]$HardLink, [string[]]$ExcludeNames = @()) {
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File
        | Where-Object { $ExcludeNames -notcontains $_.Name }) {
        $target = Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $file.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        if ($HardLink) {
            if (-not (Test-Path -LiteralPath $target)) {
                New-Item -ItemType HardLink -Path $target -Target $file.FullName | Out-Null
            }
        } else {
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
    }
}

New-Item -ItemType Directory -Path $game, $roaming, $local -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $sourceGame -File | Where-Object Extension -In '.exe', '.pck', '.dll') {
    $target = Join-Path $game $file.Name
    if (-not (Test-Path -LiteralPath $target)) {
        New-Item -ItemType HardLink -Path $target -Target $file.FullName | Out-Null
    }
}
Copy-RuntimeTree (Join-Path $sourceGame 'data_sts2_windows_x86_64') (Join-Path $game 'data_sts2_windows_x86_64') $true
foreach ($mod in $mods.GetEnumerator()) {
    $exclude = $mod.Key -eq 'CombatSolver' ? @('SeedOracle.dll', 'SeedOracle.pdb', 'SeedOracle.json') : @()
    Copy-RuntimeTree $mod.Value (Join-Path $game "mods\$($mod.Key)") $false $exclude
}
$staleCombatSolverSeedOracle = Join-Path $game 'mods\CombatSolver'
foreach ($name in @('SeedOracle.dll', 'SeedOracle.pdb', 'SeedOracle.json')) {
    $stale = Join-Path $staleCombatSolverSeedOracle $name
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
}
$profile = Join-Path $roaming 'SlayTheSpire2\default\1'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
foreach ($name in @('settings.save', 'profile.save')) {
    Copy-Item -LiteralPath (Join-Path $account $name) -Destination $profile -Force
}
Copy-RuntimeTree (Join-Path $account 'modded') (Join-Path $profile 'modded') $false
foreach ($save in Get-ChildItem -LiteralPath $profile -Recurse -File -Filter 'current_run.save*') {
    $resolved = [IO.Path]::GetFullPath($save.FullName)
    if (-not $resolved.StartsWith($profile + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Save is outside the isolated profile: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Force
}
$settingsPath = Join-Path $profile 'settings.save'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
$settings.mod_settings = @{ mods_enabled = $true; mod_list = @() }
foreach ($key in @('volume_master', 'volume_bgm', 'volume_sfx', 'volume_ambience')) { $settings[$key] = 0 }
$settings['skip_intro_logo'] = $true
$settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

$modOutput = Join-Path $game 'mods\SeedOracle'
$buildArgs = @('-c', 'Debug', "-p:Sts2Dir=$sourceGame", "-p:ModOutputDir=$modOutput")
foreach ($dependency in @('STS2-RitsuLib', 'RandomForeseer', 'CombatSolver')) {
    $directory = Join-Path $game "mods\$dependency"
    $versioned = Get-ChildItem -LiteralPath (Join-Path $directory 'lib') -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$dependency.dll") } |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($versioned) { $directory = $versioned.FullName }
    $property = $dependency -eq 'STS2-RitsuLib' ? 'RitsuLibDir' : "${dependency}Dir"
    $buildArgs += "-p:${property}=$directory"
}
& dotnet build (Join-Path $repository 'SeedOracle.csproj') @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Smoke build failed.' }
$log = Join-Path $runtime 'godot.log'
$report = Join-Path $game 'seed_oracle_smoke_report.txt'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $game 'SlayTheSpire2.exe'))
$start.WorkingDirectory = $game
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$arguments = @('--headless', '--force-steam=off', '--seed-oracle-smoke')
if (-not $FullRun) { $arguments += '--seed-oracle-planning-audit' }
$arguments += @('--seed', $Seed, '--log-file', $log)
$arguments += @('--simulation-case', $SimulationCase, '--simulation-event', $SimulationEvent)
foreach ($argument in $arguments) {
    $start.ArgumentList.Add($argument)
}
$start.Environment['APPDATA'] = $roaming
$start.Environment['LOCALAPPDATA'] = $local
if ($SelfTest) { $start.Environment['SEED_ORACLE_SELF_TEST'] = '1' }
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "Planning audit timed out. Inspect $log"
    }
    if (-not (Test-Path -LiteralPath $report)) { throw "Audit produced no report. Inspect $log" }
    Select-String -LiteralPath $report -Pattern 'PASS:|SUMMARY:' | ForEach-Object Line
    if ($process.ExitCode -ne 0) { throw "Planning audit exited with code $($process.ExitCode). Inspect $log" }
    if (-not (Select-String -LiteralPath $report -Pattern '^native-combat-reward-groups PASS:')) {
        throw "Native reward group audit did not pass. Inspect $report"
    }
    if (-not (Select-String -LiteralPath $report -Pattern '^planning-combat-simulation PASS:')) {
        throw "Planning combat simulation audit did not pass. Inspect $report"
    }
    if (-not (Select-String -LiteralPath $report -Pattern 'event-audit SUMMARY:.*nondeterministic=0 failed=0$')) {
        throw "Planning audit failed or did not reach its summary. Inspect $report"
    }
    if ($FullRun -and -not (Select-String -LiteralPath $log -Pattern 'AutoSlay.*(Run completed|RunCompleted|Victory!|Abandoning)')) {
        throw "Full smoke run did not report completion. Inspect $log"
    }
    [pscustomobject]@{ Report = $report; Log = $log; ExitCode = $process.ExitCode }
} finally {
    if (-not $process.HasExited) { $process.Kill($true) }
    $process.Dispose()
}
