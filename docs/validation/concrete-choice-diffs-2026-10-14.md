# Concrete Choice Diffs: SeedOracle 0.1.28

Native reference: STS2 0.111.0. Date: 2026-10-14.

## Behavior

- An unfinished current event's initial page is included before future route nodes. Its regenerated option keys and event RNG counter must match the open page. Finished events and unsupported continuations are not treated as fresh choices.
- Current options and their supported card targets execute on independent serialized states. Their completed state feeds merchants, combat rewards and chests.
- A complete reference path is computed for each current option before the larger future-choice search. The reference explicitly skips prior combat cards/relics, keeps native gold and available potion pickups, skips chest relics, buys nothing and does not smith. A prior event must execute a supported completed choice.
- Tooltips show one reference result, followed by the localized action chain and changed slots only. Prices, sale status, upgrades, enchantments, gold, added/removed reward groups and conditional reroll candidates participate in the displayed comparison.
- Identical results merge conditions. Branches with no visible content change and uncomputed warning-only branches are omitted. No generic “choices may change this; recalculate” footer is appended.
- The computation remains bounded (128 work items, 24 terminal states, 600 ms checkpoints shared by a map prediction). The list is a set of computed conditional results, not an exhaustive claim. Numerical collapsed-result counts refer only to results actually calculated.

## Native Example

The isolated fixture `NEOW_DIFF_0` uses the smoke test's captured starting player and a synthetic second-floor merchant target. It is a reproducible native-generation comparison, not a promise that every character or real map with this seed has that merchant.

Reference: Neow → Booming Conch (轰鸣海螺), or Silver Crucible (白银熔炉).

Alternative: Neow → Arcane Scroll (奥术卷轴).

| Changed Slot | Reference | Alternative |
| --- | --- | --- |
| Character card 2 | 击掌, 75 gold | 坟冢爆射, 50 gold |
| Character card 3 | 忧郁, 37 gold, on sale | 吸引仇恨, 25 gold, on sale |

The other merchant slots are unchanged and are not repeated under this branch.

## Verification

`CausalChoiceRegressionTests` initializes Neow through native `BeginEvent`, independently executes each of its three offered options through native `EventOption.Chosen`, and compares complete merchant inventories/prices, elite rewards and treasure contents with the ordinary prediction branches. A separate default-budget fixture carries all three choices through an intervening combat into a later shop. Synthetic presentation checks verify price/sale and upgrade-only differences, unchanged-result suppression and absence of warning-only hints.

The existing accuracy, ordered reroll, interleaved pickup, event-to-shop, potion, event-continuation, event-audit and combat-simulation suites remain required. Live run fingerprints and profile progress must remain unchanged.

The isolated harness does not verify the visual fit of every long tooltip during interactive gameplay.

The final isolated run passed with seed `CHOICE_DIFF_20261014`. The native fixture produced 1 changed merchant inventory, 2 changed elite reward results and 2 changed treasure results across the 3 Neow options. The default-budget Neow → combat → shop check completed in 117 ms. The existing suites passed 12 merchant comparisons, 26 ordered reroll cases, 3 interleaved pickup cases, the potion and event continuation checks, an event audit (106 supported, 15 degraded, 0 nondeterministic, 0 failed), and 3 combat simulation samples. Live state and profile progress remained unchanged. Source snapshots, final smoke output and deployment hashes are recorded under `.local/concrete-choice-diffs-20261014/`.

Release installation was blocked by the running game holding `SeedOracle.dll` (PID 18012). The installed 0.1.27 files were backed up; the verified 0.1.28 package is staged at `artifacts/SeedOracle-0.1.28.zip`. Installation must follow game exit. No live game process was stopped.
