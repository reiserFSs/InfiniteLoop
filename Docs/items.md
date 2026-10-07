# Item IDs for `/item`

`/item add <id> <count>` grants a row from `Resources/table/share/item/Item.tsv`. `max` fills that row up to its cap. Money with no cap stops at 999,999,999. These are the IDs a shop or a handler actually spends. The player-facing name is listed when `Item.tsv` uses a different one.

Item **105** is the Simulation Score. `Item.tsv` names that row Creation Operator, and the client enum is `TransfiniteScore`. The Overclock Simulation screen and its shop both read 105. Cap is 1,500.

```text
/item add 105 max
```

The next login deletes 105 unless this rotation already has a settlement receipt (`transfinite-terminal`, `transfinite-reset`, or `transfinite-score` for the current activity and circle). The balance stays for the current session, including reward claims.

Item **62738** is also named Simulation Score in `Item.tsv`. That row is a Simulated Battlefield exchange token and one 6★ memory resonance option. It does not move the Simulation Score.

## Currencies

| ID | Name | Cap | Spent by |
| --- | --- | --- | --- |
| 1 | Cogs | 999,999,999 | Upgrades, shops, hypertune |
| 2 | Black Card (free stack) | 999,999,999 | Pulls and premium shops. Spent before 3. The client shows 2 + 3 as one balance. |
| 3 | Black Card (paid stack) | 999,999,999 | Same balance. `/bc` grants this stack. |
| 4 | Serum | 5,000 | Stage entry |
| 5 | Rainbow Card | 999,999,999 | Coating, weapon, and scene Rainbow Card shops. Recharge grants this stack. |
| 12 | Skill Point | 99,999 | Skill levels |
| 23 | War Zone Influence | 99,999 | War Shop |
| 30 | Dorm Coin | 99,999 | Dorm Coin Shop |
| 31 | Decor Coin | 9,999,999 | Dorm decor placement |
| 32 | Coating Blueprint | 99,999 | Coating blueprint shop |
| 33 | Coating Sketch | 99,999 | Blueprint exchange |
| 34 | A-Class Inver Material | 99,999 | A-rank material shop |
| 39 | United Achievement Points | 99,999 | Point Shop. This is the guild coin stored on the player. |
| 45 | Weapon Coating Blueprint | 99,999 | Weapon coating shop |
| 47 | Special Support Token | 99,999 | Resource shop, and one 6★ memory resonance option (585 per roll) |
| 49 | Scene Blueprint | 99,999 | Blueprint Shop - Scene |
| 60 | Intel Value | 999,999 | Farwatch File Passport level |
| 63 | Signal Diffuser | 99,999 | Circuit Connect shop 1291 |
| 102 | Trade Voucher | 999,999,999 | Voucher exchange, including S-Rank Inver-Shard shop 6001 |
| 103 | Dispatch Certificate | 9,999,999 | Dorm decor sets |
| 200 | Phantom Pain Scar | 99,999 | A-Rank Omniframe, S-Rank Omniframe, Weapon Harmony |

Commandant level is `/level`. Item 7 is the Squad EXP counter, and `/item` does not apply it.

Guild contribution (38), Command Bureau EXP (40), Beacon Amplifier Points (46), and Cooperation Merit (62723) live on the guild record. `/item` does not fund those.

## Draw tickets

| ID | Name | Notes |
| --- | --- | --- |
| 50000 | Basic Construct R&D Ticket | Standard construct banner. This exact ID. |
| 50001 | Basic Weapon R&D Ticket | Standard weapon banner. This exact ID. |
| 50003 | Target Weapon R&D Ticket | Shares a balance with 50020. 50020 is spent first. |
| 50020 | Target Weapon R&D Ticket | Earned stack of the same banner. |
| 50005 | Event Construct R&D Ticket | Event construct banner. This exact ID. |
| 50009 | CUB R&D Ticket | CUB banner. This exact ID. |
| 50017 | Date A Live Character R&D Ticket | Shares a balance with 50021. 50021 is spent first. |
| 50021 | Date A Live Character R&D Ticket | Earned stack of the same banner. |
| 50018 | Date A Live Weapon R&D Ticket | Shares a balance with 50022. 50022 is spent first. |
| 50022 | Date A Live Weapon R&D Ticket | Earned stack of the same banner. |
| 50019 | Date A Live Armament R&D Ticket | Shares a balance with 50023. 50023 is spent first. |
| 50023 | Date A Live Armament R&D Ticket | Earned stack of the same banner. |

