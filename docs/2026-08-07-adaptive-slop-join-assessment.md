# AdaptiveSlop Join Assessment

**Date:** 2026-08-07
**Status:** Analysis only. No code changes.
**Scope:** The per-enemy reactive join in the Enemies system.

---

## 1. Summary

The reactive join works. It costs little time at 60 FPS. It does allocate memory
per frame. The allocation scales with the number of enemies. This document
explains the cost, the trend, and the options to flatten the join.

## 2. Measured Facts

We captured two traces of the running game with `dotnet-trace`.

| Fact | Value |
| --- | --- |
| Game CPU busy time | 0.75 % of wall time |
| AdaptiveSlop busy time | 64.9 % of busy time (0.49 % of wall time) |
| Game CPU per frame | ~0.13 ms |
| AdaptiveSlop CPU per frame | ~0.08 ms |
| Memory allocated per frame (wave active) | ~11 KB |
| Memory allocated per frame (settled reads) | 0.3 B |

The 64.9 % value looks large. It is large only inside the busy slice. The game
is idle 99.25 % of the time. The values are not visible to the player.

## 3. Why the Join Costs Time

The join has this shape:

```
AliveCount (AMap.count)
  -> Alive (AMap.filter)
    -> Views (AMap.mapA join)
      -> per enemy: tryFind Healths + tryFind Motions + map2
```

Enemy positions change every frame. Every change bumps the source version.
Every read of the chain re-evaluates the chain. The router reads AliveCount
every frame. The view reads Alive and Views every frame. Alive is read twice
per frame. This doubles the drain work.

The cost is linear in enemies. It is not exponential.

## 4. Where the Memory Goes

The per-enemy node recompute path allocates arrays. The trace shows all
sampled array allocations inside these nodes:

- `tryFind Healths` nodes
- `tryFind Motions` nodes
- the `map2` join node

This is a library behavior. It happens only when the source changes. It is
~1.2 KB per enemy per frame. At 300 enemies this becomes ~360 KB per frame.
This is the first thing that would become visible at scale.

## 5. Flattening Options

| Option | Effect | Trade-off |
| --- | --- | --- |
| Coarse rows | Fewer maps = fewer tryFind nodes | Less component separation |
| Read each derived node once per frame | Removes the Alive double-read | None; this is the library's documented shape |
| Compute count from the frame's transient rows | Removes the count node and one chain level | Loses the reactive aggregate |
| Force + FrozenDictionary once per frame | Retainable artifacts | ~1.7 KB per force, no clean shortcut |
| Transient reads | 0 B, no copy | Valid only until the next write |

## 6. Consumer Guidance for the View

The same-thread shape has a clear best practice:

1. Read each derived node once per frame.
2. Consume transient views within the frame.
3. Never write while iterating a transient view.
4. Use `force` only when the artifact must be retained.
5. Do not stack aggregates on aggregates on the per-frame path.

Kimo's pattern (version cache + `force`) is the right choice when the render
side must retain artifacts or runs on another thread. In this same-thread
shape, transient reads are strictly cheaper.

## 7. Viability Verdict

AdaptiveSlop is viable for this game and for games of this scale.

- At 60 FPS, the current cost is not measurable by the player.
- The cost grows linearly with entities.
- The library's zero-allocation claim holds for settled graphs.
- The claim does not hold for per-element nodes over sources that change
  every frame. This is the known risk at scale.

The project stays on the current code style. The join is the correct shape.
The measured costs are within the frame budget with large margin.

## 8. Reproduction

```
dotnet-trace collect --profile gc-verbose --name Defli
dotnet-trace convert trace.nettrace --format Speedscope -o trace.speedscope
dotnet fsi tools/analyze-trace.fsx trace.speedscope.json
dotnet fsi tools/analyze-subtree.fsx trace.speedscope.json
```

The analyzer file is `tools/analyze-trace.fsx`. It reproduces all tables in
this document from a new capture. `tools/analyze-subtree.fsx` adds the
microscope pass: per-depth inclusive tables and per-frame subtree
attribution (same sample-based census semantics). `tools/probe-sinks.fsx`
adds the write-path census: per-30 s buckets of `pushMapDelta`/tick/
OnDeltas samples (growth check) and per-frame open tallies for the
changeable-map write path.

## 9. Phase-3 Follow-up (start → wave ~11, 2026-08-07)

