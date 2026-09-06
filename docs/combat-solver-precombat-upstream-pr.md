# PR 记录（已合并）：Add a controllable isolated pre-combat forecast API for companion mods

PR #43 已由作者合并到 `main`。当前作者主线合并提交为 `0552b33`，manifest 仍为 `0.31.1`，并包含 public API v5；下方保留本地审阅分支从作者 `v0.29.1` 到 API v5 的提交链和历史验证结果。

## Problem

Companion mods can identify a future encounter on the map, but Combat Solver currently starts from an already active `CombatState`. Calling combat setup or `SolverController` from the live map would advance run/combat RNG, run global hooks, and mutate the active solver session. Reimplementing only part of combat setup outside Combat Solver would omit hook and mod semantics while still presenting the result as precise.

## Result

This change adds `CombatSolver.Api.PreCombatForecastApi` v5. A caller supplies the current single-player `RunState`, a known `EncounterModel`, target act floor and map column, room kind, map-point kind, and optional search controls. An ordered list of known intervening non-combat map points can preserve the requested route's floor, coordinate, and room-history context. The asynchronous result contains:

- status and request ID;
- projected whole-combat HP loss and final HP;
- planned potion ID, localized title, turn, and slot;
- search boundary and `Complete` / `Bounded` / `DeathOnly` confidence;
- combat end turn, search/total elapsed time, and diagnostic log path.

The live process only captures and validates state. Combat setup and search run in a separately owned headless Slay the Spire 2 process:

1. Capture the full normalized `SerializableRun`, an opaque SHA-256 state token, and the exact loaded Mod set on the main thread.
2. Build an owned hard-link game mirror under `.combatsolver-precombat`, mirror the loaded Mods, copy required configuration, use isolated `APPDATA` / `LOCALAPPDATA`, disable Steam and NoGC, and force all four isolated audio volume settings to zero without modifying the user's settings.
3. Restore the full run directly in the worker, load its assets/map, serialize it again, and require an exact normalized snapshot match before any hypothetical state is applied. Empty historical event-variable objects are normalized because the game restores them as absent values; populated maps remain byte-significant.
4. Record each validated intervening non-combat map point without executing its choices, optionally override entry HP for a clearly labelled scenario such as resting before a boss, and enter the requested room at the target coordinate through the existing game path. This preserves target `TotalFloor`, coordinate-derived monster RNG, normal combat-start hooks, and Combat Solver's root/search pipeline.
5. Return an immutable result, reset to the main menu, wait for tracked asynchronous work to quiesce, and publish a matching ready barrier before accepting the next request. Revalidate the active run reference, absence of active combat, and the complete state token on the main thread; return `LiveStateChanged` if anything moved.

Only one worker is active at a time. Sequential requests with the same game root, user root, and loaded Mod set reuse that process after its ready barrier. Its idle deadline defaults to two minutes but can be reset while idle, configured to another bounded duration, disabled with `null`, or bypassed with request-level auto-close; `StopWorkerAsync()` always remains available. Lightweight callers can share in-flight work and successful cached results. A visible manual operation can request a fresh result and own cancellation; in that mode cancellation kills and awaits the exact worker process before returning. The API disables itself inside the worker process to prevent recursion when a companion Mod is also loaded there.

API v5 exposes read-only PID, busy state, working set, private memory, peak working set, audio-mute state, and a nullable idle timeout. `SetWorkerIdleTimeoutAsync()` reschedules an already-idle worker, `RestartWorkerAsync()` can prewarm a fresh compatible process with the selected deadline, and each request can close it after the reusable barrier. A separate `SimulateAsync()` accepts a current-act encounter and caller-owned sample seed. After exact snapshot restoration, the worker replaces only the encounter-local RNG and combat RNG streams before native combat setup; these samples are deliberately excluded from deterministic forecast caching.

## Scope and compatibility

- Windows, single-player, out-of-combat states only in v5.
- Intended for a uniquely determined first combat on a route. Intervening steps must be consecutive and non-combat; unresolved events are rejected because their options can advance combat-relevant RNG. Purchases, rewards, smithing, relic hooks, and other player-state changes remain the caller's responsibility and must be labelled as assumptions.
- Existing in-combat `SolverController`, search, overlay, and deployment APIs remain internal and unchanged.
- Public contracts do not expose simulator, controller, or mutable combat types.
- The unattended protocol gains direct full-run restoration, target room/map-point fields, exact loaded-Mod assertions, API round-trip verification, and structured potion actions.
- Windows and Linux boundary scripts both enforce the new source ownership and forbid direct live-combat calls from `src/Api`.