## Simulation Score and mode currencies

| ID | Name the player sees | Table name | Cap | Use |
| --- | --- | --- | --- | --- |
| 105 | Simulation Score | Creation Operator | 1,500 | Overclock Simulation score and that shop. See the note at the top. |
| 96101 | Marg | Marg | 999,999 | Recitativo di Fantasia run shop |
| 96102 | Inspiration | Inspiration | 999,999 | Recitativo decorations |
| 96103 | Cadenza | Cadenza | 999,999 | Recitativo power favor |
| 96104 | Neume | Neume | 999,999 | Recitativo shops 1193 and 1194 |
| 96117 | Downfall Crystal | Downfall Crystal | 999,999 | Cursed Waves reward level |
| 96118 | Curse-Dispelling Chapter | Curse-Dispelling Chapter | 999,999 | Cursed Waves permanent boosts |
| 96119 | Tinbread Cookie | Tinbread Cookie | 999,999 | Cursed Waves in-run shop |
| 96120 | Biomimetic Blood | Biomimetic Blood | 999,999 | Cursed Waves revive and restart |
| 96189 | Processing Unit | Processing Unit | 999,999 | Derived from Matrix shop, revive, and restart |
| 96200 | Supply Coupon | Supply Coupon | 9,999,999 | Awakening Tundra tech tree |
| 96002 | Tantalum Ore | Tantalum Ore | 999,999,999 | Norman Ore Shop |
| 96207 | Expedition Loot X | Expedition Loot X | 999,999 | Guild Expedition Shop X |
| 97074 | Service Certificate | Service Certificate | 999 | Service Shop 1460 |
| 97084 | Sealed Writs | Sealed Writs | 999,999 | Shrouded Requiem story rooms |
| 97086 | Retro Module | Retro Module | 999,999 | Retro Prize Counter 1468 |
| 50135 | Insatiable Chips | Insatiable Chips | 999,999 | Permanent shop 1436 |

62738 pays Simulated Battlefield shops 1421 Version Limited, 1422 Material Shop, and 1423 Memory Shop. It is not 105.

Shrouded Requiem Discernment (97085) can sit in the bag. Talent level moves only when a settlement grants it. Mammon Platinum Coin (97054) is granted into the bag and no implemented shop spends it. Godfall's in-run gold is the run's `GoldNum`.

## Upgrade materials

| ID | Name | Cap | Use |
| --- | --- | --- | --- |
| 30011 | EXP Pod (S) | 9,999 | Character EXP |
| 30012 | EXP Pod (M) | 9,999 | Character EXP |
| 30013 | EXP Pod (L) | 9,999 | Character EXP |
| 30014 | EXP Pod (XL) | 9,999 | Character EXP |
| 31101 | Weapon Enhancer I | 999,999 | Weapon EXP |
| 31102 | Weapon Enhancer II | 999,999 | Weapon EXP |
| 31103 | Weapon Enhancer III | 999,999 | Weapon EXP |
| 31104 | Weapon Enhancer IV | 999,999 | Weapon EXP |
| 31105 | Weapon Enhancer V | 999,999 | Weapon EXP |
| 31106 | Weapon Enhancer VI | 999,999 | Weapon EXP |
| 31200 | Processor Shard | 99,999,999 | Memory EXP from recycled memory |
| 31201 | Memory Enhancer I | 999,999 | Memory EXP |
| 31202 | Memory Enhancer II | 999,999 | Memory EXP |
| 31203 | Memory Enhancer III | 999,999 | Memory EXP |
| 31204 | Memory Enhancer IV | 999,999 | Memory EXP |
| 31205 | Memory Enhancer V | 999,999 | Memory EXP |
| 31206 | Memory Enhancer VI | 999,999 | Memory EXP |
| 40100 | Minor Overclock Alloy | 99,999 | Low-rank level-cap break |
| 40103 | Weapon Overclock Core I | 99,999 | Low-rank weapon break |
| 40104 | Memory Overclock Circuit I | 99,999 | Low-rank memory break |
| 40110 | Major Overclock Alloy | 99,999 | High-rank level-cap break |
| 40113 | Weapon Overclock Core II | 99,999 | High-rank weapon break |
| 40114 | Memory Overclock Circuit II | 99,999 | High-rank memory break |
| 34000 | Harmony Accelerator | 999,999 | Weapon Harmony |
| 24 | 4★ Weapon Shard | 99,999 | Weapon exchange |
| 25 | 5★ Weapon Shard | 99,999 | Weapon exchange |
| 26 | 6★ Weapon Shard | 99,999 | Weapon exchange |
| 27 | 4★ Memory Shard | 99,999 | Memory exchange |
| 28 | 5★ Memory Shard | 99,999 | Memory exchange, and a 5★ memory resonance option |
| 29 | 6★ Memory Shard | 99,999 | Memory exchange, and a 6★ memory resonance option (150 per roll) |
| 96004 | 6★ Weapon Resonance Shard | 9,999 | Weapon Resonance Exchange Shop |
| 30113 | Integrated CUB EXP (L) | 9,999 | CUB level-up. 30111 is the small pod. |
| 40200 | Support Overclock Bundle (S) | 9,999 | Low-rank CUB level-cap break |
| 40201 | Support Overclock Bundle (L) | 9,999 | High-rank CUB level-cap break |
| 40301 | Support Skill Component | 9,999 | CUB skill levels |
| 32000 | Leap Wafer Chip | 999,999 | Leap Shop |
| 33000 | Uniframe Single Crystal | 999,999 | Uniframe Shop |

