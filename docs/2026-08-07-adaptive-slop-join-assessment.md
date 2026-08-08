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
```

The analyzer file is `tools/analyze-trace.fsx`. It reproduces all tables in
this document from a new capture.

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
