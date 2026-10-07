# Surfaces that stay closed

When the client or this repository has no drop table, pity weights, shop goods, or mode table, the surface stays closed. A `Reward.tsv` row that only shares a number is a different grant. These notes record the player-facing behavior and the missing source. They are not permission to invent a pool.

## Affection gift boxes

Normal, Fine, and Precious Gift Boxes give one random affection gift of that tier. They do not pay Black Cards, Cogs, or Skill Points.

`Item.tsv` lines the tiers up by quality. Favor gifts are item type `524292`.

| Box | Quality | Affection gifts of that quality |
| --- | --- | --- |
| 40691 Normal Gift Box | 3 | 40681 Best-Seller, 40682 Vinyl Record |
| 40692 Fine Gift Box | 4 | 40683 Ration Chocolate, 40684 1 Day Tour Tickets x2 |
| 40693 Precious Gift Box | 5 | Character favorites 40601–40645. A precious gift names its preferred construct |

Quality 6 affection items 40800–40811 are a separate tier. They are not these three boxes.

Each box is gift subtype 2. Its second parameter (1003, 1004, 1005) is a drop-group id. No drop table for those ids is in the installed client or in this repository, so the box does not open. Rewards 1003, 1004, and 1005 happen to exist and pay Black Card, Cogs, and Skill Points. That collision is not the gift pool.

## Other packs with no drop table

The same gap covers the other subtype-2 and subtype-4 packs called out with the gift boxes. `Docs/items.md` lists each id.

- 94033 Construct Research Surprise Fortune Bag. The description states Event Construct R&D Ticket chances of 10/15/20/25/30% for counts 250/225/200/175/150. Reward 1011 pays Cogs x50,000 and Black Card x30, which is not that split, and the drop table is absent.
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

Operation Guardians is Maintainer Action. The client opens it through `OnOpenMaintainerAction` with functional id 1302. This repository has the error strings and the `NotifyMaintainerActionData` schema, and no Maintainer Action share tables. Login does not push that notify. The mode stays unimplemented until those tables exist.