A 247.7 s capture of the Phase-3 build (flier archetype, targeting
policies, wave director).

| Fact | Value |
| --- | --- |
| Game CPU busy time | 0.9 % of wall time (2.2 s of 247.7 s) |
| AdaptiveSlop busy time | 53.6 % of busy time (0.5 % of wall time) |
| AdaptiveSlop samples per second | ~4 /s — flat vs every earlier capture |
| AdaptiveSlop CPU per frame | ~65 µs |
| Longest CPU-busy run | 5.6 ms (startup) — no CPU-side hitches exist |
| zeroCreate samples (per-element nodes) | ~296 across the session |
| String samples: AssetsService.Texture / HUD sprintf | 105 / 25 |

Reading:

- The 53.6 % "dominance" is a quiet-session ratio artifact: with few
  enemies the adaptive chain is almost the only busy work. The absolute
  cost is unchanged (~4 samples/s, ~0.5 % of wall). The reactive data is
  NOT the constraint — not yet.
- The allocation drip is the item to watch: ~296 sampled array
  allocations, all on the per-element recompute path (`tryFind` nodes
  for Motion/Positions/Homing/Healths + `EnemyView` recompute). Linear
  in entities; the predicted curve holds.
- The view path still builds strings per frame (asset-texture lookups,
  HUD formatting) — housekeeping, not a reactive cost.

Hitch investigation (same session):

- The player saw worst-ms spikes around tick ~6000. The sampled trace
  cannot see them: gaps between samples are wall-clock sampling noise of
  a 0.9 %-duty thread (the gap distribution matches the geometric model
  exactly), and no busy run exceeds 5.6 ms — so the hitches are native
  blocks, invisible to this instrument by construction. The before/after
  stacks show no operation pattern.
- A run WITHOUT the trace collector attached was smooth — the collector
  (EventPipe attach/backpressure) was the cause, not the game. The F3
  worst-ms column is the ground-truth frame-time indicator.

Verdict: keep an eye on the allocation rate as entity counts grow, but
nothing is near the frame budget. We are not dead in the water.

## 10. Phase-4 Trace (start → wave ~10, 2026-08-08)

A 201.3 s capture of the Phase-4 build (camera sub-system, HUD
renderer, frost tower, keyboard pan). Same method, same census.

| Fact | Value |
| --- | --- |
| Game CPU busy time | 1.26 % of wall (2.54 s — 0.21 ms/frame) |
| AdaptiveSlop busy time | 49.5 % of busy (0.62 % of wall — 0.10 ms/frame) |
| GC frames in the busy profile | 0 (no PollGC/WriteBarrier samples) |
| Towers.tick (NEW #1 consumer) | 21.9 % of busy (0.28 % of wall) |
| Alive-count node pull (World.fs RoomTick) | 17.8 % of busy |
| Renderer2D (both passes) | 18.8 % of busy |
| String samples (AssetsService.Texture per frame) | 49 (1.9 %) |

Microscope (per-frame subtree attribution):

- Towers.tick is half adaptive machinery: ~13 % of busy is node
  GetValue/Recompute plus ~3 % of NODE CONSTRUCTORS on the tick path.
  Source-verified: `AMap.tryFind` is `new AdaptiveNode(...)` — a fresh
  node per call. The per-tower-per-tick tryFind pattern rebuilds nodes
  every frame; each GetValue re-reads the map and re-allocates. This
  is the assessment's allocation drip, now on the Towers hot path.
- The Alive-count node (`Alive |> AMap.count |> AVal.getValue`,
  RoomTick) is still pulled per frame: 17.8 % of busy, including the
  whole Alive chain drain (ElementMapNode 16.6 %, buildViews 11.1 %).
  This is lever #1 from §5 — the transient `.Count` flattening is the
  documented fix and is not in place here.
- Homing join: 8.5 % of busy, pulled by Projectiles.view — linear in
  projectiles, expected.
- Enemies.tick itself (the real sim work): 1.3 % of busy.

### 10.1 Does more busy work dilute the adaptive share?

Intuitively yes — and the busy-share numbers look like it (64.9 % →
49.5 % as the game got busier). But this is a MIX EFFECT, not an
improvement:

| Capture | Busy (% wall) | AdaptiveSlop busy-share | AdaptiveSlop wall-share |
| --- | --- | --- | --- |
| Phase 1 (37 s) | 0.75 % | 64.9 % | 0.49 % |
| Wave 28 (60 s) | 6.1 % | 5.3 % (flattening REFACTOR) | 0.30 % |
| Start→wave 11 | 0.8 % | 46.9 % | 0.40 % |
| Phase 3 (247 s) | 0.9 % | 53.6 % | 0.50 % |
| Phase 4 (201 s) | 1.3 % | 49.5 % | 0.62 % |

Facts:

- The busy-share is a ratio of mixes: "of the CPU I burn, how much is
  adaptive". When the game is busier for OTHER reasons (bigger waves:
  more sprite commands, more sim rows), the same absolute adaptive
  cost is a smaller fraction. The ratio flattens by arithmetic.
