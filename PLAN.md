# Defli — Minimal Tower Defense Plan

> **Living document.** Defli is a minimal tower-defense game on **Mibo** (MVU +
> raylib 2D), built to stress-test **AdaptiveSlop** (`AdaptiveSlop/` submodule) as
> the primary state-management driver — a **functional ECS**: component stores as
> `CMap`s, reactive "queries" (projections) as derived adaptive collections, MVU
> as the event/command router.
>
> Architecture lineage: **Kimo** (`E:\Kimo`) — same strict sub-system separation
> (router, events/intents as pure data, per-system models/messages/views). The
> **one structural difference**: Kimo runs each world's sim on a separate thread
> and publishes struct rows into render-owned adaptive graphs (Shape B). Defli
> runs the simulation **on the same thread as MVU** — the adaptive graph IS the
> model (Shape C). No post rings, no `WorldManager`, no `KeyTracker`, no publish
> step, **no view cache layer** — systems mutate cells directly; the view reads
> transient views at the frame's end.
>
> See `AGENTS.md` for the mandatory Mibo architecture rules; they apply here.

---

## Table of Contents

1. [Vision & Scope](#1-vision--scope)
2. [Architecture: Shape C — a functional ECS](#2-architecture-shape-c--a-functional-ecs)
3. [MVU ↔ Adaptive division of labor](#3-mvu--adaptive-division-of-labor)
4. [Sub-system Inventory](#4-sub-system-inventory)
5. [The AdaptiveSlop Showcase (projections)](#5-the-adaptiveslop-showcase-projections)
6. [Per-System Template](#6-per-system-template)
7. [World Router & View Composition](#7-world-router--view-composition)
8. [File Layout & fsproj](#8-file-layout--fsproj)
9. [Mibo Building Blocks](#9-mibo-building-blocks)
10. [Phased Roadmap](#10-phased-roadmap)
11. [AdaptiveSlop Performance Rules & Risks](#11-adaptiveslop-performance-rules--risks)
12. [Testing Strategy](#12-testing-strategy)

---

## 1. Vision & Scope

Minimal single-screen tower defense:

- Fixed tile grid (e.g. 16×9), one **path** (spawn → base) through it, buildable
  tiles beside the path.
- **Enemies** walk the path in **waves**; reaching the base costs a **life**.
- Player **places towers** (gold) on buildable tiles; towers auto-target enemies
  in range and fire **projectiles** (homing).
- **Economy**: gold from kills; **game over** when lives hit 0; **next wave**
  button.
- 2–3 tower kinds (Arrow: fast/cheap, Frost: applies slow, Cannon: slow/splash —
  stretch).

Deliberately small: 5 sim systems, ~100–300 concurrent entities. That is the
sweet spot for the component-map shape — enough entities for collection deltas
to matter, few enough that joins stay shallow.

## 2. Architecture: Shape C — a functional ECS

The world model is a set of **component stores** (`CMap<EntityId, Component>`
with `[<Struct>]` component rows) owned by sub-systems, plus a **projection
layer** of derived adaptive queries. Entities are just keys; an entity's
"archetype" is the set of maps its id appears in.

| ECS concept | Defli |
| --- | --- |
| Entity | `EnemyId` / `TowerId` / `ProjectileId` (unit-of-measure keys, zero-cost) |
| Component | `[<Struct>]` row in a `CMap<Id, Component>` |
| Archetype | the set of maps an entity id appears in (membership is implicit) |
| Query | **Projection** — derived `AMap`/`ASet`/`AVal`: joins, filters, aggregates |
| System | sub-system — **writes only its own maps**, reads projections |
| Spawn/Despawn | `Transaction.run` adding/removing rows across the system's own maps |
| World | `WorldModel` composing sub-models + cross-subsystem `Projections` |

```fsharp
type WorldModel() as this =
  member val Enemies = Enemies.init() with get, set
  member val Towers = Towers.init() with get, set
  member val Projectiles = Projectiles.init() with get, set
  member val Waves = Waves.init() with get, set
  member val Economy = Economy.init() with get, set
  member val Map = MapModel.create() with get, set
  member val Rng = Random(seed) with get, set        // determinism
  member val Projections = buildProjections this     // LAST — reads the maps above
```

### Projection ownership

**Projections live as close to their data as possible.** A sub-system owns
projections derived purely from **its own maps** — built in its `init`, exposed
on its model (`Enemies` owns `EnemyViews` (its 3-map join) and `AliveEnemies`
(filter)). The **world owns cross-subsystem projections** — anything joining
two systems' maps (`Homing` = Projectiles × Enemies.Positions, `Buildable` =
Map × Economy.Gold, `RangeRing` = shell hover × Towers.Statics) — built in
`buildProjections this` at construction. Systems stay self-contained: their
queries travel with them; the world builds only what needs to see several
systems at once. Nodes register lazily on first read, so init is cheap.

### Read model: transient views, no cache layer

**There is no `ViewCaches` layer.** Kimo version-checked and `force`d frozen
artifacts on the render thread (it needed retainable snapshots across the
thread seam). Defli is one thread, the view consumes within the frame, and
nothing writes between the sim's last tick write and the draw — so the view
reads **transient views** (`GetValue`, measured **0 B** on a clean derived
node) and renders directly. `force` (materializes a `FrozenDictionary`,
**measured ~1.7 KB per 100-entry map, no clean-node short-circuit**) is the
rare exception, only when a retainable snapshot is genuinely needed.

Force points are the **consumption edges** only: the view (render), and sim
phases that materialize their input before mutating (movement/"physics" runs
first — see §7). Everything else passes direct values or transient reads.

### What adaptive buys here (and what it doesn't)

**Buys:**
- **Row-level deltas.** A damage write touches only `Healths`; the joined
  `EnemyViews` projection re-derives exactly that enemy's row. `Positions`/
  `Motions` writes don't invalidate it.
- **Queries as data.** Targeting, render rows, homing, affordability, game-over
  are *derived state*, not code in the view or duplicated bookkeeping.
- **Write-many/read-once.** Systems write freely during `tick`; the view's
  transient reads settle everything once per frame, allocation-free.
- **Atomic multi-map mutations** via `Transaction.run` (spawn = 3 map inserts,
  one notification).

**Doesn't buy (be honest, don't over-engineer):**
- Per-field invalidation of values that always change together — so group
  components by *write frequency and owner*, not by ECS dogma: `Healths`
  (damage), `Motions` (speed/slow/progress/pathIndex), `Positions` (move).
  Three maps, not seven.
- Bulk iteration — the render loop still walks the transient view; the win is
  the view is *already materialized and delta-maintained*, and unchanged
  subgraphs cost O(1).
- The static path/waypoints — plain data, not adaptive (like Kimo's maps).

## 3. MVU ↔ Adaptive division of labor

Two complementary drivers; neither replaces the other:

| Concern | Driver |
| --- | --- |
| What happened / what should happen next | **MVU** — events & intents, `Cmd`, router translation |
| Input → semantic actions | **MVU** — `InputMap` → `GameIntent` |
| Who runs this tick, in what order | **MVU** — `Tick` routing / System pipeline |
| Simulation facts (component rows, gold, lives) | **Adaptive** — `CMap`/`CVal` cells, written by systems |
| Everything derived (joins, filters, aggregates) | **Adaptive** — projections, read by systems + view |
| What the view sees this frame | **Adaptive** — transient view reads at the view (0 B); `force` only for retainable snapshots |

Rule of thumb: **MVU answers "what runs next", adaptive answers "what is true
now".** A system never computes derived state imperatively for consumers; it
writes component rows, and projections exist as graph nodes. The view never
computes derived state either — it reads avals and transient projection views.

## 4. Sub-system Inventory

### 4.1 Shell (`Application.fs`)
MVU `Model` holds only client concerns: `Input: ActionState<GameAction>`,
`MousePos`, `World: WorldModel`. Routes `Msg` → `WorldMsg`, lifts with `Cmd.map`.
Owns the hover cell `CVal` (from click intents) that world projections join on.

### 4.2 Enemies (`World/Systems/Enemies.fs`)
Owns **all** enemy component maps (lifecycle consistency: it alone spawns/
despawns enemies, atomically across its maps) **and its own projections**.

```fsharp
// Components (Domain.fs — compiled first; structs, value equality)
[<Struct>]
type Health = { Hp: int; MaxHp: int }

[<Struct>]
type Motion = { Speed: float32; Slow: float32; Progress: float32; PathIndex: int }

// Positions: CMap<EnemyId, Vector2>  — separate map: damage writes must NOT
// invalidate positions and vice versa.

type EnemiesModel() as this =
  member val Healths = CMap.empty<EnemyId, Health> with get, set
  member val Motions = CMap.empty<EnemyId, Motion> with get, set
  member val Positions = CMap.empty<EnemyId, Vector2> with get, set
  member val Events = ResizeArray<EnemyEvent>() with get, set
  // Subsystem-owned projections (own maps only) — declared after the maps
  member val Views = buildEnemyViews this with get, set      // join → EnemyView rows
  member val Alive = buildAlive this with get, set           // filter hp > 0

[<Struct>]
type EnemyMsg =
  | Spawn of def: EnemyDef * spawnDelay: float32
  | ApplyDamage of enemy: EnemyId * amount: int
  | ApplySlow of enemy: EnemyId * factor: float32 * seconds: float32
  | Despawn of enemy: EnemyId

[<Struct>]
type EnemyEvent =
  | Killed of enemy: EnemyId * reward: int
  | ReachedBase of enemy: EnemyId
```

- `update` (cold path): `Spawn` inserts a row into each of the three maps inside
  `Transaction.run`; `ApplyDamage` writes `Healths` (single map touched — the
  delta propagates only to health-dependent projections); death detection reads
  the row after the write; `Despawn` removes rows from all three maps in a
  transaction.
- `tick dt` (hot path): the **movement/"physics" phase** — iterate a transient
  view of its own id list directly; advance `Positions` + `Motions` rows along
  the waypoint list (or the flier's straight line for `Flier` archetypes —
  Phase 3); write rows with `UpdateTo` semantics (equal value = no mark).
  Arrival → `ReachedBase` event.
- Writes go through the model's maps only — `CMap.addOrUpdate`, never `Post`
  (same thread).

### 4.3 Spawning (`World/Systems/Spawning.fs`)
Kimo's `Systems/Spawning.fs` analog — owns **placement, the spawn queue, and
the weighted table picks**. Waves composes *what* a wave contains; Spawning
executes it. Emits spawn intents; the router forwards them to Enemies.

```fsharp
/// One wave's executable content (composed by Waves, executed here).
[<Struct>]
type WaveDef = {
  Table: struct (EnemyDef * int)[]   // weighted enemy table (Kimo pickKey)
  Count: int                          // total spawns in the wave
  Interval: float32                   // seconds between spawns
  InitialDelay: float32
}

type SpawningModel(rng: Random) =
  member val Queue = ResizeArray<struct (EnemyDef * float32)>() with get, set
  /// Own RNG stream — seeded by the caller, never shared with other systems.
  member val Rng: Random = rng

[<Struct>]
type SpawnMsg = | FillWave of wave: WaveDef
[<Struct>]
type SpawnEvent = | SpawnEnemy of def: EnemyDef | SpawnFailed of reason: string
```

- **update** (cold path): `FillWave` builds the queue — one weighted
  `pickKey` per spawn (Kimo's algorithm), spaced by `Interval`; the queue
  stores `(def, remainingDelay)` pairs.
- **tick dt** (hot path): drain the queue — decrement remaining delays,
  emit `SpawnEnemy` for due entries, swap-remove (order not significant,
  Kimo's pattern).
- **Placement**: the map's `SpawnCell` + a walkable validation (Kimo's
  `resolvePlacement`/`isWalkable` — `AtCell` semantics today; `NearCell`
  ring search when procedural maps land in Phase 5).
- **Deliberately no capacity/respawn invariant**: Kimo's zone capacity +
  `EntityDied`→respawn serves ambient spawns; TD waves are finite batches.
  Death/arrival handling lives in Enemies (events out).

### 4.4 Waves (`World/Systems/Waves.fs`)
The wave **director** — pure composition + state; no queue, no timing.

```fsharp
type WavesModel() =
  member val WaveNumber = CVal.create 0 with get, set
  member val WaveActive = CVal.create false with get, set
  member val NextWaveIn = CVal.create 0f with get, set
  member val Events = ResizeArray<WaveEvent>() with get, set

[<Struct>]
type WaveMsg = | StartNextWave
[<Struct>]
type WaveEvent =
  | WaveStarted of wave: WaveDef
  | WaveCleared
```

- `composeWave waveNumber : WaveDef` — pure, deterministic per wave number
  (difficulty budget scales count/interval/table weights). No RNG here: the
  randomness lives in Spawning's picks (Kimo's rule: RNG streams are owned,
  never shared).
- `update`: `StartNextWave` → `composeWave` → `WaveStarted` event; the router
  translates it into `SpawnMsg.FillWave` (declarative, no direct calls).
- `tick dt`: decrement `NextWaveIn`; **wave-clear detection via direct
  values** (hot path, no closures): the router passes `aliveCount` (from
  `Enemies.Alive`) and `queueEmpty` (from Spawning) — when both are zero/
  empty, `WaveActive.Set false` + `WaveCleared` → Economy bonus.
- Owns its HUD projection: `WaveBanner = waveNumber |> AVal.map2 ... waveActive`.

### 4.5 Towers (`World/Systems/Towers.fs`)
Owns placement, targeting, firing.

```fsharp
// Components (Domain.fs)
[<Struct>]
type TowerStatic = { Def: TowerDef; Cell: struct (int * int) }   // written once
[<Struct>]
type TowerRuntime = { Cooldown: float32; Target: EnemyId voption }

// TowerDef carries the targeting policy (Phase 3):
//   TargetPolicy = First | Last | Strongest | Weakest | Closest

type TowersModel() =
  member val Statics = CMap.empty<TowerId, TowerStatic> with get, set
  member val Runtimes = CMap.empty<TowerId, TowerRuntime> with get, set
  member val NextId = 0 with get, set
  member val Events = ResizeArray<TowerEvent>() with get, set

[<Struct>]
type TowerMsg = | Place of cell: struct (int * int) * def: TowerDef
[<Struct>]
type TowerEvent = | Fired of tower: TowerId * enemy: EnemyId * damage: int
```

- Static vs runtime split is the write-frequency grouping: `Statics` is written
  once (placement), `Runtimes` every tick — targeting writes don't invalidate
  the static projection (tower sprite positions, range rings).
- `Place` validates against the map + gold (router checks the economy aval
  first); inserts both rows in a `Transaction.run`.
- `tick`: decrement cooldown, acquire target from **`Enemies.Alive`** — a
  transient view read once per frame and passed in as a direct value (hot
  path, no closure); narrow candidates with `Grid2DSpatial.inRange` over the
  tower's cell (Chebyshev ring, exact range in cells), then exact
  world-distance against the transient enemy positions; write `Runtimes`
  rows; on fire emit `Fired` (router → Projectiles spawn + enemy damage).

### 4.6 Projectiles (`World/Systems/Projectiles.fs`)
Owns in-flight shots. **The `bind` showcase** — see §5.

```fsharp
[<Struct>]
type ProjectileRow = {
  Pos: Vector2
  TargetEnemy: EnemyId
  Damage: int
  Speed: float32
  Lifetime: float32
}

type ProjectilesModel() =
  member val Rows = CMap.empty<ProjectileId, ProjectileRow> with get, set
  member val NextId = 0 with get, set
  member val Events = ResizeArray<ProjectileEvent>() with get, set

[<Struct>]
type ProjectileMsg = | Spawn of ...
[<Struct>]
type ProjectileEvent = | Impact of projectile: ProjectileId * enemy: EnemyId * damage: int
```

- One map is enough — projectiles have no cross-component reads; don't add
  maps for the sake of it. Its homing projection joins `Enemies.Positions`, so
  it is **world-owned** (see §5 #3).
- `tick`: advance `Pos` toward the target's **live position row** (read from
  `Enemies.Positions` directly), hit check, `Impact` event → router →
  `EnemyMsg.ApplyDamage`.

### 4.7 Economy (`World/Systems/Economy.fs`)
Two singletons, one system.

```fsharp
type EconomyModel() =
  member val Gold = CVal.create 60 with get, set
  member val Lives = CVal.create 20 with get, set

[<Struct>]
type EconomyMsg = | SpendGold of amount: int | EarnGold of amount: int | LoseLife
```

No events (nothing consumes economy output except the view). Kills/arrivals
reach it via router-translated `Cmd`; spends are validated by the caller
against the reactive `Gold` aval. Owns its `GameOver` projection (`lives ≤ 0`).

### 4.8 Map & Map Generation (`World/Systems/Map.fs`)
The map is a **`CellGrid2D<MapTile>`** (`Mibo.Layout`) — static content, built
once at `World.init`, never mutated (same rule as Kimo's map/stores; not
adaptive).

```fsharp
[<Struct>]
type TerrainKind = Grass | Dirt | Stone | Sand

[<Struct>]
type MapTile = {
  Terrain: TerrainKind
  IsPath: bool                    // road — enemies walk it, NOT buildable
  Buildable: bool                 // grass beside the road
  Decor: DecorKind voption        // tree/rock/crate — blocks building
}

type MapModel = {
  Grid: CellGrid2D<MapTile>
  Path: Vector2[]                 // world-space waypoint centers (spawn → base)
  SpawnCell: struct (int * int)
  BaseCell: struct (int * int)
}
```

**Generation — Level 1 (Phase 0, hand-authored):**
- `CellGrid2D.create width height cellSize origin`; fill grass tiles.
- A fixed waypoint list defines the road; carve it with the stamp machinery:
  `Layout.run (fun s -> ... setLocal ...) grid` over `createSection`
  (`Mibo.Layout` / `GridSection2D`) — each path cell gets `IsPath = true`,
  `Buildable = false`; neighbors of the path stay buildable.
- Waypoints → world centers via `CellGrid2D.getWorldPos` (the movement phase
  walks these).

**Generation — Level 2 (Phase 5, procedural, the spatial stress test):**
- `Rng` (world seed, deterministic) scatters obstacle clusters
  (`Layout.scatterStamp` — trees/rocks/crates block building).
- Pick spawn/base cells; run `Grid2DSpatial.findPath` (`isPassable` =
  not obstacle) from spawn → base; the found path BECOMES the road
  (`IsPath = true`), guaranteeing a walkable route by construction.
- Validate with `Grid2DSpatial.floodFill` (spawn→base reachability) — cheap
  BFS over the pooled queue; regenerate or patch if a route is missing.

**Spatial functions usage map (`Mibo.Layout.Grid2DSpatial`):**

| Need | Function | When |
| --- | --- | --- |
| Click → cell (tower placement intent) | `worldToCell` | shell input, cold path |
| Placement validation (empty + buildable) | `CellGrid2D.get` on `Buildable` + tower map | cold path |
| Target candidates around a tower | `inRange x y range grid` (Chebyshev ring) → precise world-distance check | Towers.tick, hot path |
| Optional tower LOS (cannons over walls) | `lineOfSight`/`lineOfSightCells` (Bresenham) | stretch |
| View culling when panning | `CellGrid2D.iterVisible` | Map.view (Phase 4 camera) |
| Enemy movement | `Path: Vector2[]` from `getWorldPos` | Enemies.tick (physics phase) |

### 4.9 Assets & Sprite Sheets (`Tiles.fs` — baked, no runtime parsing)
`assets/` holds Kenney packs (CC0): `kenney_tower-defense-top-down` (the main
sheet — 299 tiles: `path_*`/grass/dirt/stone/sand terrain, `turret_base_a/b`,
`rocket_pod_*` (projectiles), crates/trees/rocks (decor/obstacles), coins,
buttons, crosshair, impact effects), `kenney_top-down-tanks-remastered`
(enemy sprites), `kenney_racing-pack` (spare).

- **The atlas is baked into the codebase, not parsed at runtime.**
  `tools/gen-tiles.fsx` reads the Kenney XML once (dev-time) and emits
  `World/Tiles.fs` — a committed, compile-checked dataset: `Tiles.all`
  (299 `TileInfo` records with name + position + size), semantically named
  accessors (`Tiles.grassFullA`, `Tiles.pathVerticalDirt`, …) and groups
  (`Tiles.groundGrass`, `Tiles.pathDirt`, `Tiles.effects`).
  Regenerate with `dotnet fsi tools/gen-tiles.fsx` when the sheet changes;
  the curated semantic-name mapping lives at the top of the script.
- Runtime loads exactly one texture (`assets.Texture Tiles.SheetPath` via
  `IAssets`, cached) and indexes baked rects — zero parsing, zero lookups.
- Sprites are plain data (never adaptive); the sim stays coordinate-only.

### 4.10 VFX & Particles (`World/Systems/Vfx.fs`)
Kimo Phase 4 analog — a dedicated VFX sub-system, but deliberately **the one
non-adaptive system in the world**: per-particle adaptive cells would be node
churn for pure presentation. VFX is fire-and-forget; nothing in the sim
queries it, so it uses the pooled-particles pattern
([Pooled Particles](https://angelmunoz.github.io/Mibo/patterns/pooled-particles.html))
with plain SoA pools.

```fsharp
[<Struct>]
type VfxKind =
  | ImpactBurst        // effect_impact_burst / _ring / _debris (Kenney sheet)
  | DeathPoof          // debris burst on kill
  | MuzzleFlash        // small burst at the tower on fire
  | PlaceDust          // ring on tower placement
  | BaseHit            // red ring at the base on leak

[<Struct>]
type VfxSpawn = { Kind: VfxKind; Pos: Vector2; Rotation: float32 voption }

type VfxModel() =
  // SoA pools (pooled-particles pattern): pre-allocated, zero-alloc steady state
  member val Particles = Array.zeroCreate<Particle2D> 1024 with get, set
  member val Velocities = Array.zeroCreate<Vector2> 1024 with get, set
  member val Lifetimes = Array.zeroCreate<float32> 1024 with get, set
  member val Count = 0 with get, set
  // The one adaptive touch: diagnostics aval for the debug HUD
  member val Active = CVal.create 0 with get, set

[<Struct>]
type VfxMsg = | Spawn of spawn: VfxSpawn
```

- **Model**: pre-allocated SoA pools (one `Particle2D` array for the render
  call, parallel velocity/lifetime arrays for the sim). All particles share
  ONE texture — the Kenney sheet — so the whole effect layer is a single
  `.particles(texture, particles, count, layer)` draw call.
- **update** (cold path): `Spawn` writes a burst into the pools (each
  `VfxKind` maps to source rects + spawn params from the sheet). No events
  out — fire-and-forget by design.
- **tick** (hot path): integrate velocities/lifetimes, then
  `ParticleSimulation.fadeAndCompact` (in-place, no allocation);
  `Active.Set count` for the debug HUD.
- **view**: `.particles(...)` on an effects layer above entities, below HUD.
- **Spawn sources** (router-translated events, MVU side): `Impact` →
  ImpactBurst, `Killed` → DeathPoof, `Fired` → MuzzleFlash, `Place` →
  PlaceDust, `ReachedBase` → BaseHit.
- Non-adaptive is a **documented exception** (rule 9), not an accident: the
  sim never reads VFX state, so adaptive tracking would buy nothing.

## 5. The AdaptiveSlop Showcase (projections)

Ownership per §2: **subsystem-owned** where the derivation touches one
system's maps, **world-owned** (`buildProjections`) where it joins two. Read by
systems (cold-path queries) and by the view (transient reads, once per frame).
**These are the stress tests** — each exercises a different AdaptiveSlop
mechanism:

| # | Projection | Owned by | Mechanism stressed |
| --- | --- | --- | --- |
| 1 | `EnemyViews = join(Positions × Healths × Motions)` — `AMap.mapA` over one map, `AMap.tryFind` the other two, `chooseV` the `ValueNone`s | Enemies | **Per-element adaptive join** — a `Healths` write re-derives exactly one enemy's view row; positions writes don't touch it |
| 2 | `Alive = EnemyViews |> AMap.filter (fun _ v -> v.Hp > 0)` | Enemies | **Collection filter** — the targeting/rendering query; deaths drop entries delta-wise, no removal churn in the maps |
| 3 | `Homing = Projectiles.Rows |> AMap.mapA (fun _ p -> Positions |> AMap.tryFind p.TargetEnemy |> AVal.map (fun pos -> struct (p.Pos, pos)))` | World | **`bind`-style dynamic dependency** — projectile render positions track the enemy's position row through the graph; dead target ⇒ `ValueNone` ⇒ entry drops |
| 4 | `GameOver = lives |> AVal.map (fun l -> l <= 0)` | Economy | scalar derivation feeding the router (stops waves) and the view (overlay) |
| 5 | `Buildable = buildTiles |> ASet.filterA (fun t -> occupied(t) \|\| gold < def.Cost)` — per-tile `AVal.map2` over `Gold` | World | `filterA` + `map2` fan-in for placement highlighting |
| 6 | `Furthest = Alive |> AMap.toASet |> ASet.fold 0f (fun acc v -> max acc v.Progress)` | Enemies | collection **reduction** for the "leak warning" HUD bar |
| 7 | `AliveCount = Alive |> AMap.toASet |> ASet.count`; `Kills = CVal` incremented by router | Enemies / world | aggregate scalars, delta-maintained |
| 8 | Wave start / spawn / despawn inside `Transaction.run` | — | **transactions**: one notification delivery for multi-cell atomic changes |
| 9 | `WaveBanner = waveNumber |> AVal.map2 (fun n active -> ...) waveActive` | Waves | HUD text derivation (optional `AVal.observe` if re-derivation is measurable) |
| 10 | `RangeRing = hoverCell |> AVal.bind (fun c -> Statics \|\> AMap.tryFind ...)` | World | `bind` for UI state joins (hover → tower → range circle) |
| 11 | `ActiveParticles = Vfx.Active` (CVal<int> written by Vfx.tick) | VFX | singleton scalar — the **one adaptive touch in the deliberately non-adaptive VFX system** (debug HUD reads it; per-particle cells would be node churn) |

Rules for these derivations:

- **Read each derived node once per frame.** Transient `GetValue` on a clean
  node is a flag/version check, **0 B**; a write between reads re-scans
  unobserved `mapA`/`filterA`/`chooseA` nodes O(N) — so don't read the same
  derived node twice in a frame, and read it after the sim settled.
- **Transient views are valid until the next write.** Consume within the frame;
  never retain across frames; never write while iterating. The view never
  writes, so its reads are safe by construction. `force` (~1.7 KB per 100-entry
  map, no clean short-circuit) only for genuinely retainable snapshots.
- **Keep derivations shallow.** Wide fan-out degrades exponentially — prefer
  `mapN`/`reduce`/`fold` over chains, and compute per-frame bulk aggregates in
  the view loop from transient rows when a derivation gets deep.
- **Writes are cheap until read.** Systems write rows freely during `tick`; the
  view's transient reads are the read that settles everything.
- **Component granularity = write-frequency groups.** Maps exist where a write
  to one thing must not invalidate another (`Healths` vs `Positions`); don't
  split a row that always changes together.

## 6. Per-System Template

Every system file follows Kimo's §9 shape, adapted to Shape C (component maps +
projections, no cache layer):

```fsharp
// ── Enemies Sub-System ──

// 1. Component rows live in Domain.fs ([<Struct>], value equality).

// 2. The system's slice: owned component maps + own projections + event buffer
type EnemiesModel() as this =
  member val Healths = CMap.empty<EnemyId, Health> with get, set
  member val Motions = CMap.empty<EnemyId, Motion> with get, set
  member val Positions = CMap.empty<EnemyId, Vector2> with get, set
  member val Events = ResizeArray<EnemyEvent>() with get, set
  member val Views = buildEnemyViews this with get, set   // own maps only
  member val Alive = buildAlive this with get, set

// 3. Isolated messages ([<Struct>] DU — zero allocation)
[<Struct>]
type EnemyMsg = | Spawn of def: EnemyDef | ApplyDamage of enemy: EnemyId * amount: int

// 4. Events (pure data — what happened)
[<Struct>]
type EnemyEvent = | Killed of enemy: EnemyId * reward: int | ReachedBase of enemy: EnemyId

// 5. Cold path — mutates OWN maps only, returns events
let update (msg: EnemyMsg) (model: EnemiesModel) =
  match msg with
  | Spawn def ->
    Transaction.run (fun () ->
      model.Healths |> CMap.addOrUpdate id { Hp = def.Hp; MaxHp = def.Hp }
      model.Motions |> CMap.addOrUpdate id { Speed = def.Speed; Slow = 1f; Progress = 0f; PathIndex = 0 }
      model.Positions |> CMap.addOrUpdate id def.StartPos)
    ...
  | ApplyDamage(eid, amount) ->
    ... // write Healths only; read back; emit Killed on zero-crossing

// 6. Hot path — movement/"physics" phase; direct values, no closures
let tick (dt: float32) (model: EnemiesModel) (path: Waypoint[]) =
  let ids = ... // transient view of Positions (0 B, read once)
  for eid in ids do
    ... // read rows, advance, UpdateTo-write both maps

// 7. Own view function (drawn by World.view composition)
let view (ctx: GameContext) (model: EnemiesModel) (buffer: RenderBuffer2D) =
  ... // sprites + health bars from a transient read of model.Views
```

Conventions inherited from Kimo: `[<Struct>]` messages/events, `ValueOption`,
BCL collections in hot paths, RNG seed in the model (deterministic), persistent
event buffers (`ResizeArray` cleared by the caller).

## 7. World Router & View Composition

```fsharp
// World.fs — WorldModel + WorldMsg + router + view

[<Struct>]
type WorldMsg =
  | RoomTick of tick: GameTime
  | EnemyMsg of EnemyMsg | WavesMsg of WaveMsg
  | TowersMsg of TowerMsg | ProjectilesMsg of ProjectileMsg
  | EconomyMsg of EconomyMsg

let update (msg: WorldMsg) (model: WorldModel) : struct (WorldModel * Cmd<WorldMsg>) =
  match msg with
  | EnemyMsg m ->
    ... // translate EnemyEvent → Cmd (Killed → Economy.EarnGold + despawn, ...)
  | TowersMsg m ->
    ... // translate TowerEvent.Fired → ProjectilesMsg.Spawn + EnemyMsg.ApplyDamage
  | RoomTick gt ->
    // Kimo's system organization: mutation phases in fixed order; the
    // movement/"physics" phase runs FIRST (Kimo: pipeMutable Physics.update →
    // pipeMutable Combat.update → snapshot → pipe → finish) and may
    // materialize transient views of its input before mutating.
    let dt = ...
    Enemies.tick dt model.Enemies model.Map.Path          // physics/movement first
    Spawning.tick dt model.Spawning                       // drains queue → SpawnEnemy intents
    Waves.tick dt model.Waves (aliveCount model) (queueEmpty model.Spawning)  // direct values
    Towers.tick dt model.Towers (transient model.Enemies.Alive)  // direct value
    Projectiles.tick dt model.Projectiles model.Enemies   // direct values
    model, Cmd.batch [...translated events...]

let view (ctx: GameContext) (model: WorldModel) (buffer: RenderBuffer2D) =
  // The view is the frame's final read point: transient reads settle the
  // graph once (0 B steady state). No caches, no version checks to maintain.
  Map.view ctx model.Map buffer                           // grid + path (static)
  Towers.view ctx model.Towers buffer                     // transient Statics/Runtimes
  Enemies.view ctx model.Enemies buffer                   // transient model.Enemies.Views
  Projectiles.view ctx model.Projectiles buffer           // transient world Homing
  Hud.view ctx model buffer                               // gold/lives/wave via avals
```

Tick ordering and cross-system reads follow Kimo: mutation phases first,
read-only phases after. Use the `System` pipeline
(`System.start → pipeMutable → snapshot → pipe → finish`) once ordering grows;
plain composition is fine for the minimal set. The view runs after all sim
mutations — that ordering is what makes transient reads safe.

## 8. File Layout & fsproj

```
Defli.fsproj          — add AdaptiveSlop.Core project reference (no NuGet published yet)
Program.fs            — entry point (RaylibGame wiring)
Application.fs        — shell MVU: Model, Msg, init/update/view, input subscription
World/
  Domain.fs           — typed ids (EnemyId/TowerId/ProjectileId), component structs
                        (Health/Motion/TowerStatic/...), MapTile/TerrainKind,
                        EnemyDef/TowerDef, path/waypoint types, constants
  Tiles.fs            — GENERATED baked atlas dataset (tools/gen-tiles.fsx)
  Systems/
    Map.fs            — CellGrid2D<MapTile> + generation (stamps / findPath),
                        waypoints; static
    Enemies.fs        — EnemiesModel (maps + own projections) + Msg/Event + update/tick/view
    Spawning.fs       — SpawningModel (queue + own RNG) + SpawnMsg/SpawnEvent + update/tick
    Waves.fs          — WavesModel (director) + WaveMsg/WaveEvent + update/tick/view (banner)
    Towers.fs         — TowersModel + TowerMsg/TowerEvent + update/tick/view
    Projectiles.fs    — ProjectilesModel + ProjectileMsg/Event + update/tick/view
    Vfx.fs            — VfxModel (SoA particle pools) + VfxMsg + update/tick/view
    Economy.fs        — EconomyModel + EconomyMsg + own GameOver projection
  Projections.fs      — world-owned CROSS-subsystem projections only
                        (Homing, Buildable, RangeRing) + buildProjections(world)
  World.fs            — WorldModel (composes models, Projections = buildProjections this),
                        WorldMsg, World.update (router), World.view
```

Compile order in `Defli.fsproj` (topological): `Domain.fs` → `Tiles.fs` →
`Systems/*.fs`
(each defines its own projections) → `Projections.fs` (cross-subsystem, needs
all models) → `World.fs` → `Application.fs` → `Program.fs`.

```xml
<ItemGroup>
  <ProjectReference Include="../AdaptiveSlop/src/AdaptiveSlop.Core/AdaptiveSlop.Core.fsproj" />
</ItemGroup>
```

`AdaptiveSlop.Core` targets net8.0/net10.0 — compatible with Defli's net10.0.

## 9. Mibo Building Blocks

> Per AGENTS.md: verify against the linked docs before implementing; do not
> reinvent what Mibo ships.

- **Input** — `InputMap`/`ActionState`/`InputMapper.subscribeStatic` (already in
  the template) → `GameIntent` (PlaceTower, StartNextWave, Pause). See
  [Input](https://angelmunoz.github.io/Mibo/input.html).
- **Grid & map** — `Mibo.Layout.CellGrid2D<'T>` (create/set/get/clear/
  getWorldPos/iter/iterVisible), `GridSection2D` + `Layout.run`/`setLocal`/
  `scatterStamp` for stamp-based map authoring, and
  `Mibo.Layout.Grid2DSpatial` — `findPath` (A*, pooled, `isPassable` +
  `costFn`), `floodFill` (BFS), `inRange`, `worldToCell`, `lineOfSight`,
  `neighbors4/8`, distance helpers. Sources:
  [Level Design 2D](https://angelmunoz.github.io/Mibo/level-design/2d/core.html),
  [Top-Down](https://angelmunoz.github.io/Mibo/level-design/2d/topdown.html)
  and `E:\Mibo\src\Mibo.Core\Layout\{Grid2D,Spatial2D,Layout}.fs`.
- **Rendering** — `Renderer2D` over the deferred `RenderBuffer2D` with
  `Draw.fillRect`/`sprite`/`text`, layered (ground/path → entities → HUD)
  ([2D Buffer & Commands](https://angelmunoz.github.io/Mibo/graphics2d/buffer-and-commands.html),
  [Layered Rendering](https://angelmunoz.github.io/Mibo/patterns/layered-rendering.html)).
- **Camera** — `Camera2D` for pan/zoom once the map outgrows the window
  (optional; minimal scope starts fixed-screen)
  ([Camera](https://angelmunoz.github.io/Mibo/camera.html)).
- **Particles** — `Particle2D` struct + `.particles(texture, array, count,
  layer)` bulk draw (one texture, one call), `ParticleSimulation.fadeAndCompact`
  (in-place fade + compaction, zero alloc), SoA pooled-particles pattern.
  See [2D Particles](https://angelmunoz.github.io/Mibo/graphics2d/particles.html)
  and [Pooled Particles](https://angelmunoz.github.io/Mibo/patterns/pooled-particles.html);
  the Kenney sheet's `effect_impact_*` sprites are the source rects.
- **Tick ordering** — `System` pipeline when phase composition grows
  ([System](https://angelmunoz.github.io/Mibo/system.html)).

## 10. Phased Roadmap

### Phase 0 — Skeleton & map (foundation)
- fsproj: add AdaptiveSlop.Core reference; split `Program.fs` into shell +
  world skeleton (empty `WorldModel`, router with `RoomTick` passthrough).
- `Domain.fs` component structs (`MapTile`, `TerrainKind`) + baked `Tiles.fs`
  (Kenney XML atlas → frame table); `CellGrid2D` map + hand-authored path
  (Level-1 generation, stamps) drawn by `Map.view`; input map (click → tile
  via `worldToCell`, StartNextWave key); shell routes intents as `WorldMsg`.
- **Config seam (Kimo Phase 6 analog)**: `WorldConfig` (seed, starting
  gold/lives, wave table, map variant) assembled outside the world, handed
  to `World.init`; code-authored definition stores (`EnemyDef`/`TowerDef`
  registries) built once at init, held on `WorldModel` — never
  process-global, same rule as Kimo's `WorldStores`.
- `Projections.fs` with an empty record; subsystem projections scaffolding.

### Phase 1 — Enemies, waves, economy (the adaptive showcase)
- `EnemiesModel` component maps (Healths/Motions/Positions) + own projections
  (Views/Alive); spawn/damage/tick along path; `Economy` cells.
- **`Spawning` slice** (Kimo analog): queue + own seeded RNG, `FillWave`
  (weighted `pickKey` picks, interval spacing), `tick` drain → `SpawnEnemy`
  intents; spawn placement validated against the map's `SpawnCell`.
- **`Waves` director**: pure `composeWave` (deterministic, budget-scaled),
  `Transaction.run` wave start (wave number + active + queue fill batched
  into one notification), clear detection via direct values.
- Projections **1, 2, 4, 6, 7, 9** live; health bars and game-over overlay
  render from transient projection reads. **This phase is the AdaptiveSlop
  stress test** — kill a wave, watch the `chooseV`-joined view delta only the
  dead enemy, verify zero-allocation steady state (profiler + GC pause checks,
  rule 10), then the GC/allocation trace.

### Phase 2 — Towers & projectiles (bind showcase)
- `Towers` slice: placement (buildability projection **5**), targeting via
  `Enemies.Alive`, firing events.
- `Projectiles` slice with homing `bind` projection **3** (world-owned);
  impact → damage.
- Range ring on hover (**10**, world-owned); gold spend on placement
  (router-checked via aval); targeting via `inRange` candidate narrowing.

### Phase 3 — Enemy AI & Targeting Policies (Kimo Phase 7 analog)
The TD's "AI" is two decision-makers — enemies (one goal: reach the base) and
 towers (one decision: who to shoot) — plus the wave director. This phase is
**sim logic over adaptive data**: policies read projections (transient views),
no new adaptive machinery needed.

- **Enemy archetypes & movement AI** (`EnemyDef.Archetype`, Kimo's
  WaypointNavigation + archetype analog):
  - `Grunt` — linear waypoint follower (default).
  - `Runner` — fast, low HP.
  - `Tank` — slow, high HP.
  - `Flier` — **the first real decision**: ignores the road, flies the
    straight line spawn→base (uses `distanceEuclidean`/`lineOfSight`
    geometry; world-space interpolation, not waypoints).
  - `Boss` (every N waves) — slow aura or split-on-death (stretch).
  - Movement modifiers already adaptive: frost `Slow` factor lives in
    `Motions` (status-effect analog), read by the movement tick.
- **Tower targeting policies** (Kimo's SkillSelection + decision-interval
  gating analog): `TargetPolicy = First | Last | Strongest | Weakest |
  Closest` as a `TowerDef` field; `Towers.tick` picks via the policy over
  the `AliveEnemies` transient view, narrowed by `inRange` + exact distance;
  re-acquisition gated by cooldown (Kimo's decision interval): re-target
  only when the current target dies/leaves range or the tower is ready to
  fire.
- **Wave director** (Kimo's SpawnZone weighted-table analog): waves composed
  from weighted enemy tables under a difficulty budget, deterministic from
  the world seed; escalating composition per `WaveNumber`.
- **Headless tests**: policy selection per scenario, flier pathing, slow
  modifier movement, wave composition determinism.
- **Deliberately NOT ported** from Kimo Phase 7: perception/FOV, memory,
  behavior trees, engage/flee states — a TD has no exploration or morale;
  porting BT infrastructure would violate the minimal scope.

### Phase 4 — HUD, VFX & polish
- HUD overlay (gold/lives/wave/furthest-progress bar) from avals; wave banner.
- **VFX subsystem (§4.10)**: impact bursts, death poofs, muzzle flashes,
  placement dust, base-hit rings — one `VfxMsg.Spawn` per router-translated
  event, SoA pools + `fadeAndCompact`, single `.particles(...)` draw call on
  the effects layer; optional floating damage text (small text pool) and
  screen shake (camera offset on BaseHit).
- Frost tower applies the `Slow` factor in `Motions`; sound optional.
- `Camera2D` pan/zoom if wanted.
- Shell scene flow (Kimo Phase 9 analog): start → game → game-over overlay →
  restart (fresh `WorldModel` from the same `WorldConfig`).

### Phase 5 — Stretch
- Tower upgrades (`Level` component map — projections compose on top), cannon
  (splash — **done**: the blast fans out from the detonation point; shots whose
  target dies mid-flight fly on to the last recorded position and detonate
  there), difficulty curve data.
- **Procedural map generation (Level 2)**: obstacle scattering via
  `scatterStamp`, road carved from `findPath`, `floodFill` reachability
  validation — the `Grid2DSpatial` stress test. Optional tower LOS via
  `lineOfSight`; `iterVisible` tile culling with the `Camera2D`.
- Optional: save/load (wave progress + high score — Kimo Phase 9 analog),
  audio. **Deferred to later phases** (post-Phase 5).

### Phase 6 — Boss waves (the spatial-join stress case)
- `Boss` archetype: slow, deep HP pool, walks the road (no custom
  locomotion). Every 5th wave leads with a tier-scaled boss via
  `WaveDef.ExtraSpawns` (explicit fixed-delay spawns queued ahead of the
  weighted picks — a table entry would make the boss a dice roll).
- **Suppression aura (the experiment)**: world-owned projection
  `Suppression = Towers.Statics × Enemies.BossPositions` — per-tower
  filter over live boss positions within `BossAura.Radius`, count > 0 →
  fire-rate factor. Boss positions move every frame → T filter/count
  nodes re-scan per frame: O(towers × bosses) of graph work for what a
  raw loop does free. Consumed MVU-clean: the router passes the
  transient view into `Towers.tick` as a direct value (like `Alive`);
  nothing is written back into a changeable map.
- **Split-on-death**: `Killed` (boss) → router synchronously spawns
  `SplitCount` grunts at the corpse (`EnemyMsg.SpawnAt` — same atomic
  four-row write at an explicit position/progress; `Spawn` would
  teleport children to the path origin). Synchronous per the
  FillWave-on-WaveStarted precedent: a Cmd round-trip would let the
  wave clear in the one-frame window before children exist.
- **The settle-ordering caveat (rule 12)** — the phase's real finding:
  reading only the Suppression tail served a permanently stale value
  until the chain's bottom (`BossPositions`) was read. RoomTick now
  reads bottom-up. Traces to be captured post-landing.
- Deferred from this phase: boss HP-bar UI, aura affecting enemy
  movement, save/load, audio, tower LOS.

## 11. AdaptiveSlop Performance Rules & Risks

From the README/benchmarks + our own measurements — enforced, not aspirational:

1. **Write many, read once.** Systems write rows freely during `tick`; the
   view's transient reads (after all sim mutations) settle the graph once. Ten
   writes before one read cost one recompute.
2. **Read each derived node once per frame, after the sim settled.** Transient
   `GetValue` on a clean node is a flag/version check, **0 B** (measured).
   Unobserved `mapA`/`filterA`/`chooseA` re-scan O(N) per read after a write —
   don't read the same derived node twice in a frame.
3. **`force` is the exception, not the rule.** It materializes a
   `FrozenDictionary`/`FrozenSet` — **~1.7 KB per 100 entries, and it does not
   short-circuit on clean nodes** (measured). In the same-thread frame path,
   transient reads are strictly cheaper and safe (no writes during
   consumption). Reserve `force` for genuinely retainable snapshots.
4. **Transient views die on the next write.** Never retain one across frames;
   never write while iterating. The view never writes — its reads are safe by
   construction; sim read phases must consume before mutating.
5. **Shallow derivations.** Wide fan-out degrades exponentially; use
   `mapN`/`reduce`/`fold` for 5+ inputs; compute per-frame bulk aggregates in
   the view loop from transient rows when a derivation gets deep.
6. **Equality dedup at the source.** `UpdateTo`/`addOrUpdate` with an equal
   value marks nothing — skip position writes that don't move.
7. **One graph, one thread.** Everything (init, ticks, views) on the main
   thread. Never create cells inside a `Task`/async work. No `Post`/`pump`
   needed; `Transaction.run` is the batching tool.
8. **Component granularity = write-frequency groups.** One map per component
   that changes independently; don't split rows that always change together;
   don't add maps with no independent writer. A few maps per entity kind, not
   one per field.
9. **Particles stay non-adaptive** (the VFX exception, §4.10): per-particle
   cells would be node churn for fire-and-forget presentation. SoA pools +
   `fadeAndCompact`; the only adaptive touch is the `ActiveParticles`
   diagnostics scalar.
10. **Steady state must allocate zero.** Verify with the GC/alloc profiler each
    phase: steady-state ticks and frame reads allocate nothing; only spawns,
    removals, and transactions may allocate.
11. **Watch `AMap.tryFind` joins inside `mapA`.** They're the power move and the
    scan risk (unobserved nodes re-check entries on read) — keep them read once
    per frame, keep target counts bounded (projectiles, not enemies, for homing
    binds).
12. **Lazy settle is bottom-up — read the chain's bottom first.** Transform
    nodes (chooseA/filter) only push downstream when they are themselves
    read, and scalar escapes (`count`/`tryFind`) gate on their DIRECT
    source's version. Reading only the tail of a deep chain
    (cmap → chooseA → filter → count → map) serves the value from the
    last time the middle was read — if nothing reads the bottom, that is
    *permanently stale* (Phase 6's Suppression bug: the aura never
    flipped until RoomTick read `BossPositions` before the tail).
    Per-frame loops that read every level every frame converge with ≤1
    frame lag (why `AliveCount` never showed it); a chain read only at
    its tail does not.

## 12. Testing Strategy

- **Headless** — the world is a plain class + `World.update`, so
  `HeadlessProgram.mkHeadless (World.init defs) World.update` +
  `HeadlessRunner.Step/StepUntil` runs the full sim in virtual time, no window
  ([Headless Mode](https://angelmunoz.github.io/Mibo/headless.html)).
- **Per system** — `update` is `(Msg, Model, Query) → (Model, Events)`: pure
  enough to unit test in isolation (Enemies: damage → death event; Spawning:
  FillWave → queue drain → SpawnEnemy intents; Waves: clear detection;
  Towers: range acquisition; Projectiles: homing hit).
- **Projection contracts** — after stepping, read the projections (transient
  `Alive`, `gameOver`, `Gold.Value`, `EnemyViews`) and assert they agree with
  the component maps. This is the contract between the MVU side and the graph
  side — the ECS "query returns what the tables contain".
- **Determinism** — RNG seeded in `WorldModel`; time only from `GameTime`; no
  ambient state in `update`.
- **Perf smoke** — a bench-style headless run (N enemies, M towers, K ticks)
  asserting steady-state allocation via `GC.GetAllocatedBytesForCurrentThread`
  deltas (rule 10).

---

### Appendix — AdaptiveSlop API cheat sheet (used above)

Sources: `CVal.create/set/UpdateTo/Value/GetValue`, `CMap.empty/addOrUpdate/
remove/Value/force`, `CSet.empty/add/remove`, `CList.empty/append`.
Derived: `AVal.map/map2/mapN/bind/getValue/force/observe`,
`AMap.mapA/filterA/chooseA/chooseV/tryFind/fold/toASet/getValue/force`,
`ASet.filterA/chooseA/mapA/count/fold`, `Transaction.run`.
(Names per `AdaptiveSlop.Core` API — verify exact signatures against the
[API reference](https://angelmunoz.github.io/Mibo/reference/index.html) for
Mibo and the AdaptiveSlop source before coding.)
