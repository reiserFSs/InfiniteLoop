# Draw rule provenance

Per the maintainer's corrected instruction on 2026-09-10, client tables take
precedence over the initial comparison table. DrawProbShow.tsv is read directly:
normal characters 0.5%, Fate 1.5%, weapons 5% total (targeted 4% plus two 0.5%
off-targets), Uniframes 5%, CUBs 5.82%.
DrawServerRule.tsv transcribes guarantees and fallback target rates from DrawGroupRule.tsv.
Per-banner target rates come from DrawAimProbability.tsv, so banners in one group
may retain different rates. Arrival and targeted weapons calibrate after an off-target rare.
Fate limits are sampled inclusively from 80 to 100 once per round. The client
specifies the range, 1.5% base rate, and equality with the corresponding normal
pool's combined rate, but does not expose the server's threshold weights. The
emulator therefore uses the unique maximum-entropy full-support distribution
that satisfies those published constraints; weights are derived at runtime from
DrawGroupRule, DrawProbShow, and DrawServerRule rather than captured values.
Member's initial limit is 40, then 60. Target percentages apply conditional on
obtaining the highest rarity, not as additional independent rolls.

DrawGroupRule.tsv supplies group identities. DrawPreview supplies eligible
rewards (both GoodsId and UpGoodsId). Uniframes use the client's 100% target rule.
CUB previews contain the selected S and lower-rarity CUBs, so S targets are 100%.
No calibration is invented for
CUBs/Uniframes where the supplied rules leave it version dependent.
Crucible groups 35/36 retain their separate client-defined inheritance groups;
they are not assumed to be Phylotree Nexus. No Phylotree identity or Checked S
selection/remaining-use source is currently provided by the server catalog.
Those mechanisms require explicit catalog/selection data before activation.

Legacy lifetime counters remain intact for activity progress. A new persisted
round stores misses, sampled limit and calibration independently. Migration
preserves the legacy partial cycle; historical early S resets cannot be fully
reconstructed from the old bounded history.
