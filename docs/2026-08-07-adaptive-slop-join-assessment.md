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
attribution (same sample-based census semantics).

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
