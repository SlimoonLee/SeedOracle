#!/bin/bash
# Seed Oracle unattended smoke driver.
# Sandboxes the real save data (backs up, hides the in-progress run, restores
# afterwards) so AutoSlay never touches the player's actual run.
set -u

GAME="/d/SteamLibrary/steamapps/common/Slay the Spire 2"
DATA="/c/Users/slimoon/AppData/Roaming/SlayTheSpire2"
BAK="/d/FORSTS2/.smoke/backup"
SEED="${1:-SEEDORACLE}"

SAVES="$DATA/steam/76561198444364716"
MOD_SAVES="$SAVES/modded/profile1/saves"
VAN_SAVES="$SAVES/profile1/saves"

# 0) recover from a previously interrupted smoke: restore first
if [ -d "$BAK/steam" ]; then
  echo "[driver] restoring leftover backup from an interrupted run"
  cp -rf "$BAK/steam/." "$DATA/steam/"
  cp -rf "$BAK/default/." "$DATA/default/" 2>/dev/null
  rm -rf "$BAK/steam" "$BAK/default"
fi

# 1) back up
mkdir -p "$BAK"
rm -rf "$BAK/steam" "$BAK/default"
cp -r "$DATA/steam" "$BAK/steam"
cp -r "$DATA/default" "$BAK/default" 2>/dev/null
echo "[driver] backup complete"

# 2) hide the in-progress run (sandbox only)
rm -f "$MOD_SAVES/current_run.save" "$MOD_SAVES/current_run.save.backup"
rm -f "$VAN_SAVES/current_run.save" "$VAN_SAVES/current_run.save.backup"
echo "[driver] current_run.save hidden"

# 3) launch headless smoke and wait for exit
cd "$GAME" || exit 1
rm -f seed_oracle_autoslay.log seed_oracle_smoke_report.txt
( ./SlayTheSpire2.exe --headless --seed-oracle-smoke --seed "$SEED" > /dev/null 2>&1 &
GAMEPID=$!;
for i in $(seq 1 120); do
  sleep 5;
  kill -0 $GAMEPID 2>/dev/null || break;
done;
kill -9 $GAMEPID 2>/dev/null;
wait $GAMEPID 2>/dev/null )
echo "[driver] game exited with code $?"

# 4) restore the player's real data
cp -rf "$BAK/steam/." "$DATA/steam/"
cp -rf "$BAK/default/." "$DATA/default/" 2>/dev/null
rm -rf "$BAK/steam" "$BAK/default"
echo "[driver] restore complete"

# 5) verdict
if grep -q "Run completed" "$GAME/seed_oracle_autoslay.log" 2>/dev/null; then
  echo "[driver] VERDICT: run completed"
elif grep -q "Run failed" "$GAME/seed_oracle_autoslay.log" 2>/dev/null; then
  echo "[driver] VERDICT: run FAILED"
  grep -E "Run failed|ERROR" "$GAME/seed_oracle_autoslay.log" | tail -3
else
  echo "[driver] VERDICT: inconclusive"
fi
grep -E "\[Report\]|\[Smoke\]" "$DATA/logs/godot.log" 2>/dev/null | tail -10
