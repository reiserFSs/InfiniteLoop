# PGR 4.9 one-click equipment upgrades

## Two failures in the shop preload and exchange lookup

The Memory one-click entry point in the installed EN client calls `XEquipAgency._RequestAwarenessEnhanceShopInfo`. It first requests shop base information, then asks `GetFixedShopListRequest` for Material Shop 1422 and Command Bureau Personal Shop 4001. Its silent-error callback returns immediately on a nonzero code, before it caches any shops or opens the Memory upgrade screen. The weapon entry point requests shop 1422 on its own.

On upstream `ad3bf047`, `ShopModule.GuildShopInfoCode` returns `20063226` for a player without qualifying membership. The client `CodeText` table identifies that code as `GuildGetShopHasNotGuild`. The fixed-list handler copied the per-shop code onto the whole batch, including when the response contained the Material Shop. The diagnostic response at 17:39:24.100 was `GetFixedShopListResponse`, with code `20063226` and 17 goods for shop 1422. `GetShopBaseInfoResponse` at 17:39:24.088 contained shop metadata.

The handler now omits an inaccessible personal guild catalog and lets the batch succeed when it returns a populated shop and the membership error is the only failure. Guild-only requests, direct guild lookups, and guild purchases continue to enforce membership. The installed client Lua replay confirmed that the old response skips the open callback and the corrected response invokes it.

The weapon screen already received code 0 at 17:29:31.066, followed by missing-goods errors as it built the upgrade preview. `ShopCatalog` reads the curated `Configs/client_shops.json` snapshot. `BuildClientShop` clones the catalog and populates per-player counts and refresh fields. It passes through the catalog's goods list. The active launcher resource directory and source checkout had identical catalog SHA-256 `51c84a1c12f54b4239dc26653a5649d18d7c15d35b3d78243b2a4840be1785b7`. Both have 17 goods for 1422 and none of the six offers below. The same absence appears in the original [Simulated Battlefield shop import](https://github.com/reiserFSs/InfiniteLoop/commit/1dee8f91173887a6d35390530b1aab35e17b2169).

## Installed client evidence

The game log identifies app and document version 4.9.0. The Steam package declares package version 4.9.1. The Steam installation and the launcher copy have identical effective `ItemAutoExchange` and `ActivityBriefShopGoodsSort` TextAssets. The client-authored item mappings are:

| Shop | Goods ID | Item template | Item |
| --- | --- | --- | --- |
| 1422 | 147067 | 40103 | Weapon Overclock Core I |
| 1422 | 147068 | 40100 | Minor Overclock Alloy |
| 1422 | 147070 | 40113 | Weapon Overclock Core II |
| 1422 | 147071 | 40110 | Major Overclock Alloy |
| 1422 | 147066 | 40104 | Memory Overclock Circuit I |
| 1422 | 147069 | 40114 | Memory Overclock Circuit II |