- The absolute wall-share — the metric that matters — is FLAT at
  ~0.5 % with a slight upward drift (0.49 → 0.62 %) as the game gained
  projections. It did not flatten toward zero.
- The one big drop (wave 28: 64.9 % → 5.3 % of busy) was the
  transient-read flattening refactor (§5 lever #1), not natural
  dilution.
- Mechanism: adaptive cost is linear in the elements of sources that
  change per frame. Rendering and sim are also linear in entities but
  with a bigger constant (draw commands, sorting). At larger entity
  counts the adaptive fraction shrinks RELATIVELY while the absolute
  ms/frame stays identical.

Consequence: "more busy work dilutes the share" is true but does not
answer "will adaptive data bite us at scale". The answer there is
still the linear growth + the per-element allocation drip. Watch the
absolute ms/frame and the allocation samples per entity — not the
ratio.

## 11. Phase-5 Trace (waves 11 → 21, 2026-08-08)

A 321.4 s capture of the Phase-5 build (procedural map, decorations,
tower upgrades, difficulty tiers 2–4). Same method, same census. The
file carries two threads — 12 ms of EventPipe-attach noise plus the
game thread; the subtree tool aggregates all profiles.

| Fact | Value |
| --- | --- |
| Game CPU busy time | 6.7 % of wall (21.4 s — 1.11 ms/frame) |
| AdaptiveSlop busy time | 61.5 % of busy (4.1 % of wall — 0.68 ms/frame) |
| GC frames in the busy profile | 0 (the drip is F# array ZeroCreate/Create in node recomputes) |
| Towers.tick | 30.9 % of busy (2.05 % of wall) — #1, grew from 21.9 % |
| Homing join | 18.6 % of busy (1.24 % of wall) — grew from 8.5 % |
| Alive-count node pull (RoomTick) | 11.5 % of busy |
| Alive/Views join | 11.1 % of busy |
| Renderer2D (both passes) | 20.8 % of busy |

Microscope (per-frame subtree attribution):

- Towers.tick decomposition: 10.8 % own code, 6.8 % `AdaptiveNode<
  Single>.GetValue` (cooldownA), and ~4 % (854 samples) node
  CONSTRUCTORS on the tick path — the per-tower-per-tick `AMap.tryFind`
  pattern builds fresh nodes every frame (source-verified: `tryFind`
  is `new AdaptiveNode` per call). Runtimes is written for EVERY tower
  every frame (cooldown decay), so every tower's chains re-run every
  frame.
- The Alive-count node (`Alive |> AMap.count |> AVal.getValue`,
  RoomTick) is still pulled per frame (11.5 % of busy) — lever #1
  from §5 remains unapplied here.
- Homing grew 2.2×: more towers (upgrades phase) + longer-lived
  enemies (tier 2–4 HP = 2.6–4.1×) → more shots in flight.

### 11.1 The trend across all seven captures

| Capture | Busy (% wall) | AdaptiveSlop busy-share | **AdaptiveSlop wall** | ms/frame (busy) |
| --- | --- | --- | --- | --- |
| Phase 1 | 0.75 % | 64.9 % | 0.49 % | 0.13 |
| Wave 28 | 6.1 % | 5.3 % (flattened) | 0.30 % | 1.0 |
| Start→11 | 0.8 % | 46.9 % | 0.40 % | 0.13 |
| Phase 3 | 0.9 % | 53.6 % | 0.50 % | 0.15 |
| Phase 4 (→10) | 1.3 % | 49.5 % | 0.62 % | 0.21 |
| Phase 5 (→21) | 6.7 % | 61.5 % | 4.1 % | 1.11 |

Reading: the §10 prediction materialized — absolute wall share jumped
6.6× (0.62 % → 4.1 %) once the alive set grew (tier-scaled HP) and
the tower count grew. Two drivers: the linear-in-entities per-element
chains (Views/Homing/Alive) with a larger alive set, and the Towers
per-tick `tryFind` node construction. 61.5 % of busy is the highest
share recorded — this time the ratio is honest, not a mix artifact.
Still far from the frame budget (6.7 % of 16.7 ms), but the growth
rate is exactly the linear curve §1 predicted.

## 12. 2026-08-09 Trace A — per-key scalar escapes (#16)

The AdaptiveSlop submodule advanced to `2290cef` (feat: per-key/
per-position precise scalar escapes, #16): `AMap.tryFind`/`count` and
friends are no longer `AdaptiveNode` over whole-collection `GetValue`
reads — they are delta-sink nodes that scan each delta for the watched
key and advance their own version only when the watched output actually
moved. The game code did not change (`AMap.tryFind`/`AMap.count` calls
are identical). Same method, same census. A 368.7 s capture of the
Phase-5 build (waves 10 → game over, ~wave 2X),
collected after a warm-up run.

| Fact | §11 capture (waves 11→21) | Trace A (waves 10→end) |
| --- | --- | --- |
| Game CPU busy time | 6.7 % of wall (1.11 ms/frame) | 17.1 % of wall (2.86 ms/frame) |
| AdaptiveSlop busy share | 61.5 % of busy | 71.5 % of busy |
| AdaptiveSlop wall share | 4.1 % (0.68 ms/frame) | 12.2 % (2.04 ms/frame) |
| Towers.tick | 30.9 % of busy (0.34 ms/frame) | 47.2 % of busy (1.35 ms/frame) |
| `Collections.pushMapDelta` (write dispatch) | — (absent from top frames) | 61.4 % of busy (10.5 % wall) — #1 |
| `MapLookupNode<TowerRuntime>.OnDeltas` | — (new node type) | 42.6 % of busy |
| `MapLookupNode<Motion>.OnDeltas` | — (new node type) | 18.1 % of busy |
| Alive-count chain (RoomTick pull) | 11.5 % of busy | ~5.1 % (MapCountNode 2.7 % incl. its filter pull) |
| Homing join (Projectiles) | 18.6 % of busy | 2.7 % of busy |
| zeroCreate/Create samples | ~3 393 (10.6/s) | 1 052 (2.9/s) |
| Node constructors on the tick path | 854 samples | none sampled |

### 12.1 What the per-key differentiation fixed

- **The allocation drip (§11's watch item) is 3.6× lower** (10.6 → 2.9
  sampled array allocs/s) even in a busier session. The remaining
  `zeroCreate` leaves are the genuinely-changed `AdaptiveNode.Recompute`
  calls (cooldownA, the mapA join transforms), not the per-frame
  `tryFind` node construction — the 854 constructor samples are gone.
- **The Alive-count pull is ~2.3× cheaper.** `MapCountNode` now bumps
  only when the count itself changes (an in-place HP write does not),
  instead of re-running the whole `Alive` filter chain per write.
  Lever #1 from §5 is now effectively applied library-side.
- **The Homing join dropped 7×** (18.6 → 2.7 % of busy): its per-
  projectile `tryFind`s are O(1) cached lookups instead of whole-map
  re-reads with re-allocation.

### 12.2 What regressed — the write-side fan-out

The cost moved from read-time to write-time, and in this game's usage
pattern that is a 3× net regression:

- `pushMapDelta` is now the #1 consumer at 61.4 % of busy — it did not
  exist in any previous top frame. Every `AddOrUpdate`/`Remove`
  dispatches the delta to **every registered sink** of that map.
- Towers.tick went 0.34 → 1.35 ms/frame; 42.6 % of busy is
  `MapLookupNode<TowerRuntime>.OnDeltas` + its
  `FSharpValueOption<TowerRuntime>.Equals` equality gate. The
  per-tower-per-frame `AMap.tryFind` churn (Towers.fs:125, inside the
  per-tower loop) registers a fresh node per tower per frame; the
  per-tower `CMap.addOrUpdate` (Towers.fs:168/176/185, one per tower
  per frame) then dispatches to all of them.
- The Motion map repeats the shape: Enemies.fs:277-285 writes Motion
  for every enemy every frame, while the `buildViews` mapA transform
  re-runs per enemy per frame (positions change every frame) and
  creates fresh `tryFind` nodes → 18.1 % of busy in the Motion
  dispatch + equality gate.
- The `GetValue`/`Register`/`AddMapSink` read side is nearly free now
  (98 samples) — the lookup reads are O(1). The cost is purely the
  per-(write × sink) dispatch.

The library's promise ("a write to an unrelated key costs this node
and its consumers nothing") is true **per node**: one lookup node is
O(1) per unrelated write. The aggregate is O(writes × live sinks),
and the game's per-frame node churn multiplies the live-sink count by
the GC interval (weak refs die only when the GC actually collects;
compaction happens at the next delivery/registration). Observed
delivery fan-out is on the order of 10–40× the entity count (derived
from 1.22 ms/frame of TowerRuntime dispatch at ~100 ns/delivery).

### 12.3 The session is honest, not a mix artifact

This time the ratio means what it says: the game is 2.5× busier than
Phase 5 (end-game waves, larger alive set) and AdaptiveSlop still grew
its share (61.5 → 71.5 %). The per-30 s buckets (`tools/probe-sinks.fsx`)
show `pushMapDelta` at 59–68 % of every bucket from the first (wave 10,
mid-game load comparable to Phase 5) — the dispatch dominates even at
moderate load, and it stays flat (no super-linear growth within the
session): the cost scales with the write volume, with a much larger
constant than before.

### 12.4 Verdict and next steps

Not dead in the water — 2.04 ms/frame of adaptive work is still 8×
under the frame budget — but the per-key refactor moved the cost to a
place this game pays heavily: **the per-frame `tryFind` node churn**.
The library's design intent is "register once, read many"; the game
registers per entity per frame. Two game-side fixes, in impact order:

1. **Stop the churn (the big one).** Hoist `AMap.tryFind` out of the
   per-frame loops: one cached lookup node per tower id (built at
   Place, stored on the model) and per enemy id inside `buildViews`
   (memoized per eid). This removes the GC-interval multiplier from
   the sink lists — the fan-out drops to O(writes × live entities).
2. **Batch the per-tower Runtimes writes** into one `Transaction.run`
   per frame (one delta instead of T per frame) — removes the
   per-write delivery loop overhead (the Enemies movement writes are
   already per-enemy `addOrUpdate`; a single transaction would batch
   those too).

If the churn cannot go away, the library-side lever that matches the
commit's intent is a **key-indexed sink dispatch**: a per-key sink
list so a delta dispatches only to sinks watching keys present in the
delta — then per-frame dispatch is O(writes × sinks-per-key) instead
of O(writes × all-sinks).

Trend across all captures (absolute, the metric that matters):

| Capture | Busy (% wall) | Adaptive busy-share | Adaptive wall | Adaptive ms/frame |
| --- | --- | --- | --- | --- |
| Phase 1 | 0.75 % | 64.9 % | 0.49 % | 0.08 |
| Wave 28 (flattened) | 6.1 % | 5.3 % | 0.30 % | 0.05 |
| Start→11 | 0.8 % | 46.9 % | 0.40 % | 0.07 |
| Phase 3 | 0.9 % | 53.6 % | 0.50 % | 0.08 |
| Phase 4 | 1.3 % | 49.5 % | 0.62 % | 0.10 |
| Phase 5 | 6.7 % | 61.5 % | 4.1 % | 0.68 |
| Trace A (#16 delivery) | 17.1 % | 71.5 % | 12.2 % | 2.04 |

The curve is steeper than §1's linear prediction: the per-frame node
churn × write fan-out is a quadratic-in-entities term that the old
read-side design did not have. The allocation drip is fixed; the
dispatch fan-out is the new watch item — and unlike the drip, it is
CPU time, not garbage.

## 13. 2026-08-09 Trace B — lazy scalar escapes (PR #17)

The submodule advanced to `9bc0d9a` (feat: lazy scalar escapes —
version-bump writes, read-time gate): the scalar escapes no longer
register as delta sinks; a write is a version bump (O(1)) and the
per-key gate runs at the node's next read. Same method, same census.
A 208.9 s capture of the Phase-5 build, waves 11 → 16 (the player lost
earlier this run), game code unchanged.

| Fact | Trace A (delivery) | Trace B (lazy) |
| --- | --- | --- |
| Game CPU busy time | 17.1 % of wall (2.86 ms/frame) | 2.0 % of wall (0.33 ms/frame) |
| AdaptiveSlop busy share | 71.5 % of busy | 42.4 % of busy |
| AdaptiveSlop wall share | 12.2 % (2.04 ms/frame) | 0.84 % (0.14 ms/frame) |
| `pushMapDelta` / lookup OnDeltas | 61.4 % of busy | 0 (MapLookupNode total 0.5 %) |
| Towers.tick | 47.2 % of busy (1.35 ms/frame) | 12.3 % (0.04 ms/frame) |
| Enemies.tick | 18.8 % of busy | 3.9 % |
| Renderer2D | 4.0 % of busy | 27.7 % (mix effect in reverse) |
| zeroCreate samples | 1 052 (0.048 ms/frame) | 343 (0.027 ms/frame) |

Microscope (per-frame subtree attribution):

- **The write-side dispatch is gone.** `pushMapDelta` and
  `MapLookupNode.OnDeltas` do not appear in the profile at all
  (MapLookupNode totals 0.5 % — a few `Resync` reads). Towers.tick's
  children are now the real sim: target acquisition (7.2 %), cooldownA
  `GetValue` (1.7 %), target `GetValue` (1.2 %) — no AddOrUpdate
  machinery under it.
- The remaining adaptive cost is the READ side, now the expected
  pre-#16 shape with O(1) per-key reads: Views join drain
  (`ElementMapNode<Vector2, EnemyView>` 13.3 %), Homing join drain
  (15.8 %), the AliveCount chain pull (`MapCountNode.Resync` 13.1 % →
  `FilterMapNode` 13.4 % → the Views drain — RoomTick reads it every
  frame), and the genuinely-changed `AdaptiveNode.Recompute`s (~12 %,
  cooldownA/targetA/EnemyView/HomingView — the §4 drip, now gated).
- Allocation samples all sit on those changed recomputes — 343 over
  the session, 0.027 ms/frame.

Reading:

- The §12 prediction materialized exactly: removing the eager delivery
  collapsed the game's CPU 8.6× (2.86 → 0.33 ms/frame) and the
  adaptive wall share 14.6× (12.2 % → 0.84 %). The absolute adaptive
  cost (0.14 ms/frame) is back in the Phase 1–4 regime (0.08–0.10)
  while the entity counts are Phase 5+.
- Session caveat: waves 11–16 (mid-game) vs Trace A's end-game run —
  part of the drop is load. The structural evidence is load-
  independent: the dispatch frames do not exist in this profile, and
  Towers.tick's children are sim work, not delivery.
- The busy-share is now a mix artifact in the GOOD direction:
  rendering is 27.7 % of busy because the adaptive tax collapsed —
  absolute rendering roughly halved (1 155 vs 2 540 samples).

### 13.1 The trend across all eight captures

| Capture | Busy (% wall) | Adaptive busy-share | Adaptive wall | Adaptive ms/frame |
| --- | --- | --- | --- | --- |
| Phase 1 | 0.75 % | 64.9 % | 0.49 % | 0.08 |
| Wave 28 (flattened) | 6.1 % | 5.3 % | 0.30 % | 0.05 |
| Start→11 | 0.8 % | 46.9 % | 0.40 % | 0.07 |
| Phase 3 | 0.9 % | 53.6 % | 0.50 % | 0.08 |
| Phase 4 | 1.3 % | 49.5 % | 0.62 % | 0.10 |
| Phase 5 | 6.7 % | 61.5 % | 4.1 % | 0.68 |
| Trace A (#16 delivery) | 17.1 % | 71.5 % | 12.2 % | 2.04 |
| Trace B (lazy, PR #17) | 2.0 % | 42.4 % | 0.84 % | 0.14 |

The curve is back on the linear, sub-0.2 ms regime. The game's CPU is
~42 % adaptive read-side, ~28 % rendering, ~16 % sim, ~8 % input,
rest noise — 50× under the frame budget at waves 11–16. The write
fan-out regression is closed; the remaining adaptive cost is the
inherent per-frame read-side chain (positions change every frame →
the joins re-run), which is the library's documented behavior and
cheap enough that the §5 transient-count lever is no longer worth
pulling.
