# Defli

A 2D tower-defense prototype built on **Mibo** (Elmish MVU, raylib-cs) with
**[AdaptiveSlop](https://github.com/AngelMunoz/AdaptiveSlop)** as the game-state
modeling layer — adaptive maps (`cmap`/`amap`/`aval`) with FRP-style derived
projections (`mapA`/`filter`/`chooseA`/`count`/`tryFind`) instead of
hand-wired mutation and event plumbing.

The project doubled as an evaluation of that question: **can adaptive/FRP
data serve as the state layer of a real game without falling into
performance chasms or retreating to procedural techniques?** The answer,
after nine profiler captures across six development phases, is yes — inside
a boundary this document maps out.

Full measurement details:
[docs/2026-08-07-adaptive-slop-join-assessment.md](docs/2026-08-07-adaptive-slop-join-assessment.md).

## How the architecture fits

- **Routed sub-systems** (Enemies / Towers / Projectiles / Waves / Spawning /
  Vfx / Camera), each owning its state as changeable adaptive maps. The root
  update is a router; systems communicate by events/intents, never by calls.
- **Cross-system data flows through projections only.** No system reads
  another's maps directly; the world owns derived chains, e.g.
  `Views = Motions × Healths`, `Alive = Views |> filter`, `HomingView =
  Projectiles × Positions`, `Suppression = Towers.Statics × BossPositions`.
- **Hot loops consume per-frame snapshots.** `Towers.tick` receives `Alive`
  as a direct value; `Projectiles.tick` gets `Positions |> AMap.getValue`.
  Adaptive maps own the logic graph; the data plane (movement, rendering
  feed) runs on plain values.

## What adaptive data bought us (the correctness argument)

- **No staleness bugs by construction.** Derived state (`AliveCount`,
  suppression auras, homing targets) is a projection, not a cached field —
  there is no "forgot to update the other copy" failure mode. Features like
  the Phase-6 boss aura were added as one projection line
  (`Statics × BossPositions`) with zero changes to the systems producing
  the inputs.
- **FRP discipline survived contact with a real game.** The codebase never
  retreated to imperative invalidation, dirty flags, or pub-sub plumbing to
  make a feature work or a frame budget hold. Every performance problem
  encountered was fixed in the *library's evaluation strategy*, not by
  abandoning the model.
- **Change-gating is real.** Settled graphs cost ~0 to read; scalar escapes
  (`tryFind`, `count`) are O(1) per unrelated write (lazy per-key gates,
  AdaptiveSlop PR #17).

## Findings as we stressed it

Nine captures, Phase 1 → Phase 6 end-game (waves 14→32, boss waves — the
heaviest session: 591 s, ~150 concurrent entity rows):

| Regime | Result |
| --- | --- |
| Phase 1–4 (early game) | Adaptive cost 0.08–0.10 ms/frame — invisible |
| Phase 5 (bigger waves, more towers) | 0.68 ms/frame — the predicted linear growth |
| Eager delta delivery (lib #16) | **Regression:** write-side fan-out O(writes × sinks), 2.04 ms/frame — found by profiling, fixed library-side |
| Lazy scalar escapes (lib #17) | 0.14 ms/frame mid-game — regression closed, writes are O(1) version bumps |
| Phase 6 end-game | **0.45 ms/frame adaptive, 1.0 ms/frame total CPU — ~17× headroom** at the game's practical ceiling |

Constants measured: **~3 µs per changed row per frame** of chain drain,
**~1.2 KB allocated per changed element recompute**, both linear in entity
count across all nine captures — no quadratic term in the current (lazy)
design.

The two failures we did find:

1. **Eager write fan-out (#16)** — a library design that dispatched every
   write to every sink multiplied cost by per-frame node churn. Caught by
   the profiler (71.5 % of busy, `pushMapDelta` at #1), fixed by lazy
   per-key gates (#17). Lesson: profile the absolute ms/frame, not ratios.
2. **The stale-read footgun (rule 12).** Lazy settle is bottom-up: a
   tail-only read of a deep chain can serve a permanently stale value.
   Fixed in-game by reading the chain bottom-first per frame
   (`World.fs:308`); confirmed as a library bug, fix upstream.

## Where adaptive data falls (the boundary)

- **Dense per-frame change at scale is the ceiling.** The gating benefit
  needs sparse change. The drip (~1.2 KB/changed row) becomes GC pressure
  before CPU does: practical ceiling for "everything in adaptive maps" is
  the **low thousands of densely-changing rows**. This game peaks at ~150.
- **Numerically hot inner loops** (physics, particles, crowd steering, bone
  matrices) want SoA/SIMD, not per-element closures — keep them on arrays
  and let adaptive data observe snapshots (exactly what `Projectiles.tick`
  does).
- **Writes ≫ reads** workloads (telemetry, event streams) pay journaling
  for values nobody pulls.
- **Hard worst-case latency** (lockstep netcode, zero-jitter budgets):
  lazy settle defers cost into the first read after a write burst, and the
  GC owns the rest of the jitter.

## Where next

- **Headless stress probe** (Mibo headless mode): sweep entity counts
  250→8k to convert the "low thousands" extrapolation into a measured
  breakpoint (CPU + GC).
- **A dense-change game** (survivor-like, 500–2k moving entities): the
  real test of the hybrid boundary — SoA data plane + adaptive logic
  graph.
- **Library-side:** the per-element recompute allocation (~1.2 KB/row)
  is the known weak point at scale; the stale-read fix is delegated
  upstream.

## Reproducing the numbers

```
dotnet-trace collect --profile gc-verbose --name Defli
dotnet-trace convert trace.nettrace --format Speedscope -o trace.speedscope
dotnet fsi tools/analyze-trace.fsx trace.speedscope.json
dotnet fsi tools/analyze-subtree.fsx trace.speedscope.json
```