`ItemAutoExchange.tab` has 85 rows and exactly three columns, `Id`, `ShopId`, and `GoodsId`. `ActivityBriefShopGoodsSort.tab` lists all six offers in Material Shop sort group 4. `ActivityBriefShop.tab` assigns 1422 and 1423 to separate tabs. The item names come from the installed `Item.tab`. This identifies the expected `RewardGoods.TemplateId` for each offer. The public EN and CN client dumps independently show the same relationships. [Public EN client extraction](https://github.com/myssal/PGR_Data/blob/de33e494ea090e82dff530d0d115e79d414f0dcf/en/bytes/client/item/itemautoexchange.json)

`XItemManager.GetItemAutoExchangeInfo` reads the configured shop/goods pair for overclock materials. `XShopManager.GetGoodsExchangeInfo` looks up the cached goods record and checks its reward template and nonempty consume list. The preview uses the server's bundle count, consume IDs and quantities, and active sale percentage. A missing record makes the route unavailable before purchase. With auto-exchange enabled the client sends `BuyRequest` for each planned exchange first, then `EquipOneKeyFeedRequest` for each selected Memory or the selected weapon, and `EquipResonanceRequest` for selected weapon slots. The weapon logs show that requests can still succeed when enough materials are already owned.

The installed manifest and targeted/full scan cover 21,170 table and selected Lua paths from the effective launcher copy and 21,141 from the Steam installation, with selected bundle bytes checked against the effective file index. A second parser decoded the shop detail and screen group tables. Those hold presentation metadata. The scanned asset names, decoded ID hits, public extractions, and existing InfiniteLoop catalogs yielded no server-owned offer definitions.

## Fields to recover from the retail server

Each of the six offers still requires its priority, reward type and bundle quantity, the full `ConsumeList` including the exact currency and quantity, purchase limits, conditions, sale/discount configuration, schedule and reset rules, gift information, and any other static flags. The older material offers and the exchange shops at 108/109 do not establish those values. `TotalBuyTimes`, calculated refresh timestamps, and other player-specific fields are runtime values. One response can show the current values but may be insufficient to identify the rules that produced them.

The next source is an authorized retail 4.9 EN shop export or a decoded response from the player's own retail session. Open the Simulated Battlefield Material Shop and collect the successful `GetShopInfoResponse` for 1422, or the 1422 entry in a successful `GetFixedShopListResponse`. Capture it on an account that can see the offers and include the region, client/document versions, and capture time. Record the six complete goods objects and any shop-level reset fields. A refresh/reset sample or the server-owned configuration is needed if a schedule, limit, or conditional visibility remains ambiguous. Share only the catalog fields and provenance, with account details and the raw capture kept private.

The repository decoder accepts a previously obtained classic Ethernet/IPv4 PCAP:

```powershell
python Scripts/decode_pgr_pcap.py '.runtime/retail-49-en.pcap' --port 0 --dump-dir '.runtime/pgr49-shop' --dump-name GetShopInfoResponse --dump-name GetFixedShopListResponse
```

Select a successful payload's shop `1422` and its six goods IDs. Compare each response to the field types in `GetShopInfoResponse` and retain only verified static catalog fields under the existing curated-shop rule. Runtime counts remain derived from player data and refresh rules.

### Reproducing the client-table extraction

Use Python with `UnityPy`, `pycryptodome`, and `lz4`, plus the repo's bundle resolver and BinaryTable decoder. For this package, the effective manifest is `37ec7ceb97560217` in `PGR_Data/StreamingAssets/document/matrix` for the launcher copy or `resource/matrix` for the Steam base. The document manifest's SHA-256 is `ace6905a92ad9992936b6e3c7494fcf7a0bb9d11f9393819fff81781b703db03`. The manifest lists these physical bundles:

| TextAsset | Bundle | Effective launcher-copy bundle SHA-1 |
| --- | --- | --- |
| `assets/temp/bytes/client/item/itemautoexchange.tab.bytes` | `dfc14e5f356f76ca.uab` | `65e2f8056c755db22bbb3bc7e7df23eac5f80c14` |
| `assets/temp/bytes/client/activitybrief/activitybriefshopgoodssort.tab.bytes` | `6d9245e613d70d7a.uab` | `8c3137deaae5d36eadd48d00f7e33f75b78e819f` |
| `assets/temp/lua/matrix/xmanager/xshopmanager.lua.bytes` and the equipment/item Lua modules | `e6b53fac297abd1a.uab` | `59a861feb52f3d9ba1d3c8a0a326fc61c138b285` |

Resolve by name using [the repository's bundle tooling](development.md#resolving-client-bundles-scriptspgr_bundlespy) or select the physical file above from the document directory first. Verify its SHA-1 and length using `pgr_bundles.Resolver.file_entry`. Load `pgr_bundles.strip_prefix(raw)` with UnityPy and its PGR key configured from `pgr_bundles.KEY`. Read the named TextAsset's `m_Script` and pass its bytes to `Scripts/decode_store_table.py` or its `decode` function. Filter the returned rows to `ShopId == 1422` and the goods IDs above. This procedure reads the installation and produces local research output.

## Verification and remaining scope

- `--equipment-one-click-shops-only` verifies the two preload shapes, membership states, existing catalog and item references, duplicate IDs, and rejection of unauthorized guild requests and purchases.
- `--equipment-one-click-upgrade-only` uses a disposable MongoDB database. It covers a single Memory enhancement, a two-Memory bulk sequence, weapon breakthrough and leveling, selected weapon resonance, exact item and coin debits, and state reloaded into a fresh session.
- The published Windows Release server also completed the Memory and weapon RPCs with pre-owned materials and preserved the levels, material balances, and selected resonance after a host process restart. GM Tools and Item Finder integration tests passed on the source used by the active server.
- The six exchange definitions and a client UI run with auto-exchange enabled remain required for full completion. The response fix makes the Memory preload usable, and exchange-dependent upgrades still lack authoritative catalog data.
