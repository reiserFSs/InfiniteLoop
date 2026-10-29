# Surfaces that stay closed

When the client or this repository has no drop table, pity weights, shop goods, or mode table, the surface stays closed. A `Reward.tsv` row that only shares a number is a different grant. These notes record the player-facing behavior and the missing source. They are not permission to invent a pool.

## Daily section rewards and obsolete stage purchases

`ReceiveDailyRewardRequest` remains closed with `20020003` (`RewardManagerGetRewardGoodsNotFound`). The EN/CN daily sources and installed daily resource expose dungeon data, rules and special conditions, but no daily-section claim requirements or section-to-reward mapping. Clearing unrelated stages cannot establish that missing claim rule. No claim record, reward or reset schedule is invented.

`BuyMainLineChallengeCountRequest` rejects unknown stages with `20003161` and existing stages with `20003020` (purchase limit reached). The EN Stage source supplies no `BuyChallengeCost` or `BuyChallengeCount` values; the server's corresponding TSV columns are blank. This obsolete purchase path cannot sell attempts until an authoritative price and purchase limit are available. It neither deducts currency nor changes persisted attempt counts.

`UnlockPrequelChallengeRequest` validates `CoverId` and the one-based `ChallengeId` against imported `prequel/Cover.tsv`, returning `20003095` or `20003096` for invalid selections. Valid selections remain locked (`20003097`): the client UI references `ChallengeConsumeItem`/`ChallengeConsumeCount`, but neither the EN Cover source nor the installed Cover schema provides them. Missing cost data is not a zero-price unlock. No synthetic unlock state or login data is created. Repeated requests and reloads preserve these boundaries.

## MainLine2 chapter rewards and eggs

Chapter treasure claims are open: `MainLine2ReceiveTreasureRequest.ChapterId` resolves `MainLine2Chapter.TreasureId`, counts passed stages through its stage groups, and claims all eligible zero-based `StageCounts` tiers with their normal and highlight rewards. Achievement claims use the requested main chapter's `AchievementId`; only persisted achievement bits whose `MainLine2Stage.AchievementTpyes` are NORMAL/SPECIAL count, never hidden bits or stage clears alone. Existing MainLine2 login data carries durable chapter treasure indices and main achievement flags.

Egg reward tables and membership are available, but the current eggs use condition type `15204`, requiring an exact persisted stage behavior-event value (`XConditionManager` reads stage id, event id, expected value). The server has no persisted stage-event value source or validated report ingestion for these behavior parameters. `MainLine2ReceiveEggsTreasureRequest` therefore fails the condition (`20219013`, `MainLine2MainEggsTreasureRewardConditionReceive`) without granting or marking the egg; ordinary stage clears cannot substitute for that missing evidence. A supported condition uses the shared condition evaluator and table reward, and claimed egg ids use the existing login list. Opening the current eggs requires authoritative stage-event report ingestion/persistence.


## Weekly task activeness and new-player task UI

These surfaces are open. `GetWeeklyActivenessRewardRequest` takes the client's empty request and claims all eligible, unclaimed Type-5 `CurrentTaskActiveness` milestones. Points count distinct finished tasks with authoritative `Task.Tag = 1`; reward contents come from `Reward`/`RewardGoods`, not legacy daily reward tables. Claimed milestone values are persisted, included in task login data, and cleared with weekly-tagged task claims at the existing weekly reset boundary. The legacy Type-2 weekly item/bitmask remains separate. `SetNewPlayerTaskActiveUiRequest.Group` must name an imported `NewPlayerTaskGroup` row and sets that group's bit in the persisted UI mask; the full mask is returned and included in login/new-player status.

## Awareness chapter claim rewards

`AwarenessGetRewardRequest` is recognized, but grants remain closed. The client Lua still calls `GetRewardId()` and consumes `RewardList`; neither the repository `AwarenessChapter.tsv`, the CN fallback JSON, nor the installed EN resource `AwarenessChapter.tab` contains a `RewardId` column or any chapter-to-reward mapping. The installed resource table SHA-1 is `df7b80159f41b72e897e263bbeba16eb206507d1` (bundle/index SHA-1 `5b6f172592c0dfbf04d488b6b85c40d5c24655fb`). Missing chapters return `20182001`, unfinished chapters `20182012`, and completed chapters `20020003` (`RewardManagerGetRewardGoodsNotFound`) without mutation, including retries and reloads. Opening this claim requires an authoritative chapter-to-reward mapping, not a matching numeric reward id.


## Affection gift boxes