## Validation

- Release build: 0 warnings, 0 errors.
- `tools/verify-refactor-boundaries.ps1`: passed.
- `PRECOMBAT-API-FINAL-004`, runId `7da852343994495e923dec58bf624b28`: public API round trip returned HP loss `5`, boundary `None`; exact worker restore passed and the live state token was unchanged.
- `PRECOMBAT-API-SEED-STACK-005`, runId `1961ef8c7d484da691e07cec99074215`: RitsuLib, Combat Solver, Random Foreseer, and Seed Oracle loaded together; exact Mod-set worker restoration and initial API call passed without recursion.
- `PRECOMBAT-POTION-METRICS-006`, runId `5e281451a3534051b605577cd51c9038`: the structured result contained `FIRE_POTION`, localized title `火焰药水`, turn `3`, slot `0`.
- `PRECOMBAT-REMOTE-CAMPFIRE-007`, runId `fe93b060ada64e78864fca8d825aeb85`: exact full-run restore followed a campfire and treasure route into floor 11 at the target coordinate; an entry-HP override of `66` produced loss `12` and final HP `54`.
- `PRECOMBAT-API-MANUAL-V2-008`, runId `f8db7c03ea9444cc89483495c985f221`: API v2 round trip verified event-history normalization, entry-HP override, and an unchanged live-state token.
- `SEEDORACLE-PRECOMBAT-PANEL-V2-STACK-FINAL`, runId `501485530e6a4453814bbb742aae9666`: RitsuLib, Random Foreseer, Combat Solver API v2, and the Debug companion loaded together; the map-construction smoke test passed panel/toggle creation and layering plus the existing route, hover-tip, and forecast checks. Travel was not yet enabled at this headless lifecycle point, so the manual target list was empty.
- `PRECOMBAT-API-SEED-STACK-0.29.1-FINAL`, runId `2e038fa1be6b45a19a7a5096c9be0f16`: after rebasing onto author version `0.29.0`, the four-Mod stack loaded local Combat Solver `0.29.1.0`; Seed Oracle reported `precombat_public_api_v2=true`, and the public API returned entry HP `79`, HP loss `5`, boundary `None`, with the live state unchanged.
- `PRECOMBAT-API-MANUAL-CACHE-REUSE-009`, runId `08fc0ae91d39417b9c234bbc5d2da6ea`: the four-Mod stack loaded Combat Solver `0.29.2` and Seed Oracle `0.1.14`; two forced forecasts of the same snapshot used one worker PID (`starts=1`, `reuses=1`), returned the same HP loss `5`, kept the live token unchanged, and verified all four isolated audio volumes at zero. The first request spent about `12.57 s` in game startup; the reused request spent `0.6 ms`.
- `PRECOMBAT-API-V4-WORKER-SIMULATION-010`, runId `61f19e8db7ae4f9a8a217424e602f075`: the four-Mod stack loaded Combat Solver `0.29.3` and Seed Oracle `0.1.15`; prewarming exposed PID and memory status, two exact forecasts remained identical, and one hypothetical sample confirmed its RNG marker inside the worker. The session used `starts=1 / reuses=3`, auto-closed after the sample, and left the live token unchanged.
- `SEEDORACLE-PRECOMBAT-V4-UI-011`, runId `505a961bceaf4e3ab8ffa74e347420b5`: the four-Mod stack constructed the Seed Oracle panel under its UI self-test and verified all 11 sample-count choices, all 6 target categories, both worker-retention policies, status/memory controls, close/restart controls, and the full hypothetical-result warning.
- `PRECOMBAT-API-V5-UPSTREAM-0291-013`, runId `e2236a4ec85041dc95c0b3417ed0b688`: after rebasing onto author `v0.29.1`, the four-Mod stack verified the 2/10/30-minute and indefinite retention paths, live idle rescheduling, PID/memory/audio state, one worker start with three reuses, deterministic forecasts, hypothetical RNG, auto-close, and an unchanged live token.
- `SEEDORACLE-PRECOMBAT-V5-LOCALIZATION-014`, runId `e15b744f36e84d85aa8771cf02328121`: the UI self-test verified five retention choices and required every current-act encounter title to resolve through the game localization API instead of rendering a raw `LocString … .title` value.

The remaining acceptance item is a visible-game check of the companion Mod's manual map panel, Stop control, and conditional labels. It does not affect the process-isolation or state-token assertions above.
