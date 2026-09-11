# InfiniteLoop

InfiniteLoop is a working branch of [AscNet](https://github.com/rafi1212122/AscNet), a private-server emulator for **Punishing: Gray Raven**. This branch focuses on bringing AscNet forward to the current global PC/Steam client path and documenting the compatibility work needed for the 4.5-era client.

This is research/dev infrastructure, not an official service. It expects a local client, a local AscNet process, and a local MongoDB database.

## Current target

The current compatibility target in this tree is:

| Area | Value |
| --- | --- |
| Client package | `com.kurogame.pc.punishing.grayraven.en` |
| Application version | `4.5.0` |
| Document version | `4.5.12` |
| Launch module version | `4.5.12` |
| Steam/PC channel | `205` |
| Game server TCP port | `2335` by default |
| SDK/HTTP URL used by the runner | `http://127.0.0.1:8080` by default |

`Resources/Configs/version_config.json` carries the current `4.5.0 -> 4.5.12` version/hash tuple used by the current-client config endpoint.

## What changed in this branch

### Current-client config and SDK routing

- Added current-client config generation for the PC package `com.kurogame.pc.punishing.grayraven.en`.
- Added Steam/PC channel handling with channel `205`.
- Added current-client CDN and server-list payloads for the config endpoints.
- Added Kuro SDK compatibility endpoints under `/sdkcom/v2/...`, including:
  - email/password login
  - Steam third-party login
  - auto login
  - real-name login/check
  - access token
  - OAuth code generation
  - Steam/PC third-login mark/browser helpers
  - player config/system config payloads
- Added gate-login fallback via `ASCNET_GATE_FALLBACK_USERNAME`, so Steam/KRSDK handoff can map an unknown external user id to a local AscNet account.
- Added current notice fixtures and endpoint compatibility checks for the current client.

### Steam/PC bridge

- Added `run_steam.py`, a local runner that starts AscNet, optionally starts MongoDB, starts mitmproxy, performs SDK/config smoke checks, creates or verifies a local AscNet account, and launches the client command with proxy environment variables.
- Added `proxy.py` routing for current PC/Steam HTTP traffic:
  - `sdkapi.kurogame-service.com`
  - `sdkapi.kurogame-service.xyz`
  - `prod-encdn-*.kurogame.net`
  - local wildcard `/api/`, `/prod/`, and `/sdkcom/` requests
- Added redacted proxy flow logging to `.runtime/proxy-flows.log`.
- Added KRSDK cache repair/seeding helpers for local Steam bridge experiments.
- Added `launch-pgr-ascnet.sh` as a macOS/CrossOver launch example. Its paths are workstation-specific; adjust them before reuse.

### Game protocol and data compatibility

This branch adds or fixes current-client server behavior for:

- `NotifyLogin` shape and current-client login data.
- Completed scheduled sign-ins stay marked claimed across login and daily reset; recurring daily sign-ins still advance normally.
- Dorm commission board replacements notify the client; stale dispatch indexes trigger a board resync without partial acceptance.
- Dorm commission system upgrades retain the current level until the saved start time plus the table-defined duration has elapsed. Relogs and restarts cannot finish them early; reconciliation completes each upgrade once and recovers previously stuck AscNet upgrades without restarting their timer or charging again.
- Ultima Awakening checks claimed Exhibition milestones for the relevant construct. Eligible skills still require an unlock request, and learned skills survive character reloads.
- Character login normalization reuses skill-upgrade, condition, and skill-level indexes within one roster pass; skill eligibility is still evaluated per character against current player state.
- Observer activation (including Ishmael) derives from the actual deployed team and all applicable learned observation skills. Empty descriptive skill rows no longer hide activation effects; Tank/Amplifier/Breaker selection follows the client rules, including physical-member exclusions and catalog-based Breaker availability. Ordinary, guild and roguelike pre-fight builders share this logic; cached mode battle payloads retain the effects, and the base Observer career remains unchanged.
- Current-client notice payloads.
- Stage bookmark compatibility.
- Board mutual push compatibility.
- Mainline 2 exhibition chapter compatibility.
- Mainline treasure rewards using current `Treasure.tsv` and `Chapter.tsv` contracts.
- Story course rewards.
- Boss single login payload shape.
- Guide table compatibility for current guide TSVs.
- Player cost-time upload.
- Player point upload.
- PR2 quality compatibility.
- Character progression persistence.
- Character/frame experience rollover and commandant-level caps.
- Fight settlement commandant EXP and character/card EXP.
- Current-client first-clear fields such as `FirstTeamExp` and `FirstCardExp`.
- Mission snapshots refresh after authenticated requests; supported character, equipment, draw, shop, stage, and client-event progress uses current task tables, persisted state, and committed actions. Overlapping task catalogs emit one update per task ID. Historical cumulative events are not fabricated.
- Group 1 `MemberTarget` draws use an explicitly local, non-retail policy where retail configuration is unavailable:
  - The numeric `DrawProbShow` profile mixes a 0.5% base S row with a 1.9% aggregate S-with-pity row. This local interpretation excludes the aggregate row, rolls the published 0.5% S chance exactly, then selects among the published non-S figures as relative weights: combined A/B characters 13.95, shards 22.11, memories 28.39, overclock materials 14.42, EXP materials 4.81, and cogs 14.42 (sum 98.1). This handling of the mixed profile is not a claim that those figures are exact unconditional retail probabilities; the values are loaded from the table rather than copied into server logic.
  - Because no authoritative A/B rank split or member weights are available, the base A/B result is selected uniformly from the visible eligible full-character pool, with the selected target included once; the pool composition therefore determines each rank's chance. Conditional on an A result, `DrawAimProbability` selects the target (normally 80%, or a row-specific 100%); a miss is uniform among other eligible A members. B and S members are uniform within their ranks. Preview goods, up goods, and minimum character quality remain authoritative, so a selected A target is never promoted to an S award.
  - S is rolled first. Otherwise, nine draws since the last A-or-S force A on the tenth draw; 59 draws since the last S force S on the sixtieth and take precedence. Every A or S resets the ten-draw counter, while only S resets the sixty-draw counter. Both counters are shared across group 1 targets, survive target switches, advance sequentially within batches, and are persisted.
  - Existing group 1 history is replayed chronologically when it is complete or contains a known S anchor, recognizing character results and conversion-result ranks; migrated progress is capped at nine draws since A-or-S and 59 since S. If history is absent or inadequate, migration preserves the old visible S remainder from the prior pity counter modulo 60 and initializes A progress at zero. These counters remain separate from existing total-draw counters, and other draw groups retain their existing behavior.
  - Calibration is also an explicitly local, permanent one-use entitlement per player, not a reconstructed retail campaign: authoritative `DrawAdjustActivity` campaign rows are unavailable. Its presentation uses the latest valid `DrawActivityTargetShow` row, stays open without calendar bounds, and derives selectable S characters from the existing eligible group 1 pools. Selecting or clearing a target (`0` means choose later) persists without changing pity. The next raw group 1 S result, whether random or guaranteed, becomes the selected S and consumes the entitlement before duplicate conversion; non-S results, other groups, and S results without a selection do not consume it. Batches resolve sequentially. Selection and consumption survive relog and never reset with time or presentation-table versions. Legacy players start unused because the old server never implemented or recorded calibration; existing draw history does not fabricate consumption.
  - The calibration banner remains visible and browsable before its introduction, but selecting a new nonzero S target requires completing the authored calibration guide. `DrawCalibrationGuide` links the draw group to its guide using the installed `GuideData` `UiNewDrawMain` / `PanelBanner/<group>` / `PanelSwitchS` focus paths. This local tutorial-before-use rule prevents spending the only check before the later tutorial needs its banner; it does not auto-complete or bypass the guide. Choosing later (`0`) and retrying the same selection remain allowed.
- CUB archive stories derive unlocks from owned partners and their progression, persist newly unlocked entries, and synchronize after committed changes and login.
- Norman sweep (`SweepStrongholdStageRequest`) resolves the configured group and its stages, handles already-paid partial clears, and shares normal-clear progression and reward settlement.
- Legacy Stronghold quick clear (`BfrtOneKeyPassGroupRequest`) validates the formation and computes battle power from character progression, equipment, skills, and authoritative tables instead of the uncomputed persisted `Ability` field. Attribute tables preserve original Q32.32 values as hexadecimal strings for client-compatible rounding.
- F.O.S. course battle results, exam scoring, rewards, and saved state have compatibility coverage. Lesson result saving remains explicitly blocked because the authoritative lesson-clear rule is missing; no inferred lesson completion or mission credit is awarded.
- Arcade Anima (`CharacterTower`, FunctionalOpen 10436) implements its seven mode requests from the EN `share/fuben/charactertower` tables: chapter reward, stage reward and star-treasure claims, relation fight-event and story activations, story-card video markers, and one-time trigger-condition acknowledgements. Stage and chapter rewards are manual claims (settlement never grants a first clear); each payout carries a stable per-player receipt, so a retried claim pays once even when the mode save is retried after a partial failure. Relation tiers activate against the authored `FinishNums` thresholds and freeze the conditions that are true at activation; `Stage.Stages` stays the only pass/star authority. Tower availability uses unbounded windows derived from `Tower.OpenTimeId`; the authored promotional chapter windows (`20402`, `34301`) and their activity chapter list are not reconstructed, so those chapters remain gated by their authored open conditions.

Mission coverage is not complete: Basic Research category `27000`, extended character/fashion task filters, and standard client-event subsystem eligibility still need authoritative rules. Unsupported predicates do not receive guessed completion credit.

The legacy power calculation follows the supplied tables, where `PartnerAbilityConvert` is zero. A nonzero future coefficient is rejected explicitly until partner power calculation is implemented; it is not silently omitted.

### Recitativo di Fantasia (original Theatre)

Original Theatre uses its own persistent adventure, chapter/node offers, event steps, node shops, recruitment, single/multi-team formations, skill choices, keepsakes, power favor, decorations, endings and shared tasks. Its 17 mode requests own the unprefixed Theatre protocol; the unused Bianca copies of the two multi-team requests have been removed. Login restores the real mode snapshot and pending durable outcomes. Combat routes remain separate from Cursed Waves, Matrix and ordinary stages.

The 28 imported original-Theatre tables contain 2,067 authored rows. They supply content IDs, weights, numeric parameters and references, not captured response payloads. **Generation, probability and economics described below are explicitly local rules where the proprietary server formula is unavailable; they are not claims of retail server equivalence.**

- **Nodes and fights:** visit authored chapter node rows in ascending ID order; fixed `Type`/`Param` pairs remain fixed offers, and their authored stage references may cross chapters. Random rows offer positive reward categories, capped by authored `MaxRandom` when present. Their weighted-without-replacement ordering uses `base reward weight + max(0, expected - current) * TheatreFactor[1..4]`; absent level weight is zero. Random stage candidates remain chapter-scoped and use their authored weights and skill bounds. Node randomness is seeded by the unchecked integer expression `(RunId * 397) XOR node-row ID`, and the resulting offers are persisted. Chapter groups advance sequentially without inventing cross-group transitions.
- **Events:** locally choose the lowest authored step-row ID for an event's initial step, then persist authored transitions and one-based option indices. Reward items use exact configured counts; option kind 1 consumes its configured items, while kind 2 only checks ownership. Event pass-record leaves are locally represented as `true`, matching the client's observed truthy-membership check. Revisiting an event step within the same slot is rejected. Battles and movies use the exact authored stage ID and story token; absent local-reward/keepsake/decoration-option data is not invented.
- **Node shops:** deterministically sample without replacement from the four reward types using authored `TheatreNodeShop` weights. Stock is `min(MaxCount, InitCount + ShopItemCountBonus)` and insufficient positive-weight candidates reject generation. Skill/level counts are one; currency counts are authored `DecorationCount`/`FavorCount`. Prices are `floor(authored base price * ShopPriceMultiplier)` and persist with the offers. The seed is unchecked `RunId XOR (RunId >> 32) XOR (SlotId * 397) XOR (shop ID * 31)`.
- **Availability:** no original-Theatre activity clock is invented. Only the user-approved decoration-release clocks `803`, `804` and `805` are permanently unbounded (`0`/`0`), derived from the condition closure of decorations `20003`, `20004` and `20005` by both the runtime calendar and offline generator. Wasteland/Kingdom I/Kingdom II still require chapter clears `8`/`14`/`20`, the preceding decoration at level 1 and their authored cost of 5 Inspiration (`96102`). Decorations are never auto-granted; promotion `24` is not auto-unlocked and other clocks retain their policies.
- **Starting and recruitment:** availability checks functional-open `10420` conditions and authored difficulty/SP-mode gates; only earned keepsakes can be selected. Locally choose the highest eligible chapter-group ID within the requested mode. The initial free offer and each refresh select one unrecruited role uniformly from each authored pool (no pool-member weights are supplied). Freeze source-valid roles, complete role-level robot mappings and owned-character IDs when the run starts. Recruitment allowance is `sum(chapter RecruitCount through the current chapter) + RecruitBonus * chapter count - recruited roles`; refreshes consume a used-count allowance capped by `RecruitRefreshCount + RefreshBonus`. Reopen usage starts at zero. Login never creates a run or rerolls offers; the last run is retained only where final presentation needs it.
- **Run currency:** an accepted new adventure journals removal of all positive leftover Marg (`96101`) before its once-per-run `InitialCoinBonus`. This local run-boundary reset does not count as task spending and prevents banking startup bonuses across start/abandon cycles. Rejected starts, reconnects and resumes do not reset currency.
- **Team ownership:** own-character use requires earned permission, ownership frozen at run start, recruitment of the matching role and the current character's power meeting `UseOwnCharacterFa` (currently `1000`, enforced here as a local rule). Permission becomes available monotonically within the run once the displayed trial roster's mean battle power reaches that threshold; displayed power is the current authored `RoleAttr.FightAbility` plus the current skills' `FightAbility` sum, re-evaluated on recruitment, level changes and skill selection. Trial characters use actual authored levels `5`/`20`/`40`/`60`/`80`, not level indices, with exact level-to-robot mappings; level changes remap saved teams. Single-team index is `0`, multi-team indices are `1..StageCount`, and captain/first-fighter positions must be occupied. Duplicate characters are rejected across owned/trial representations.
- **Skills:** locally select one weighted power, then offer up to three distinct eligible skills from that power. Use the rule row at `min(PassNodeCount + 1, last row ID)`. A power's weight is `max(1, PowerBasicWeight + Factor5 * (PowerExpect[power] - owned skills for that power) + matching equipped-keepsake PowerFactor)`. Within that power, candidate weight is `max(1, SkillBasicWeight + SkillWeight + Factor6 * (LvExpect[position] - owned core level))`; additional skills omit the positional term. Initial core quality is at least 1 and uses the highest active type-3 favor parameter; upgrades add `1 + highest active type-4 favor parameter`, capped by authored maximum quality. Replacing another power starts at initial quality. Favor type 2 unlocks authored favor-gated additional skills. Existing choices and shop previews stay frozen; skipping a purchased preview does not make it purchasable again.
- **Settlement and progression economics:** locally compute each of the five settlement-factor contributions as `floor(count * coefficient)`, then checked-sum them; an absent fifth coefficient contributes zero and a run with no node has zero score. Difficulty scales pending currency, not score. Decoration Inspiration/Cadenza multipliers apply once when a node award is earned; settlement grants `floor(accumulated pending amount * Difficulty.RewardFactor)` once for Inspiration (`96102`) and Cadenza (`96103`) through run-fenced receipts, then clears the pending counters. Choose the highest-priority ending whose authored default/run-event/run-chapter conditions pass. Because no server `PassType` is supplied, abandonment uses only achievements actually earned in that run. Power favor uses the current row's cost to buy the next level; a separate claim activates reward types 2/3/4, grants type 1, or unlocks type 5.
- **Keepsakes and native effects:** keepsake `FightCount` thresholds are consumed per completed node, subtracting the threshold on level-up and stopping at the maximum. Authored skill, difficulty and decoration fight events are passed to native combat; display-only stat substitutes are not used. Original stage graphs own their spawns. Empty monster-level lists retain the native authored fallback rather than inventing level 20 or 100.
- **Restart and native revival:** whole-fight restart and tracking of used reopen lives are implemented. Native paid revival is not available: all 88 authored `Stage` rows and 80 `LevelControl` rows lack a reboot profile, so there is no authored original-Theatre `RebootId` source. The currently issued reboot values are `0`/`0`; the native normal-death gate therefore suppresses the `Reboot` RPC. Enabling native paid revival requires the missing authoritative reboot profile, not an invented price or allowance.
- **Shared progression:** chapter, event-step, decoration-level and ending conditions read original-Theatre state, including archive CG unlocks. Original task claims use the mode journal and existing reward receipts; a batch reports foreign task IDs as unprocessed rather than mixing journal ownership. Existing Matrix/Cursed Waves task recovery and epoch handling remain separate. The existing authorized regular/rare shop catalogs (`1193`/`1194`) are retained, including product IDs, rewards, prices and limits; their original conditions are checked before purchase, with no invented catalog.
- **Shared shop and guide receipts:** the existing regular/rare products use original-Theatre durable purchase receipts for costs, rewards, monotonic purchase limits and shared spend/purchase task counters. The local guide attribution is regular shop → event cause `92004`, rare shop → `92008`; original task claims use `92004` and settlement uses `92008`. These are explicit local producer assignments, not reconstructed retail server event routing. The guide still applies its authored item filters. Original tasks comprise 3 daily claims with period-scoped receipts and 41 permanent claims; a pending shared-shop purchase must finish through its matching retry or reconnect before mission-period resets.
- **Recovery:** persisted offers, skill choices, role levels, teams, completed multi-stage indices and native attempts survive reconnects. Multi-stage progress uses indices even when stage IDs repeat. Pending mutations retain cost/reward receipts and run fencing; unrelated requests cannot spend an old balance or replay an old successful mutation. Reconnect reconstructs state rather than blindly re-sending additive node or skill notifications.

Verification covers server behavior, encrypted TCP/Mongo normal and SP adventures, and headless replay of the actual EN Lua source. Rendered gameplay, native combat and movie playback have not been exercised.

### Derived from Matrix (Theatre3)

Derived from Matrix uses its own persisted activity, teams and recruitment energy, difficulty unlocks, paired quantum chapters, weighted map offers, event choices, shops, items, equipment boxes, suit workshops, chained fights, reward claims, endings, battle pass, talents, character mastery and shared tasks. Its 25 mode requests use the same inventory reward receipts as ordinary gameplay; shared pre-fight, settlement, revive, restart and leave routes retain the other modes' routing. Login restores the complete mode snapshot and pending ending presentation instead of an empty startup notification. New adventures require the authored functional-open conditions (Commandant level 70).

The 62 imported EN Theatre3 tables supply IDs, numeric operands, weights and foreign keys. Captures are comparison evidence, not runtime payloads. **This is a documented local rules implementation, not a claim of retail server equivalence:** proprietary server-only effect semantics and the original calendar are unavailable.

- **Calendar:** every positive `Theatre3Activity.TimeId` is permanently available with start and end `0` (unbounded), using the same calendar representation as Cursed Waves. The server calendar service and offline schedule generator derive the ID from that table; neither copies a captured timestamp. This explicit local availability policy overrides dated calendar rows for this mode.
- **Offer generation:** sample only eligible authored pools with their weights. Repeated encounter offers are allowed where the authored slot multiplicity requires them; run-level encounter exhaustion still applies. If eligible content cannot fill the requested slot count within its authored bounds, cap the count rather than inventing encounters or bypassing unlock conditions. Newly foreground equipment choices reconcile entries invalidated by an earlier award without charging a refresh or replacing still-valid choices.
- **Effects and stacking:** item instances and independent effect sources stack additively; price factors multiply; competing revive prices choose the minimum. Quantum `CoverId` replaces the covered effect, suit thresholds count distinct pieces, character-ending effects apply to their role, purchased talents persist, and event effect groups last for the rest of the run. All imported items omit `ExpireTime`, so they last for the run. The empty `EnergyUnused` table grants no unused-energy bonus.
- **Run currency:** a successfully accepted new adventure clears leftover positive innercoin (`96189`) through the reward journal before startup grants. This local reset does not count as spending for tasks and does not consume battle-pass or talent currency. Resuming, relogging and rejected attempts to replace an active adventure do not clear the balance.
- **Quantum battles:** interpret each authored `QubitValue` entry as `channel|threshold` and choose the highest threshold satisfied by the persisted A/B values before the fight. Its same-index `QubitMonsterGroup` replaces `MonsterGroupId[GroupOrder - 1]`; `GroupOrder` is one-based. If no threshold is satisfied, keep the base group. A validated win adds the same-index authored equipment-box, item-box and gold-group rewards, resolving their weighted group references. Lower tiers are not accumulated, and the threshold itself does not grant a quantum-value delta.
- **Native combat parameters and extra waves:** supply neutral `Theater3AtkFactor` and `Theater3HpFactor` values of `10000`, matching the native fixed-point denominators; authored difficulty fight events still apply. Interpret `FightStageTemplate.ExtraWaveRate` locally as a basis-point chance out of `10000`. Persist the selected weighted extra-wave row and use native `Theater3ExtraWaveTime` and its one-based group index for spawning. Extra-wave bonuses require the validated native result `StringToIntRecord["Theater3ExtraWaveWin"] == 1`.
- **Restart and revive:** use `Theatre3Reboot` selected by `Difficulty.RebootId`. Restart costs `FubenRestartCost`; revive costs `RebootCost` unless an active system effect overrides it. Both consume the same `MaxRebootCount` allowance under the local policy. A persisted request-ID/seed receipt prevents duplicate restart payment.
- **Recovery:** offers, step ancestry, purchases, claims, combat attempts and effect-trigger identities are persisted. Inventory grants use durable receipts. A recovered ending is paired with a projected login snapshot so additive destiny and successful-clear updates are not applied twice; there is no invented client settlement-acknowledgement request.

The local system-effect interpretation below follows numeric operands and referenced tables, not stale descriptive percentages. `p1`, `p2`, etc. mean successive `Params` entries:

| Effect type | Explicit local rule |
| --- | --- |
| 3 | Add `p1 / 100` to the gold multiplier. |
| 4 | Grant currency `p1`, amount `p2`, per completed node. |
| 5 | Add `p1` discounted shop slots. |
| 8 | Open item box `p2` once per run when chapter `p1` is reached. |
| 12 | Grant currency `p1`, amount `p2`, once per source activation. |
| 13 | Multiply shop prices by `p1`. |
| 16 | Add `p1` capacity to every equipment slot. |
| 17 | Track clears for equipment `p1`, with channel `p2` identifying HP, attack or pet growth; apply the referenced equipment group's native leveled fight events at the recorded clear count, without invented attribute IDs. |
| 18 / 19 | Track ordinary-fight / boss-fight clears for suit `p1` after acquisition. |
| 20 | For equipment `p1`, grant the referenced gold row `p2`'s count multiplied by recorded clears. |
| 21 | Open item box `p1` on each winning clear. |
| 22 | Add `p1` recruitment energy. |
| 23 | Override revive price with `p1`, choosing the minimum active override. |
| 25 | Open equipment box `p1`, `p2` times. |
| 26 | Treat `p1` as the branch-switch enable/disable flag. |
| 27 / 35 | Switch to the opposite line; defer while a node is selected. These two types intentionally share this local interpretation. |
| 28 | Add a uniformly selected inclusive `p2..p3` amount to quantum channel `p1`, capped by the configured maximum. |
| 29 | Add `p2` workshop uses for workshop type `p1`. |
| 30 | Independently roll item chance `p1` and coin-loss chance `p2` in basis points. Select items from weighted item group `p3`; select inclusive coin loss `p4..p5`, capped by balance. Repeated `12345` values in `p6/p7` are explicitly treated as reserved sentinels, not economic operands. |
| 32 | Add `p2` to quantum channel `p1` per clear. |
| 33 | Add `p1` free refreshes to each equipment box. |
| 34 | Multiply paid equipment-box refresh prices by `1 - p1`. |

Trigger and source identities are recorded before grants to prevent retry duplication and recursive grant cycles. Battle effects use authored native fight events and leveled events; currency changes use inventory receipts and mode capacity/energy/quantum notifications carry absolute values.

### Gender setup fix

The current client needs gender selection to update both persisted player state and the live in-session player cache.

This branch adds:

- `PlayerData.Gender`
- `PlayerData.ChangeGenderTime`
- `ChangePlayerGenderRequest`
- `ChangePlayerGenderResponse`
- `NotifyPlayerGender`
- `ChangePlayerGenderRequestHandler`

Behavior:

- accepts current-client gender values `1..3`
- rejects invalid values with `20002020` / `PlayerGenderCfgNotExist`
- returns `20002021` / `PlayerGenderIsSame` only after setup has completed
- treats `Gender <= 0` or `ChangeGenderTime <= 0` as incomplete first setup
- grants first-setup `Inventory.FreeGem x50`
- sends `NotifyItemDataList` for the 50 Black Card reward
- includes `RewardGoodsList` in the response
- sends `NotifyPlayerGender` before the success response so the profile can refresh without a client restart
- persists inventory and player state

This also covers the earlier broken state where `Gender` may have been written but no reward or `ChangeGenderTime` was recorded; that account can still receive the first-setup reward once.

## Repository layout

| Path | Purpose |
| --- | --- |
| `AscNet/` | Main host process. Starts the TCP game server and ASP.NET SDK server. |
| `AscNet.GameServer/` | TCP game protocol, request handlers, commands, combat/settlement logic. |
| `AscNet.SDKServer/` | HTTP SDK/config/login/KRSDK endpoints. |
| `AscNet.Common/` | Shared database models, MessagePack schemas, config, utility code. |
| `AscNet.Table/` | Table generator/parser support. |
| `AscNet.Test/` | Focused compatibility harness and regression checks. |
| `Resources/` | Runtime configs, current client tables, data fixtures, notices. |
| `proxy.py` | mitmproxy script that routes client HTTP traffic back to local AscNet. |
| `run_steam.py` | Steam/PC bridge runner for AscNet + proxy + optional MongoDB + launch command. |
| `launch-pgr-ascnet.sh` | macOS/CrossOver launch example for the Steam client. |

## Requirements

Minimum local tooling:

- .NET SDK 8
- MongoDB reachable at `127.0.0.1:27017`, or `mongod` available for `run_steam.py --with-mongo`
- Python 3.10 or newer for `run_steam.py`
- mitmproxy/mitmdump for Steam/PC bridge mode
- A local Punishing: Gray Raven PC/Steam installation for client testing

Optional/macOS-specific:

- CrossOver or another Wine launcher if you use `launch-pgr-ascnet.sh`

## Running AscNet directly

Start MongoDB first, then run:

```bash
dotnet run --project AscNet/AscNet.csproj -- --urls http://127.0.0.1:8080
```

Default config values come from `AscNet.Common/Config.cs`:

| Setting | Default |
| --- | --- |
| Game server host | `127.0.0.1` |
| Game server port | `2335` |
| MongoDB host | `127.0.0.1` |
| MongoDB port | `27017` |
| MongoDB database | `asc_net` |

`Resources/Configs/config.json` may be left empty to use those defaults, or populated with overrides.

## Running with the Steam/PC bridge

Basic local bridge with MongoDB managed by the runner:

```bash
python3 run_steam.py --with-mongo
```

Run AscNet, proxy traffic, and launch the client command:

```bash
python3 run_steam.py --with-mongo --launch-cmd ./launch-pgr-ascnet.sh
```

Useful options:

```bash
python3 run_steam.py --help
```

Common options:

| Option | Purpose |
| --- | --- |
| `--sdk-url http://127.0.0.1:8080` | Local SDK/config URL exposed by AscNet. |
| `--proxy-host 127.0.0.1` | mitmproxy bind host. |
| `--proxy-port 8081` | mitmproxy bind port. |
| `--with-mongo` | Start local MongoDB if it is not already reachable. |
| `--ascnet-username test` | Local AscNet account used for Steam login handoff. |
| `--ascnet-password test` | Password used when creating that local account. |
| `--gate-fallback-username <name>` | Map unknown Steam/KRSDK gate logins to an existing local account. |
| `--no-ensure-account` | Skip local account creation/checking and disable implicit unknown-user fallback. |
| `--seed-krsdk-cache` | Opt in to writing local AscNet account data into KRSDK cache files. |
| `--krsdk-cache-dir <path>` | Override the KRSDK login-cache directory used for repair/seeding. |
| `--no-proxy` | Run only AscNet; skip mitmproxy. |
| `--no-smoke` | Skip config smoke checks before launching. |
| `--proxy-log <path>` | Write redacted request/response diagnostics. |
| `--launch-cmd ...` | Command to start after AscNet/proxy are ready. |

On native Windows, pass the client's actual `%APPDATA%\KR_G143\A1855` directory with `--krsdk-cache-dir` when using KRSDK cache repair or `--seed-krsdk-cache`; the default path targets the macOS/CrossOver launch example.

The runner sets:

- `ASCNET_PUBLIC_HTTP_ORIGIN`
- `ASCNET_GATE_FALLBACK_USERNAME`
- proxy variables for the launch command
- `ASCNET_PROXY_TARGET`
- `ASCNET_PROXY_LOG`

## Local account flow

The runner can create or verify a local account before the client launches:

```bash
python3 run_steam.py --with-mongo --ascnet-username test --ascnet-password test
```

The underlying account endpoints are:

- `POST /api/AscNet/register`
- `POST /api/AscNet/login`
- `POST /api/AscNet/verify`
- `GET /api/Login/Login`

Steam/KRSDK login callbacks can be mapped to the local account through `ASCNET_GATE_FALLBACK_USERNAME`.

Register, login, and verify accept JSON bodies up to 16 KiB, including chunked requests. Malformed or missing credentials return HTTP 400; oversized bodies return HTTP 413. Successful account responses omit passwords. Password storage and local gate-fallback behavior are unchanged.

SDK/proxy diagnostics omit query values, and proxy diagnostics omit URL userinfo. Inbound TCP decoding uses untrusted MessagePack settings with a 64 MiB decompression ceiling; the existing 4 MiB wire-frame limit remains. Inbound packet diagnostics record metadata rather than raw payloads.

## Testing

Run the focused compatibility harness:

```bash
dotnet run --project AscNet.Test/AscNet.Test.csproj
```

Run one focused check:

```bash
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --player-gender-compat-only
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --theatre-compat-only
```

Available focused switches:

```text
--first-batch-safety-only
--dorm-dispatch-compat-only
--ultima-awaken-compat-only
--notify-login-compat-only
--stage-bookmark-compat-only
--mainline2-exhibition-compat-only
--mainline-treasure-reward-compat-only
--boss-single-login-compat-only
--guide-table-compat-only
--player-cost-time-upload-compat-only
--record-player-point-compat-only
--player-gender-compat-only
--board-mutual-push-compat-only
--character-progression-persistence-compat-only
--exp-level-compat-only
--story-course-reward-compat-only
--pr2-quality-compat-only
--current-client-notice-endpoints-only
--theatre-compat-only
```

Build the main projects:

```bash
dotnet build AscNet.Common/AscNet.Common.csproj
dotnet build AscNet.GameServer/AscNet.GameServer.csproj
dotnet build AscNet.SDKServer/AscNet.SDKServer.csproj
dotnet build AscNet.Test/AscNet.Test.csproj
dotnet build AscNet/AscNet.csproj
```

## Runtime data

Local runtime state should stay out of commits:

- `.runtime/`
- `.runtime/mongo`
- `.runtime/proxy-flows.log`
- build outputs under `bin/` and `obj/`
- packet captures (`.pcap`, `.pcapng`, `.cap`), `proxylog`, and `.DS_Store`

Do not commit runtime logs, MongoDB files, client binaries, or captured credentials.

The host's resource-copy rules exclude local `.runtime/`, `bin/`, and `obj/` directories, logs, packet captures, and macOS metadata. Runtime JSON and MessagePack resources remain included. Use a fresh output directory when checking packaging; exclusions do not remove files left by older builds.

Ignore rules do not untrack existing files or remove them from Git history. Removing sensitive files from the index is not a substitute for credential revocation or a separate history review.

## Current caveats

- This remains a compatibility/research server, not a complete production backend.
- Some modules are still skeletal or best-effort.
- Steam support is local-bridge based: it relies on local SDK/config responses plus mitmproxy routing.
- HTTPS proxying for pinned KRSDK hosts may break; the runner keeps HTTPS proxying opt-in through `--proxy-https`.
- `launch-pgr-ascnet.sh` is an example for one macOS/CrossOver setup and should be edited for other machines.

## Upstream

Original AscNet project:

```text
https://github.com/rafi1212122/AscNet
```

This branch is intended to document and preserve the current-client compatibility work separately from upstream.