Normal, Fine, and Precious Gift Boxes (40691, 40692, 40693) are open. Each gives one favor gift of the box's quality. The members are the `CharacterTrustItem` rows of that quality: 40681 and 40682, 40683 and 40684, and the per-construct favorites 40601–40645. Each construct has their own precious gift, so a client patch that adds a construct adds an id. Account for that id here, in `Docs/items.md`, and in the expected pool in `AscNet.Test/Program.AutoUseGift.cs`. The client ships no `DropGroup` weights for ids 1003, 1004, and 1005, and the descriptions give no rates, so `AffectionGiftBoxPolicy.tsv` gives every member of a tier an equal chance. That is the same kind of labeled policy as the overclock boxes, not a recovered retail table.

Rewards 1003, 1004, and 1005 pay Black Cards, Cogs, and Skill Points. Those rows are not the gift pool. Quality 6 affection items 40800–40811 are a separate tier and are not in these boxes.

94033 Construct Research Surprise Fortune Bag is not one of these boxes. It opens from the chances printed on the item: 10/15/20/25/30% for 250/225/200/175/150 Event Construct R&D Tickets (50005). Those chances are recorded in `ConstructResearchFortuneBagPolicy.tsv`, keyed on item 94033. Reward 1011 pays Cogs and Black Cards and is not that grant.

## Other packs with no drop table

The same gap covers the other subtype-2 and subtype-4 packs. `Docs/items.md` lists each id. The affection gift boxes and 94033 are not in this list.

- 60003 Christmas Decor Blueprint Set. Reward 1006 is absent.
- 90101 and 90110 Equipment Overclock Black Box (S) and (M). Reward 9011 is absent. This is separate from 60001 and 60002, which open under the uniform overclock policy in `EquipmentOverclockDropPolicy.tsv`.
- 90104 Minor HQ Black Box, 90107 HQ Black Box (M), 90108 Memory EXP Set (S). Rewards 9014, 9017, and 9018 are absent.
- 91000 2–4★ Memory Box. Reward 9100's only good is Serum Bundle β (L) (90033), not a memory pool.
- 91006 6★ Memory Box, 92000 2–4★ Weapon Box, 93000 5★ Weapon Box. Rewards 9106, 9200, and 9300 are absent.
- 400031–400033 and 400060–400062 Flaming, Cobalt, and Green Eggs. Rewards 370000011–370000013 and 370000030–370000032 are absent. Each description grants 30,000 Cogs plus an Event Construct R&D Ticket count of 5, 10, or 15, and gives no weights.
- 1050–1052 New Year Present Test. Subtype 4, a red-envelope share chest. The client ships `RedEnvelopeNpc` only. The Cat Grab drop tables are absent.

## Fate banners

Fate groups publish pity as an inclusive 80–100 range and no threshold weights. `DrawServerRule.tsv` has `PityMin` 80 and `PityMax` 100 for groups 15, 36, and 38. A banner on those groups is not advertised, serves no draw info, and rejects a draw. See `Resources/Configs/draw-rules-source.md`.

That covers the Fate banners requested for the limited frame pools:

- Crucible Fate `2503` and arrival Fate `2510`, both group 15.
- Collab Fate `5613`, group 38. Its character banner `5612` stays open. The Fate pair does not.

## S-rank shard shop

Item 35 (S-rank inver material) and item 48 (Uniframe material) have no goods list in the shop catalogs. Overflow S-rank shards are not converted into item 35. Shop 6001 spends Trade Vouchers (102). Shop 601 spends item 34 for A-rank recycle. Details are in `Docs/items.md`.

## Operation Guardians

Operation Guardians is Maintainer Action (functional id 1302, `OnOpenMaintainerAction`). It is open: the 4.8 dump supplies `MaintainerActionConfig`, `MaintainerActionLevel` and `MaintainerActionEvent`, imported by `Scripts/import_maintaineraction_4_8.py`. These parts remain AscNet policy or closed, not retail behavior:

