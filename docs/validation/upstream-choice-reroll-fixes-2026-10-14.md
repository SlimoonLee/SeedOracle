# Upstream Choices and Rerolls: SeedOracle 0.1.27

Date: 2026-10-14. Native reference: STS2 0.111.0.

## Behavior

| Surface | Result |
| --- | --- |
| Ordinary combat tooltip | Shows Driftwood candidates for ordered subsets of rerollable groups. Initial groups are generated before any rerolls; each branch replays from an independent serialized run. |
| Prayer Wheel / White Star | Retains native independent groups, rarity rules, upgrade state and per-group reroll availability. |
| The Hunt | Adds native `CombatRoom.ExtraRewards` for an explicit assumed trigger count. Ordinary previews consider counts 0 through 3; planning accepts 0 through 100. These are conditional counts, not predictions that fatal kills will happen. |
| Planned card rewards | Records chronological group/step actions, including interleaved rerolls and picks. Changing a choice removes its obsolete suffix; stale card identity/state is rejected. |
| Downstream merchants | Explores supported upstream choices on serialized runs, then calls the native merchant generator. Conditions and uncovered branches accompany the resulting inventory and prices. |
| Events before a merchant | Enumerates unlocked executable options, ordered card targets, reward take/skip and later pages. Finished native event state, including RNG, cards, relics and flags, feeds the downstream merchant. |
| Relic pickup / merchant cards | Uses `RelicCmd.Obtain` and `CardPileCmd.Add`. Relic pickup hooks, random upgrades, gold gains and full purchased card state are retained. A failed/unresolved action blocks the planned continuation. |

Shop rarity is not isolated from rewards: merchant card rarity uses the reward RNG and accumulated rarity odds; the first two merchant relic rarities also use the reward RNG. Relic bag contents, owned relic hooks and purchase effects add further dependencies. A normal card pick need not itself consume RNG.

## Coverage Limits

- Merchant exploration shares a budget of 128 work items, 24 terminal states and elapsed-time checkpoints at 300 ms per map prediction. This is bounded exploration, not an exhaustive search claim. A native operation can take longer than one checkpoint interval.
- Reroll previews compute at most 64 possibilities and display the first 16, ordered by assumed Hunt count and reroll length. Truncation and collapsed results are visible.
- Merchant tooltips display the first 8 distinct stocks. Identical stocks merge conditions, displaying at most 6 condition paths plus the remaining count. Uncovered conditions and budget stops remain visible even if no branch completed.
- Ordinary reroll branches assume no reward was taken before or between rerolls. Planning takes enabled non-card rewards first, then follows the recorded card-action timeline; arbitrary ordering of non-card pickups is not modeled.
- Combat branches assume victory using the modeled postcombat hooks. They do not execute combat or prove damage, escapes, fatal kills, summoned enemies or other changes to reward state. Hunt is an explicit conditional extra-reward count.
- Event exploration permits at most 8 pages and bounded card-target combinations. Minigames, run-ending options, unmodeled UI and event combats without a completed combat reference remain uncovered.
- Earlier shops enumerate no purchase and individual affordable purchases. Other purchase/removal decisions need an explicit plan; restocks remain unmodeled. Rest sites enumerate unchanged decks and individual smith targets. Other actions remain listed as uncovered.
- Multiplayer chest voting is not enumerated. The display does not assign probabilities to the enumerated branches.

## Verification

`RewardChoiceRegressionTests` runs inside the isolated headless game harness, in addition to the existing accuracy and planning suites.

| Check | Evidence |
| --- | --- |
| Native ordered rerolls | 26 ordered subsets across Prayer Wheel, White Star and Prayer Wheel plus one Hunt trigger, including the empty subset. Candidate identity/state, reward RNG continuation and downstream merchant stock/prices are compared with direct native rerolls. |
| Interleaved actions | Three fixtures replay group 2 reroll, group 1 reroll, group 2 pick, group 1 pick. Both deck order and completed groups are checked. |
| Native relic pickup | Old Coin and Whetstone compare complete serialized state against direct native obtain, including gold and Niche RNG/random upgrades. |
| Event to shop | A native Aroma of Chaos option and card target are executed independently; the resulting merchant inventory/prices must be present in the ordinary branch results. |
| Reroll to shop | A native combat reward reroll/skip continuation must produce inventory/prices present in the merchant branches. |
| Presentation / limits | Both group orders must occur in ordinary tooltip text; Hunt assumptions and a forced exploration budget stop must be visible. |
| Isolation | Live run fingerprints and serialized profile progress are unchanged throughout these checks. |

Run command:

```powershell
pwsh -NoProfile -File .\smoke\run-planning-smoke.ps1 `
  -Sts2Dir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2' `
  -AccountDataRoot 'C:\Users\slimoon\AppData\Roaming\SlayTheSpire2\steam\76561198444364716' `
  -SelfTest -Seed CHOICE_BRANCH_20261014 -TimeoutSeconds 240
```

The harness uses a separate game directory and copied profile. Fresh interactive gameplay and visual inspection of every long tooltip remain manual checks, distinct from the headless results. Source, smoke logs and deployment hashes are recorded under `.local/choice-branches-20261014/`.

Final isolated run: PASS. The original accuracy suite compared 12 native inventories; the new suite passed 26 ordered reroll cases and 3 interleaved-pick fixtures. Existing native reward groups, potion chains, multi-page events and 3 combat simulation samples passed. The event audit reported 106 supported executions, 15 explicit degradations, 0 nondeterministic cases and 0 failures. Build completed with 0 warnings and 0 errors.