Named Leap Wafers (32002–32021) and named Uniframe Crystals (33002–33008) are not read by a handler. The shops spend 32000 and 33000.

## Resonance

A 6★ memory roll accepts one of these materials:

| ID | Name | Cost per roll |
| --- | --- | --- |
| 3004 | 6★ Memory Resonance Material | 1 |
| 29 | 6★ Memory Shard | 150 |
| 47 | Special Support Token | 585 |
| 62738 | Simulation Score (table name only) | 613 |
| 3005 | 6★ Memory Resonance Material Pick | 1, and the client may choose the skill |

Other resonance materials:

| ID | Name | Use |
| --- | --- | --- |
| 3001 | 5★ Weapon Resonance Material | 5★ weapon resonance |
| 3002 | 6★ Weapon Resonance Material | 6★ weapon resonance |
| 3003 | 5★ Memory Resonance Material | 5★ memory resonance |

## Hypertune

The normal bill on a memory is 50,000 Cogs, Hypertune Catalyst α (79991) ×9, Hypertune Catalyst β (79992) ×3, that set's α ×6, and that set's β ×1.

The crystal bill is 50,000 Cogs, Hypertune Crystal α (70001) ×480, and Hypertune Crystal β (70002) ×80.

| ID | Set |
| --- | --- |
| 70011 / 70012 | Condelina |
| 70021 / 70022 | Shakespeare |
| 70031 / 70032 | Heisen |
| 70041 / 70042 | Darwin |
| 70051 / 70052 | Hanna |
| 70061 / 70062 | Cottie |
| 70071 / 70072 | Da Vinci |
| 70081 / 70082 | Catherine |
| 70091 / 70092 | Einsteina |
| 70101 / 70102 | Philip II |
| 70111 / 70112 | Guinevere |
| 70121 / 70122 | Frederick |
| 70131 / 70132 | Bathlon |
| 70141 / 70142 | Patton |
| 70151 / 70152 | Chen Jiyuan |
| 70161 / 70162 | Koya |
| 70171 / 70172 | Leeuwenhoek |
| 70181 / 70182 | Wu'an |
| 70191 / 70192 | Flamel |
| 70201 / 70202 | Lucrezia |
| 70211 / 70212 | Exupery |
| 70221 / 70222 | Tifa |
| 70231 / 70232 | Jack |
| 70241 / 70242 | Elizabeth |
| 70251 / 70252 | Seraphine |
| 70261 / 70262 | Marco |
| 70271 / 70272 | Unimate |
| 70281 / 70282 | Isabel |
| 70291 / 70292 | Boone |
| 70301 / 70302 | Shelley |
| 70311 / 70312 | Charlotte |
| 70321 / 70322 | Turing |

## Consumables

The server opens fixed gifts (subtype 1 or 5), random pools (subtype 2 or 6), and choice packs (subtype 3). A choice pack needs `SelectRewardIds`: the `RewardGoods` ids of its reward. One id applies to every pack in the stack, or send one id per pack. Every authored choice pack asks for one reward. One copy uses `ItemUseRequest`. A stack of a one-choice pack is what the bag sends as `ItemUseMultipleRequest`: one entry per pack, each with that pack's `RewardGoods` id. Open the pack from Items after granting it. Direct Serum (4) and Cogs (1) remain direct grants. These packs do open:

