# Purchase rule sources

## Server-owned supply-pack catalog

The maintainer explicitly authorized preserving the existing static package
definitions as local server configuration, removing inherited sales expiration
limits, and using authoritative reward-item icons if the original artwork is
missing. They confirmed that package listing, delisting and pricing must be
owned by the emulated server, as those definitions are not shipped in the client.

`client_purchases.json` now contains `SchemaVersion: 1` and a single `Purchases`
array (58 unique packages). Duplicate response records agreed on all static
fields; only player fields differed. The migration discarded those player
fields and set all three inherited sales timestamps to zero. It does not
introduce guessed v4.7 package definitions or renumber old artwork.

Edit a package in this file, then restart the server (no client update needed):

- `Id` is unique; `UiType` selects its store tab.
- `Enabled: false` removes it from lists and prevents direct purchases.
- `ConsumeId` and `ConsumeCount` define currency and base price.
- `NormalDiscounts` maps the one-based purchase number to basis points. The
  latest applicable tier is used, with integer rounding down, matching
  `XPurchaseManager.GetLBDiscountValue`. Keep the client's discount tag for
  discounted offers. Coupon and ownership-based price reductions are not enabled.
- `TimeToShelve`, `TimeToUnShelve`, `TimeToInvalid` are UTC Unix seconds;
  zero means unrestricted. Future packages retain the client's countdown;
  ended packages are omitted, and direct purchases are rejected at the same
  boundary. Setting a new schedule does not reset a player's purchase history.
- `BuyLimitTimes` and `ClientResetInfo` define limits; current usage is derived
  from persisted counts and purchase times. Monthly/sign-in reward duration is
  independent of these sales timestamps.
- `DailyRewardDays`, where present, defines the static daily-mail term. The
  adopted descriptions of Voyage Eternal and Radiant Crusade explicitly say
  "Get 1 reward per day by mail over 10 days"; both now configure 10 days.
  This is not a captured remaining-day value. Other packages continue to use
  the existing duration table, monthly rules or sign-in reward sequence.

The loader rejects duplicate IDs, legacy response snapshots and player-state
fields. `ConvertSwitch` follows the current base price, so a price edit cannot
leave the old captured price on the client. No production code reads decoded
traffic, research dumps or installed client files.

## Client artwork compatibility

The installed 4.7 client's resource index has no `UiPurchaseV405` bundle.
Some old icon mappings (including shared artwork and coating covers) still
resolve, so changing `UIv405_N` to `UIv407_N` is incorrect.

`Scripts/patch_local_store.py --catalog` extends the existing reversible
recharge patch. The shared purchase icon resolver receives the package's reward
list, keeps available artwork/covers, and resolves the first available reward
icon through `XGoodsCommonManager.GetGoodsIcon` and the installed resource
index. Lists, purchase details and recommendation/combo views share this
resolver. Sold-out/expired overlays clear the ownership overlay on refresh and
timer transitions. No specific package IDs or image paths are patched.

Prepare into a new empty backup directory while retaining any existing recharge
patch; inspect the manifest, then apply while PGR is closed:

```powershell
python Scripts/patch_local_store.py --game-dir '<installed PGR directory>' --output .runtime/store-catalog-client-patch --catalog
python Scripts/patch_local_store.py --output .runtime/store-catalog-client-patch --apply
# Restore the exact pre-catalog bundle (including any earlier recharge patch):
python Scripts/patch_local_store.py --output .runtime/store-catalog-client-patch --restore
```

Server checks: `dotnet run --project AscNet.Test -- --store-purchases-only`.
Client checks (requires `lupa`, plus the patch script dependencies):
`python -m unittest Scripts.test_store_catalog_patch`. Set `PGR_RESEARCH_LUA`
to the exported `PGR_DATA/en/lua/matrix` directory to also run syntax checks
and real list timer/overlay tests against that client version.

## Daily reward rules

`table/share/pay/PurchaseDailyDuration.tsv` is an explicit local-server
configuration, not a recovered retail table. On 2026-09-10 the maintainer
authorized the Construct 10x R&D Pack -10d (package 9) to last 10 days:
“同意，按 10 天配置”. Runtime resolves the duration by package ID and derives
the remaining days from persisted entitlement state and the server reset clock.
Unknown non-monthly daily packages still require a configured duration.

Companion monthly-pass grants bypass the direct renewal cap. This follows the
installed EN client's `Text.tab`, key `PurchaseMonthPlusDesc`: the beginner
combo remains purchasable with six active passes and extends their term.
The combo's own one-time purchase limit and mutually exclusive pass checks
still apply. Direct monthly renewals retain their existing cap.