- The node pool for `NodePoolId` 2 is not in the dump. Board generation, node composition, hidden-node selection, card draws, the extra-action amounts of events 8 and 12 (event 13 states +2), the once-per-day start bonus and the warehouse reward (the level's `BoxRewardIds` entry) are labelled AscNet policy, seeded per player and week and persisted.
- `TimeId` 123 has no calendar row; availability is a labelled permanent-mode policy in `ActivityScheduleService`.
- Mentor nodes (node type 13, events 16-19, mails 61002/61003) stay inert: the mentor system is absent, so they are never generated and `HasMentorNode` is false.
- Other players on the board, cross-check requests and shared records are not simulated; the board shows only the owner and `MaintainerActionRecordRequest` returns only their own records.
- A lost response to a relocation node's event cannot be replayed (the follow-up request is indistinguishable from a retry); every other accepted node event is replayed.
- Only login creates the first board. A player who reaches the unlock condition mid-session gets the mode's data at their next login.

## Lightning and Thunder / The Meandering Three shops

Cerberus activity rows and Thunderbolt Manual guide activity `10007` reference shops `4008` and `4009`, but neither catalog exists in `Resources/Configs/client_shops.json`. The mode's story and challenge tables do not supply shop goods, prices or purchase limits. These two shops therefore stay closed; no catalog is inferred from the guide's reward totals or unrelated `Reward.tsv` rows.

Special "Honey Cake" (`97007`) and Movie Film: Cerberus (`97008`) remain inventory tokens, not automatically converted rewards. Guide `10007` lists Event Construct R&D Tickets (`50005`, target `750`), Trade Vouchers (`102`, target `2000`) and Punk Dynamo (`6003204`, target `1`), with event cause `180001`. Those are authored guide targets, not a free grant or a replacement shop catalog. Missing shops can prevent those exchange-derived guide targets from being earned.

## War Zone monster levels

No 4.8 table maps War Zone tiers (Backup through Legend) to monster levels. As maintainer-authorized AscNet policy (see `AGENTS.MD`), NPC levels are the curated levels in `Resources/Configs/arena_stage_data.json` scaled by `ChallengeArea.AvgScore` of the current challenge over that of the captured reference challenge (`LevelReferenceChallengeId`): Legend ×2, Hero ×1.5, Leader ×1, and Backup/Elite/Vanguard ×0.75 (those tiers share one `AvgScore`, so the table cannot separate them). Retail per-tier levels are unconfirmed; replace this when they are available.

## War Zone support buffs

No table links a zone to its support-buff group (`ArenaGroupFightEvent`). Retail pairs them by element (captured: Laser Alloy with 4174 Thunderstrike, Nihil Floating Platform with 4185 Raven's Dirge). As AscNet policy, each zone tab gets one support buff of its element, so an effect zone's two tabs get two: a group is eligible when it has one event per tab and each tab has an event whose `ArenaAreaBuffDetails` affix icon matches that tab's `AreaDistributeType` element, and a stable per-player draw picks among eligible groups. A fight applies the zone buff and the support buff of the tab it is fought on. A Chaos tab has no Chaos support buff and accepts any event; when no group matches, any group with one event per tab is used.

## Manual shop refresh and dorm tabs

The EN/CN assets contain no server-owned generic `Shop`/`ShopGoods` rows (also recorded in the table manifest). Every current curated catalog disables manual refresh (`ManualResetTimesLimit = 0`), so `RefreshShopRequest` returns `ShopKindIsNotReset` (20030018); no refresh prices or schedules have been invented. The handler supports static catalog costs/limits and explicitly authored shop `AutoResetClockId`, `IsResetManualTimes`, and goods `IsResetWhenShopReset` fields, with player counters and reset periods persisted separately. These field names come from the client's `XTableShop`/`XTableGoods` schema; applying the clock and reset flags is AscNet policy until server rows or retail behavior establish it. Escalating `ResetCostCountStr` schedules remain closed because no source rows are available; current fixed `RefreshCostCount` catalog prices are not evidence of those schedules.

Dorm is not closed: `GetShopBaseInfoRequest` returns existing curated Type-101 tab metadata only when the matching curated goods catalog exists. The client uses `Id`, `Type`, `SecondType`, and `Priority` to select and order those tabs. Goods, purchase counts and automatic reset times continue through the normal shop flow; missing dorm catalogs are hidden rather than populated with invented furniture offers. A repeated successful manual-refresh request is a new paid refresh (the client supplies no operation token), not an idempotent retry.

## Friends / social data sources

Friends, incoming applications, notes and blacklist entries are persisted and supplied through `NotifySocialData`; online targets receive the client-defined application/add/delete/block pushes. Limits and retry windows come from `share/config/Config.tsv`, normal friend capacities from `share/player/Player.tsv`, honor capacities from the CN fallback `share/player/HonorLevel.tsv` (the EN extract lacks that table), and note length from the imported client `MaxNameLength`. Recommendations contain real database players only; stable most-recent-login ordering is labelled AscNet policy because the retail ranking algorithm is not published. UTC calendar days define the daily application counter as AscNet policy. Friendship experience accrual and mentor-relation restrictions remain closed: no friendship-experience grant rule or persisted mentor backend exists in this slice; no synthetic experience or mentor relation is created.

## Archive unlocks and evaluations

Monster/info/skill/setting, weapon, and awareness acknowledgement requests validate the imported EN archive rows and durable kills/equipment before recording unlocks. Their authoritative rows contain no reward fields, so acknowledgements do not grant invented rewards. Login includes persisted unlock lists and the highest observed equipment level/breakthrough/resonance records; equipment history is reconciled at login and archive requests, retaining those observations after equipment disposal. Equipment disposed before its first archive reconciliation cannot be reconstructed from a nonexistent historical source.

Monster scores, difficulty, tags and like/dislike state are one persisted record per player and NPC. Aggregate scores/counts/tags are calculated from persisted player records, with the requesting player's live saved state replacing its database copy. Score/difficulty bounds (0–5), three selected tags, and like states (0 neutral, 1 dislike, 2 like) follow the EN Lua UI. Repeating an acknowledgement or rating is idempotent; editing/retracting a vote replaces its contribution.

`GetStoryEvaluateRequest` is declared only in `XEnumConst` in both EN and CN Lua; neither source has a producer/response consumer establishing its wire semantics. It returns `ArchiveInvalidStoryId` (20065006), and unsupported evaluation types return `ArchiveInvalidEvaluateType` (20065007), rather than inventing story aggregates. Unrecognized archive condition types fail closed with `ArchiveConditionNotFit` (20065004).

## Extra chapters, short stories and experiment star claims

Extra/short-story treasure claims join the EN chapter details' `TreasureId` and `StageId` arrays to their star-treasure rows. The imported claim TSVs preserve those joins; progress is the persisted passed-stage star bit count, not client-supplied totals. Claims and rewards persist, and `NotifyLogin` returns the claimed treasure IDs. A treasure whose authored stage list cannot meet its threshold remains unavailable rather than receiving invented stage completions.

Experiment star rewards use the EN `ExperimentLevel.StarReward` → `ExperimentReward` join (currently only the authored reward-bearing level 57). Login `ExperimentInfos` includes `Id`, persisted `StarsMark` and claimed `StarList` even before the first reward claim; live star-bearing clears publish the same `Info` through `NotifyUpdateExperimentId`, including improved stars on repeat clears. Levels without authored star rewards return `ExperimentIdNotHaveStarReward` (20051004). No CN fallback or captured reward data is needed for these claims.

## Player profiles, diaries and collection walls

Profile queries read the selected player's persisted characters, coatings, weapon coatings, medals and score titles. Appearance visibility is enforced server-side (all, persisted friends, self), including direct character-detail requests. Appearance and chief assistant selections persist in `PlayerData`; diary/message/like and collection presentation state persist in `Player.Profile`. Deleting diaries/messages and banning messages are idempotent; diary likes deliberately toggle, matching `XPersonalInfoManager`'s add/remove response codes rather than treating a repeated click as an idempotent add.

The EN legacy diary UI specifies a 100-character input limit but neither EN nor CN exposes authoritative diary-count, message-count or daily-like quotas. Under the missing-retail policy, `share/player/ProfilePolicy.tsv` explicitly defines AscNet's 100 diaries per player, 100 messages per diary, 20 like additions per server business day, and ten diaries per page. Unliking does not refund the daily addition quota. These are local server policy, not recovered retail limits. The 16-by-9 collection wall grid is not policy: it is the EN `UiCollectionWallEdit` edit surface (1440×810) divided by the client cell size of 90, which the client clamps placements to. EN `CollectionWall` and `CollectionWallDecoration` supply wall/default decoration identities and unlock rules; missing EN `CollectionSize` comes from the CN client table. Conditional walls currently evaluate the level condition used by the imported walls; unsupported condition kinds do not unlock decorations. Reward-only decorations require persisted ownership and are not granted by edits.

`CollectionShowSetRequest` is declared but unused in both managers examined: its `IsHideCollection` field follows the existing login schema as an explicit AscNet contract. Collection edits validate owned score titles, decoration ownership/type, size/quality, bounds, overlap, repeated IDs/groups and cross-wall duplicate ownership. `--profile-compat-only` exercises the registered handlers with two players and BSON reloads. No item semantics change.

## Random home scenes, scene sharing and coating pairing

`SwitchRandomBackgroundRequest`, `AddRandomBackgroundRequest`, `RemoveRandomBackgroundRequest`, `SwitchRandomFashionRequest` and `EditRandomBackGroundFashionRequest` persist `Player.RandomBackground` and feed `NotifyLogin.RandomBackgroundLoginData` (typed `RandomBackgroundData`/`RandomChars`/`RandomFashions`, as `XPhotographManager` reads them). Scene identity/ownership come from `Background.tsv` plus persisted/free scenes, the pool cap from `Config.RandomBackgroundCountLimit`, error codes from the `RandomBackground*` rows of `CodeText.tsv`. The first activation seeds the pool with the submitted current scene; new pool entries default to the chief assistant (`DisplayCharIdList[0]`) wearing its current coating, mirroring `XUiSceneRandomSetting.OnBtnSaveClick`. Closing adopts the submitted last-random scene as `UseBackgroundId`.

No code in `CodeText.tsv` covers `SwitchRandomFashionRequest` or `EditRandomBackGroundFashionRequest` failures: enabling scene fashions while random scenes are off returns `RandomBackgroundNotOpen` (20244001, the client only opens the panel while random scenes are on); invalid edits (scene outside the pool, fashions not owned/unlocked or of another character, no random assistant, repeated ids, empty fashion list, edit while scene fashions are off) return the generic 20012001. `RandomBackgroundNotOpen` is otherwise unreachable because no table gates the feature. `NotifyRandomCharUpdate` is not sent: assistant removal is not a server-side flow here.

`ShareBackgroundRequest` and `PhotoBackgroundRequest` are `XNetwork.Send` calls with no callback and no table in EN/CN defines a share or photo reward, so the handlers accept them without state change or response.

`FashionSuitSetRequest` is defined in EN `XFashionManager` but no Lua issues it, so its semantics are inferred: bind the weapon coating (0 = default, otherwise owned and unexpired) to an owned character coating of an owned character via `FashionList.WeaponFashionId` (the pairing `FashionSuitPoolSaveRequest` stores), leaving `IsRandom` and the worn coating untouched. The weapon coating's character fit is not checked because no weapon-coating table is imported.

## Gift codes, legacy auto-fight, movie options and story RPCs

`UseCdKeyRequest` always returns `CdKeyInvalid` (20054001): no client table defines a code catalog and no AGENTS.md exception authorizes a local one, so no code can be redeemed.

`StartAutoFightRequest`/`ObtainAutoFightRewardsRequest` (XAutoFightManager) persist `Player.AutoFightRecords`. The stage must have `AutoFightId` with an `AutoFight.tsv` row and be passed; runs are capped as `XUiNewAutoFightDialog` does (`MaxChallengeNums - PassTimesToday`, bounded by `AutoFight.Limit` when positive); AP is `RequireActionPoint` (else `FinishRequireActionPoint`) × runs; duration is the stage's `LastRecordTime` × runs. Obtaining after `CompleteTime` pays `FinishDropId` (plus the level-control row) × runs, `TeamExp`/`CardExp` (else `Finish*`) × runs, once. Not enforced because no source defines them: the `FubenManagerAutoFightPoolIsFull` cap, and `PassTimesToday` is not incremented (the server keeps no daily stage counter for ordinary fights either, so `MaxChallengeNums` stages are limited per request only). Prequel stages' separate challenge counter is not consulted. The modern client sweeps through `SweepRequest`.

`QueryMovieOptionsRequest`/`UpdateMovieOptionsRequest` persist `Player.MovieOptions` against `MovieRecordOptions.tsv` (unknown movie → 20241002, unlisted option → 20241003).

`StartStoryRequest` and `FinishStoryRequest` are commented out in `XStoryManager` in both EN and CN Lua, nothing else issues them, and no table defines story rewards or a story-id catalog, so no handler is registered.

## Chat gifts, emoji pack order, reports and guild scripts

`SendChatRequest` with `MsgType.Gift` (private channel, friends only; XUiPanelPrivateChatView/XUiPanelContactView) now persists a receiver-side `Player.ChatSocial.Gifts` record (WaitReceive → Received on `GetGiftsRequest`/`GetAllGiftsRequest`, Fetched once older than the previous daily reset as `XChatManager.UpdateGiftStatus` does). One gift per friend per daily period (`DailyResetTimestamp`, 20033008), receive quota `GetGiftMaxCountEveryDay` (20033005), claims are keyed `chatgift:<id>` so a retry never grants twice. The gift item is Cogs (EN text `GiftMoney*`); the per-gift count is a uniform draw in `ChatGiftCountMin..ChatGiftCountMax` — [INFERENCE]: no client source states how the server picks it. Pending gifts are replayed through `OfflineMessageRequest` (cursor = gift id). No push to the sender when the receiver claims: no client source shows one (the sender's chat copy stays WaitReceive until relog).

`SaveEmojiPackageIdRequest` persists `ChatSocial.EmojiPackOrder`; ids must be distinct, non-default and owned (an emoji of the pack is usable), else 20033014. `GetEmojiPackageIdRequest` now returns the saved order followed by remaining owned packs in table order.

`ReportRequest`/`ReportGuildRequest` validate entry/tags against the imported `ReportEntry`/`ReportTag` tables (tier rules from XUiReport), the `ReportInterval` gap (20139002) and `ReportDailyMaxTimes` (20139003), then append a durable `ChatSocial.Reports` record and push `NotifyPlayerReportData` (also at login). No external moderation effect exists. Free-text `Message`/`ReportMessage` lengths are not bounded because no client source defines a limit. Push-before-response ordering is [INFERENCE]. The existing `ReportBanChatRequest` stays telemetry-only.

`GuildListScriptRequest` is commented out in EN/CN `XGuildManager`; it returns the personal scripts saved by `GuildChangeScriptRequest` (`AutoChat` stays client-owned, so `false`); non-members get 20063125.

## Purchases: bundles, Makeup Sign-in Cards, first recharge, pay check

`PurchaseComboRequest` composes the ordinary `PurchaseRequest` rules per member (priced at the bundle's `SubPackageDiscounts`), skips members at their own limit (as `XPurchaseManager.GetComboPurchaseData` does), and applies one reward/cost grant plus all member counts atomically through persisted `PendingCombo`. Retail ships no bundle definitions, so `client_purchases.json` has an optional `Combos` array that is empty; until the maintainer authors bundles every combo id answers 20053037. `PurchaseSupplementGetDailyRewardRequest` spends one Makeup Sign-in Card (`DailyRewardSupplementGetConsumeItemId`, item 106) per unclaimed day of the current month inside the pass term (days from the new persisted `FirstDay`/`ClaimedDays`; legacy passes without `FirstDay` have none). Weekly-mission card income and the month-end automatic use of cards are not implemented. `GetFirstPayRewardRequest` compares persisted `TotalPayMoney` (hundredths of `Pay.ShowAmount`, counted from completed recharges; earlier recharges were not recorded and count as 0) with `FirstPayReward.NeedPayMoney`; the unit is inferred from the CN row (10000 for a 100 CNY product). `PayCheckResultRequest` is never issued by the client; it finalizes/acknowledges the local pending recharge by order id, else 40001006.

## Collection box favorite mails, aureole selection, gather list, board mutual

`MailCollectionBoxReceiveFavoriteMailIdsRequest` validates ids against the imported `MailCollectionBoxFavoriteMail` rows and the open `MailCollectionBoxActivity` rows, rejecting unknown ids (`20027007`), repeats (`20027003`) and ids outside an open activity (`20027004`); `Code` is 0 whenever at least one id is accepted. No collection-box-specific codes exist in CodeText, so the mail-manager codes are the nearest family. Rewards (`RewardIds` → `Reward`/`RewardGoods`) are granted once per mail by claim key. Receipts persist in `Player.ReceivedFavoriteMailIds` and feed `NotifyLoginMailCollectionBoxData`. The activity `TimeId`s have no authoritative calendar (not in the client dump); under the missing-calendar AGENTS.md policy an activity without a known window is permanently open, a known calendar window is honoured. This is AscNet policy, not retail behaviour.

`CharacterSetLiberateAureoleIdRequest` requires an owned character, an `ExhibitionLimit` row listing the aureole, a `FashionAureole` row, and liberation level >= 5 (client `GrowUpLevel.Super`). The EN/CN `FashionAureole.Condition` and `ExhibitionLimit.SetAureoleId` columns are absent from both dumps, so no extra server condition is enforced. The id persists in `CharacterData.LiberateAureoleId`.

`BoardMutualRequest` is a fire-and-forget `XNetwork.Send` push with an empty body, issued on opening the favorability screen. No response or state exists; it is not the click counted by `TouchBoardMutualRequest`.

## LimitedLoginVerifyRequest

The quiz bank (`LimitedLoginQuiz`, `LimitedLoginConst`) is a server-only table: only its schema exists in the client `XTable.lua`, and no EN/CN client dump ships rows. `NotifyLogin.LimitedLoginData` is never populated, so the client holds no quiz list (`XLoginManager.CheckLimitLogin` never opens `UiLoginVerification`). The handler therefore fails closed with `20138001` (`LimitedLoginQuizIsNull`) and `IsCorrect=false`. Implement real verification only once the quiz rows and an issue/answered ledger are sourced.

## EquipTransformChipRequest / PartnerChangeNameRequest

`EquipTransformChipRequest` is gated by the active draw group's `TransformSuitList` (`XUiGridDrawGroupBanner` opens `UiAwarenessTfChoice` only from it). No table ships that list (`DrawManager` groups all have it empty), so the handler fails closed with `20021049` until a group advertises a suit. Result progression (main material Level/Exp/Breakthrough inherited; resonance dropped) is inferred from `XUiAwarenessTf`/`AwarenessTfConfirmContentResonance`; the client shows no item cost, only `EquipTransformChipUseCount` materials.

`PartnerChangeNameRequest`: the client only checks empty and `PartnerNameMaxLength`; no sensitive-word list exists in the client dump, so `20114007` only fires for control characters. `20114043` (rename banned) has no source and is never returned.

`IsRecycle` is persisted from `EquipUpdateRecycleRequest`; automatic recycling after `EquipChipSiteAutoRecycle` days is not implemented (no source for the sweep or reward), so marked chips stay owned.

## Guild dorm room KCP channel
The six room requests (`GuildDormEnterRoom/ExitRoom/ChangeCharacter/FurnitureInteract/PlayNote/NpcInteract`) are served on the dedicated dorm TCP listener (`GuildDormRoomService`), where `GuildDormPreEnterRequest` points the client. KCP stays closed: `ConnectKcp` is commented out in `XGuildDormManager.RequestLoadRoom` ("KCP not stable, disabled in 3.8"), and `GuildDormKcpConfirmRequest` fails with 20173017; the client falls back to TCP. Covered by `--guild-dorm-rooms-compat-only` (same suite as `--guild-dorm-compat-only`).

## CharacterUnlockRewardRequest / Stage Memory, Board Effect, Birthday single-story policy
`CharacterUnlockRewardRequest` is unreachable: `XFavorabilityAgency.OnCollectCharacterReward` (XFavorabilityAgency.lua:1567) has no caller in the 4.8 client Lua (`:128`/`:187` bind a nil local), so no handler exists.
`StageMemoryGetReward`, `BoardEffectTrigger` and `ViewBirthdaySingleStory` are implemented (`--favor-memory-compat-only`). No calendar exists for Stage Memory / Board Effect (`TimeId` 0/32010, stage windows 888/42710), so AscNet policy treats them as open (client `defaultOpen` semantics) and picks the highest-Id open row. The birthday single-story chapter pushes (`NotifyBirthdayPlayCg`, `NotifyBirthdaySingleStoryShow`) are not emitted (no birthday calendar), so the invite UI stays hidden until they exist; the handler validates the `SingleStory` row and that a birthday is set (inference for `20154002`). `BoardEffectActivity` rows come from the CN dump (absent in EN); the probability roll stays client-side, the server enforces only `TriggerCd`.

## BossInshot select skill/talent/rank, Luosaita character move, Concert settle, Maverick3 exit
`BossInshotQueryRankRequest` is unreachable: `XBossInshotAgency:BossInshotQueryRankRequest` has no caller (the rank UI `XUiBossInshotRank` only calls `BossInshotQueryTowerRankRequest`, served by `BossInshotTowerQueryRankRequest`), so it is not implemented.

`BossInshotSelectSkillRequest` (practice stage + fight event, fire-and-forget in the client) is validated against `BossInshotSkill`/`BossInshotBoss`/`BossInshotStage` and the tutorial gate, kept per session and merged into the next practice `PreFight` event list. `BossInshotSelectTalentRequest` persists `SelectTalentIds` (padded to `BosssInshotTalentSlotMax`); a talent is unlocked when `UnlockConditionId` holds. Condition type `15309` ([characterId, score], "accumulate N Trial Levels with the character") has no client evaluator; it is read as `BossInshotCharacterState.TotalScore >= score` from the condition text, and `NotifyBossInshotData.UnlockTalentIds` now only lists unlocked hand-worn talents. The client is not re-pushed unlock changes after a fight (relog refreshes).

`MainLineLuosaitaCharacterMoveRequest` fails closed with `20315030`: the server has no mutable Luosaita section state (login/enter/move/use-doc are capture projections keyed by stage progress) and the only authored move (`MainLineLuosaitaCharacterMove` 1: character 3401 on a section-4 block) belongs to a section the server never serves. Unknown move ids return `20315029`. Implement the move (persisted `CharacterMoveIds`, member `PosId`) together with real section state.

`ConcertPreHeatingSettleRequest`: stage must belong to the active activity and its own `TimeId` window; first settle persists the stage and credits the task group through `TaskModule.RecordTableDrivenProgress`; rewards come from the normal task claim (once only). The per-stage `TimeId`s (48811-48815) have no `ActivitySchedule` row, so by AGENTS.md missing-calendar policy those stages are open for the activity window; a scheduled stage window is enforced (`20426004`). Server condition type `140001` has no client definition; one-parameter conditions are read as "stages settled" and `[target, stageId]` conditions as that stage (inference from `Task.tsv` results 1..4 and the "Perfect Tuning" task pinned to stage 109).

`Maverick3ExitStageRequest` fails closed with `20240001` (`Maverick3ActivityNotOpen`): there are no Maverick3 tables, login data or stage saves server-side, so no save exists to discard.

## Small reward requests (4.8)

`AccumulatedConsumeDrawDoDrawRequest` fails closed with `20162002` (`AccumulatedConsumeDrawIsNotOpen`) and changes nothing. EN ships only the client-side `AccumulatedConsumeDraw*` tables (rule text, display-rounded `ProbShow` strings, shop/task ids). The server-side `AccumulatedConsumeDrawActivity` (coin item, `CoinCost`, `SingleMaxDraw`, `DropId`) and `AccumulatedConsumeDrawReward` exist only in the CN dump with different ids, and no drop table maps `DropId` to weighted rewards. `NotifyAccumulatedConsumeDrawData` is never sent, so the client has no activity to draw from. Opening it needs the EN activity row and drop weights.

`GetPlayerInfoRequest` is unreachable in the 4.8 EN client: `XPlayerManager.GetPlayerInfos` (`XPlayerManager.lua:106`) is defined but has no caller in `PGR_DATA/en/lua`. It is not implemented (`GetPlayerInfoListRequest` is the live path).

Version gift, accumulate-expend shop and game collection have no authored calendar (`TimeId` is absent/0 in the EN JSON and in the installed `.tab`). AscNet policy (AGENTS.md missing-retail policy, not retail behavior): the authored row is permanently available; for version gift the highest `VersionGiftActivity.Id` is "current"; the shop additionally requires its authored `OpenConditionIds` (condition `30406`, player level 40 — other condition types stay closed). Daily resets use the dorm `DailyResetTimestamp`. The shop's spend counter is `Inventory.SpentTotals` (item `ItemId` debits through `Inventory.Do`/`Spend` after the player's shop state was created); earlier spend is not counted. The shop's sign rewards and token conversion, and the version gift milestones (client counts Finish tasks of the three task groups = `MissionProgress.ClaimedTaskIds`), are claim-keyed once-only grants. `GameCollectionEnterGameRequest` only validates (the client reads `Code`); a suspended game is recognised for FangKuai runs only because Golden Miner and 2048 keep no server-side run. `NotifyGameCollectionData.GameData` stays empty until a child game records `MaxScore`.

## Norman Revival Plan (Stronghold) daily settlement

`StrongholdModule.Reconcile` settles each missed day boundary of the active cycle once (at login, before every Stronghold request and before pre-fight) in a single saved mutation, then pushes `NotifyStrongholdChangeDay` (not at login, where the login payload already carries the state). Boundaries are the authored `DailyResetTimestamp` (`GuildModule.DailyPeriod`), counted from `FightBeginTime` and capped at the cycle end; `CurDay` is 1-based and capped at the activity's day count (`FightContinueSeconds`/86400). Day `c` -> `c+1` adds `StrongholdEndurance` row `c` and `StrongholdElectric` row `c` for the chosen level (matches `XStrongholdManager.GetMaxEndurance` and `XStrongholdConfigs.GetElectricAdd`); each settled day writes a `MineRecord`, adds `ceil(miners * MinerEfficiency)` to `MineralLeft`/`TotalMineral`, then grows the miner item by `MinerGrowRate` percent. `GetStrongholdMineralRequest` credits `MineralId` through a per-cycle claim key. `BorrowCount` is reset every boundary (daily borrow limit). A day recorded by `SetStrongholdStayRequest` holds its boundary (nothing settles; the pause is consumed), deferring that day's amounts to the next boundary; the client's delayed-settlement forecast (`GetAddElectricEnergy` sums the paused day plus the next) is not mirrored exactly because no server rule is shipped.

Closed: the miner source. `StrongholdCfg.InitMinerCount` has no value and no table grants miners, so a player with no `MinerId` item mines 0 until the item is granted elsewhere. Documents saved before settlement (`settled_period` 0): an untouched state counts from its fight start, one the old stay handler had already moved (`CurDay`/`StayDays`/`MineRecords` set) is anchored to the current day so hand-advanced days are not settled again. Achievement claims (`RewardIds`/`ClaimedRewardIds`, claim keys `stronghold:{uid}:{ActivityId}:{BeginTime}:achievement:{id}`) are now per cycle and cleared at cycle start; claims already stored in the running cycle keep blocking a re-claim.