| ID | Name | Opens into |
| --- | --- | --- |
| 90001 | Serum Bundle (S) | 30 Serum |
| 90002 | Serum Bundle (M) | 60 Serum |
| 90003 | Serum Bundle (L) | 120 Serum |
| 90011 | Cog Pack (S) | 10,000 Cogs |
| 90012 | Cog Pack (M) | 20,000 Cogs |
| 90013 | Cog Pack (L) | 50,000 Cogs |
| 90014 | Cog Pack (XL) | 100,000 Cogs |
| 90015 | Cog Pack (XXL) | 200,000 Cogs |
| 60001 | Overclock Material Box (α) | Random low-grade overclock material |
| 60002 | Overclock Material Box (β) | Random high-grade overclock material |
| 40691 | Normal Gift Box | Random normal gift |
| 40692 | Fine Gift Box | Random fine gift |
| 40693 | Precious Gift Box | Random precious gift |
| 400076, 400078–400082 | Tactical Assessment Manual Weapon Coating Choice I–VI | One weapon coating from that pack's reward |
| 40913 | Accumulated Top-up Weapon Coating Choice | One weapon coating from reward 1306 |
| 40905–40908, 40914–40916, 40918–40925 | Assessment Manual Shard Choice | One inver-shard from that pack's reward |
| 40904 | Character Upgrade Material Pack | 6★ shards, overclock mats, enhancers, EXP Pod (XL), Skill Points, Cogs |
| 40909 | Memory Upgrade Material Pack | Memory upgrade set |
| 40910 | Weapon Upgrade Material Pack | Weapon upgrade set |
| 94008 | S-Rank Omniframe Choice | One of Luminance, Entropy, Ember, Tenebrion, Pulse |
| 94030 | S-Rank Omniframe Pick | One of nine S-rank omniframes |
| 40901 | S-Rank Character Inver-Shard Pick | One S-rank inver-shard from the pack's list |

## Gifts

Everyone accepts 40681 Best-Seller, 40682 Vinyl Record, 40683 Ration Chocolate, and 40684 1 Day Tour Tickets x2. Favorites are in `CharacterTrustItem`:

| ID | Gift | For |
| --- | --- | --- |
| 40601 | Frog Accessory | Lucia |
| 40602 | Biosphere | Liv |
| 40603 | Precision Toolkit | Lee |
| 40604 | Jack-in-the-box | Nanami |
| 40605 | Prototype Engine | Karenina |
| 40606 | Portable Game Console | Kamui |
| 40607 | MRE Rations | Watanabe |
| 40608 | Lily Brooch | Bianca |
| 40609 | Painting Kit | Ayla |
| 40610 | High-Power Batteries | Sophia |
| 40611 | R3 Cleaning Robot | Chrome |
| 40612 | Military-Grade Bionic Dog | Vera |
| 40613 | Media Player | Camu |
| 40614 | Weather Glass | Rosetta |
| 40615 | Antique Book | Qu |
| 40616 | Copper Coin | Changyu |
| 40617 | Night Light | Luna |
| 40618 | Lunar Tear | 2B, 9S, A2 |
| 40619 | Hugging Pillow | Wanshi |
| 40620 | Iris | Selena |
| 40621 | Audiovisual Terminal | No. 21 |
| 40622 | Marionette | Roland |
| 40623 | Panda Change Purse | Pulao |
| 40624 | Deluxe Picture Book | Haicma |
| 40625 | Notebook and Pen | Noan |
| 40626 | Music Box | Bambinata |
| 40627 | Cloud Comb | Hanying |
| 40628 | Ironclad Boxing Gloves | Noctis |
| 40629 | Gavel | Alisa |
| 40630 | Coffee Cup | Lamia |
| 40631 | Star Accessory | BLACK★ROCK SHOOTER |
| 40632 | "Teddy" | Teddy |
| 40633 | Tactical Sunglasses | Bridget |
| 40634 | Hydrangea Bouquet | Yata |
| 40635 | Copperwood Dice | Ishmael |
| 40636 | Cardistry Poker | Lilith |
| 40637 | "White Box" | Jetavie |
| 40638 | Poetry Collection | Vergil |
| 40639 | Pizza Combo | Dante |
| 40640 | Scissors | Discord |
| 40641 | Weapon Charm | Veronica |
| 40642 | "Morigan" Game Console | Nirvatia |
| 40643 | Vintage Notebook | Helentine |
| 40644 | Black Forest Cake | Kurumi Tokisaki |
| 40645 | A Testament to Triumph | Adelyde |

