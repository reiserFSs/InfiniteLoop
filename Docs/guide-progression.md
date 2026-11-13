# GM guide completion

## Commands

Set `GuideGmPlayerIds` to a comma-separated string of player UIDs in the server's active `Configs/config.json`. With the Windows launcher, edit `%LOCALAPPDATA%\AscNetLauncher\local\config.json` and restart the server. The empty allowlist disables the commands. Sign in as the intended account and send the command in chat. The authenticated session determines the target. Command feedback goes to that session.

```text
/guide <guide-id> <preserve|grant|skip> [--dry-run] [--ignore-client-triggers]
/guides <group-id|all> <preserve|grant|skip> [--dry-run] [--ignore-client-triggers]
```

`/guide` takes a `GuideGroup.Id`. `/guides` takes a `GuideGroup.GroupId` or selects every active common guide with `all`. `--dry-run` uses the same eligibility and reward plan as apply, including guide dependencies within the batch. Bulk feedback shows totals and sample ids. Preview an individual id for its blocker or configured goods. A later pass can reevaluate gates after the player earns additional progress.

## Client and server contract

The installed EN 4.9.1 package's index-verified Lua is the protocol source. `XGuideProxy:FindActiveGuide` filters `GuideGroup` rows by `Ignore`, cached completion and every nonzero `ConditionId`, then sorts by `Priority` and `Id`. `ActiveUi`, node activation, behavior trees and UI interception belong to the running client. `XGuideRecordNode` calls `ReqGuideComplete` for `GuideComplete.Param[1] == 1`. An id on the wire identifies `GuideGroup.Id`. `GuideGroup.CompleteId` points to a shared completion rule, and `GuideGroup.GroupId` identifies a batch for the native group-finish request.

`XGuideManager.ReqGuideOpen` and `ReqGuideComplete` send `GuideGroupId`. `ReqCompleteGuideGroup` sends `GroupId` and its success callback marks the whole group from the current proxy. `GuideCompleteResponse` and `GuideGroupFinishResponse` carry `Code` and optional `RewardGoodsList`. Natural reward settlement pushes inventory or equipment before the success response. `XRpc.NotifyGuide` updates the cached id and dispatches the completion event. `NotifyLogin.PlayerData.GuideData` seeds that same cache on full login. A live notification may advance a waiting behavior node. Rendered behavior trees, prologue flow and closing an active tutorial overlay still require client validation.

`LevelCommand` sets the level from `Player.tsv`, sends `NotifyPlayerLevel` on an increase and runs the existing experience check. Guide eligibility can read the new level immediately. Stage passes and legacy course claims remain independent, including login's earned-stage guide reconciliation. The old `SkipCommonGuides` login setting marked non-ignored, rewardless guides without evaluating conditions. The explicit GM commands replace that behavior. Previously saved ids are retained, including markers whose original reason is unavailable.

`XFunctionManager` evaluates `FunctionalOpen` conditions and time windows. The client issues `ChangePlayerMarkRequest` for applicable marks, and `NotifyFunctionalEntranceData` carries red points. Neither is a GM unlock mechanism. Draw Calibration is a verified guide-dependent server gate. `DrawCalibrationGuide.tsv` maps the draw group to its guide, and `DrawManager.SetMemberTargetCalibration` checks the saved guide id alongside its other gates. Babylonia conditions and help-course triggers can also consume guide ids through their existing paths. Mode-specific guide lists, native-only state, and unknown functional predicates have no generic server completion rule.

## Eligibility and rewards

The GM action completes a guide record. It evaluates the authored persistent prerequisites that the server can verify, including level, stage pass, claimed legacy course reward, ownership and other guide records. A prologue fight with no explicit completion mode requires its authored `GuideFight` stage to have passed. Fresh AscNet stage documents already contain the server's normal prologue pass records. An unknown condition or completion mode blocks that guide. The optional `--ignore-client-triggers` flag waives only verified presentation checks for the client's download status, main-screen function-event queue and current battle-room UI. Stage passes, task claims, player level and functional marks still come from their own gameplay and command paths.

The command requires one explicit reward policy.

| Policy | Behavior |
| --- | --- |
| `preserve` | Completes eligible rewardless guides. Leaves eligible reward-bearing guides available through their normal completion flow. |
| `grant` | Completes eligible guides and settles authored guide goods through the existing reward handler. A missing or unsupported reward blocks that guide. |
| `skip` | Completes eligible guides without a payout. A reward with a durable receipt must use grant or resume its native completion. |

Inventory and character receipts already use the same `guide:<id>` key for native single and group completion. A rewarded operation follows the draw recovery pattern. It records the chosen ids and authored goods on the player before settling either reward document. It saves completion and clears the intent in the same checked player write. A process interruption or an uncertain write requires a fresh read of the saved documents before replay. Completed markers prevent later native payouts for skipped guides. The server cannot reconstruct a missing historical payout from a legacy completed id alone.

The current common-guide rewards resolve to items and equipment. After commit, the server sends the standard reward pushes followed by `NotifyGuide` for each GM-completed id. A full login restores the saved ids and balances together. A transport reconnect replays committed GM guide ids and current item and equipment data for guide grants. Recovery uses the account's existing receipts and performs no second grant after both documents have settled.

## Verification and limits

Run the focused compatibility harness with a loopback MongoDB on a non-default port and a disposable database whose name starts with `ascnet_manual_full_`. Configure the test output's `Configs/config.json`, then run from that directory.

```bash
dotnet AscNet.Test.dll --guide-progression-only
```

The harness logs in to two independent synthetic accounts over the server's encrypted TCP protocol. It exercises commands, policy choices, calibration, reconnect and logout/relogin, and injects lost acknowledgements around real MongoDB writes. It verifies the server packets and saved state. Rendered PGR gameplay requires a separate native-client run.

These commands cover the common guide tables. Babylonia and mode-specific guide state keep their own completion paths. Disabled guide rows, unsupported client or mode predicates, and reward categories without a verified replay path remain outside GM completion. The chat UI must be available to enter the command in-game. Client scripts determine when an active guide overlay exits and when feature UI refreshes. Regional guide-table differences have not been validated.