## Construct shards

Inver-Shards cap at 999. A-rank recycle into item 34, which shop 601 spends. S-rank and Uniframe recycle materials (35 and 48) have no goods list in the shop catalogs, and extra shards are not converted into them. Shop 6001 spends Trade Vouchers (102). It is not an item-35 shop.

| ID | Frame | ID | Frame |
| --- | --- | --- | --- |
| 501 | Palefire | 549 | Empyrea |
| 502 | Lotus | 550 | Capriccio |
| 503 | Eclipse | 551 | Dragontoll |
| 504 | Zero | 552 | Starfarer |
| 505 | Storm | 553 | Starveil |
| 506 | Bastion | 554 | Scire |
| 507 | Blast | 555 | Arca |
| 508 | Nightblade | 556 | Stigmata |
| 509 | Brilliance | 557 | Vitrum |
| 512 | Dawn | 558 | Hyperreal |
| 513 | Lux | 559 | Kaleido |
| 514 | Veritas | 560 | Crimson Weave |
| 515 | Pulse | 561 | Zitherwoe |
| 516 | Tenebrion | 562 | Feral |
| 517 | Ember | 563 | Indomitus |
| 521 | Entropy | 564 | Echo |
| 522 | Crimson Abyss | 565 | Lost Lullaby |
| 523 | Luminance | 566 | BLACK★ROCK SHOOTER |
| 528 | Astral | 567 | Epitaph |
| 531 | Silverfang | 568 | Shukra |
| 532 | Plume | 569 | Decryptor |
| 533 | Rozen | 570 | Oblivion |
| 534 | Crocotta | 571 | Ardeo |
| 535 | Rigor | 572 | Solacetune |
| 536 | Pavo | 573 | Lucid Dreamer |
| 537 | Qilin | 574 | Pyroath |
| 538 | Laurel | 575 | Fulgor |
| 539 | Arclight | 576 | Startrail |
| 540 | 2B | 577 | Parhelion |
| 541 | 9S | 578 | Daemonissa |
| 542 | A2 | 579 | Pianissimo |
| 543 | Hypnos | 580 | Daybreak |
| 544 | Tempest | 581 | Geiravor |
| 545 | Glory | 582 | Vergil |
| 546 | XXI | 583 | Dante |
| 547 | Garnet | 584 | Crepuscule |
| 548 | Flambeau | 585 | Secator |
| 586 | Aegis | 587 | Limpidity |
| 588 | Spectre | 589 | Arete |
| 590 | Dirge | 591 | Aeternion |
| 592 | Inverse Crown | 593 | Lacrimosa |
| 594 | Effulgence | 595 | Kurumi Tokisaki |
| 596 | Anabasis | | |

## CUB parts

Booster Structural Parts cap at 9,999.

| ID | CUB | ID | CUB |
| --- | --- | --- | --- |
| 201 | Yuan Ye | 223 | Snowveil |
| 202 | Noctua | 224 | Corvus |
| 203 | Toniris | 225 | Bramble Angler |
| 204 | Frost Oath | 226 | Diamaton |
| 205 | Seeshell | 227 | Billie |
| 206 | Nitor | 228 | Snow Waltz |
| 207 | Lingya | 229 | Wrathfang |
| 208 | Boreas | 230 | Mirage Blades |
| 209 | Thorny | 231 | Cavaliere |
| 210 | Jet Jaeger | 232 | Noctiluca |
| 211 | Moonhopper | 233 | Scaled Rampart |
| 212 | Shimmer | 234 | Levvi |
| 213 | Punchy | 235 | Beep-Boop |
| 214 | Rainbow | 236 | Buzzling |
| 215 | Motorbolt | 237 | Morigan |
| 216 | Hades Fangs | 238 | Ignis |
| 217 | Dawn Chorus | 239 | Allos |
| 218 | Cetus | 240 | Grand Duke |
| 219 | Shadow Wing | 241 | Patrick |
| 220 | Huiyu | 242 | Zafkiel |
| 221 | Guardrake | 243 | Kelpie |
| 222 | Dreamwing | | |
