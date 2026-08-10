module Defli.World.Systems.Enemies

open System.Collections.Generic
open System.Numerics
open AdaptiveSlop.Core
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Raylib_cs
open Defli.World

// ─────────────────────────────────────────────────────────────
// Enemies sub-system — owns ALL enemy component maps (lifecycle
// consistency: it alone spawns/despawns enemies, atomically across
// its maps) and its own projections (derived from own maps only).
//
//   Healths   — damage writes touch ONLY this map (row-level delta)
//   Motions   — speed/slow/progress/pathIndex
//   Positions — movement; separate so damage never invalidates it
//   Defs      — static per enemy (sprite, reward); written once
//
// Projections:
//   Views      = Positions × Healths × Motions join (AMap.mapA +
//                per-element tryFind avals — one ElementMapNode)
//   Alive      = Views |> filter Hp > 0 (targeting/render query)
// ─────────────────────────────────────────────────────────────

[<Struct>]
type EnemyMsg =
  | Spawn of def: EnemyDef
  /// Spawn mid-path at an explicit position/progress (Phase 6 split
  /// children appear at the corpse — Spawn would teleport them to the
  /// path origin).
  | SpawnAt of spawnAt: struct (EnemyDef * Vector2 * float32 * int)
  | ApplyDamage of applyDamage: struct (int<EnemyId> * int)
  | ApplySlow of slow: SlowApply
  | Despawn of enemy: int<EnemyId>

[<Struct>]
type EnemyEvent =
  | Killed of killed: struct (int<EnemyId> * int)
  | ReachedBase of enemy: int<EnemyId>

type EnemiesModel() =
  member val Healths = CMap.empty<int<EnemyId>, Health> with get, set
  member val Motions = CMap.empty<int<EnemyId>, Motion> with get, set
  member val Positions = CMap.empty<int<EnemyId>, Vector2> with get, set
  member val Defs = CMap.empty<int<EnemyId>, EnemyDef> with get, set
  /// Tagged from the start — ids never pass through a plain int.
  member val NextId = 0<EnemyId> with get, set
  /// Slow expiry timers (sim-only, plain — not adaptive).
  member val SlowTimers = Dictionary<int<EnemyId>, float32>() with get, set
  // Own projections (own maps only) — built in Enemies.init.
  member val Views: amap<int<EnemyId>, EnemyView> =
    Unchecked.defaultof<_> with get, set

  member val Alive: amap<int<EnemyId>, EnemyView> =
    Unchecked.defaultof<_> with get, set

  /// Live boss positions (Positions × Defs, archetype-filtered) — the
  /// world-owned Suppression projection joins on this (Phase 6).
  member val BossPositions: amap<int<EnemyId>, Vector2> =
    Unchecked.defaultof<_> with get, set

module Enemies =
  open System

  // ── Projections (the AdaptiveSlop showcase: join, filter, aggregate) ──

  let private buildViews(m: EnemiesModel) : amap<int<EnemyId>, EnemyView> =
    // One ElementMapNode: per enemy, an aval joining its three rows via
    // tryFind. Rows are written atomically in transactions, so post-commit
    // all three always exist; the defensive zero row only guards transient
    // mid-transaction reads (Alive filters it out).
    m.Positions
    |> AMap.mapA(fun eid pos ->
      let healths = m.Healths |> AMap.tryFind eid
      let motions = m.Motions |> AMap.tryFind eid

      let inline matchA (h: Health voption) (mv: Motion voption) =
        match struct (h, mv) with
        | ValueSome h, ValueSome mv -> {
            Pos = pos
            Hp = h.Hp
            MaxHp = h.MaxHp
            Progress = mv.Progress
            Slow = mv.Slow
            PathIndex = mv.PathIndex
          }
        | _ ->
            {
              Pos = pos
              Hp = 0
              MaxHp = 0
              Progress = 0f
              Slow = 1f
              PathIndex = 0
            }

      motions |> AVal.map2 matchA healths)

  let private buildAlive(m: EnemiesModel) : amap<int<EnemyId>, EnemyView> =
    m.Views |> AMap.filter(fun _ v -> v.Hp > 0)

  /// Boss positions: per-enemy tryFind into Defs (the Views-join
  /// shape), kept only when the archetype is Boss. Written by the
  /// movement tick like Positions; read by the world's Suppression
  /// projection.
  let private buildBossPositions
    (m: EnemiesModel)
    : amap<int<EnemyId>, Vector2> =
    m.Positions
    |> AMap.chooseA(fun eid pos ->
      m.Defs
      |> AMap.tryFind eid
      |> AVal.map(fun def ->
        def
        |> ValueOption.bind(fun d ->
          if d.Archetype = EnemyArchetype.Boss then
            ValueSome pos
          else
            ValueNone)
        |> ValueOption.toOption))

  let init() : EnemiesModel =
    let m = EnemiesModel()
    m.Views <- buildViews m
    m.Alive <- buildAlive m
    m.BossPositions <- buildBossPositions m
    m

  // ── Cold path (router messages) — mutates OWN maps only ──

  let update
    (msg: EnemyMsg)
    (model: EnemiesModel)
    (path: Vector2[])
    : struct (EnemiesModel * EnemyEvent[]) =
    match msg with
    | Spawn def ->
      let eid = model.NextId
      model.NextId <- model.NextId + 1<EnemyId>

      Transaction.run(fun () ->
        model.Healths |> CMap.addOrUpdate eid { Hp = def.Hp; MaxHp = def.Hp }

        model.Motions
        |> CMap.addOrUpdate eid {
          Speed = def.Speed
          Slow = 1f
          Progress = 0f
          PathIndex = 0
        }

        model.Positions |> CMap.addOrUpdate eid path[0]
        model.Defs |> CMap.addOrUpdate eid def)

      model, Array.empty
    | SpawnAt(def, pos, progress, pathIndex) ->
      // Split-child spawn: the same atomic four-row write, but at the
      // corpse's position and path state (not the path origin).
      let eid = model.NextId
      model.NextId <- model.NextId + 1<EnemyId>

      Transaction.run(fun () ->
        model.Healths |> CMap.addOrUpdate eid { Hp = def.Hp; MaxHp = def.Hp }

        model.Motions
        |> CMap.addOrUpdate eid {
          Speed = def.Speed
          Slow = 1f
          Progress = progress
          PathIndex = pathIndex
        }

        model.Positions |> CMap.addOrUpdate eid pos
        model.Defs |> CMap.addOrUpdate eid def)

      model, Array.empty
    | ApplyDamage(eid, amount) ->
      match model.Healths |> CMap.tryGetValue eid with
      | ValueSome h when h.Hp > 0 ->
        let hp = max 0 (h.Hp - amount)
        model.Healths |> CMap.addOrUpdate eid { h with Hp = hp }

        if hp = 0 then
          match model.Defs |> CMap.tryGetValue eid with
          | ValueSome def -> model, [| Killed(eid, def.GoldReward) |]
          | ValueNone -> model, Array.empty
        else
          model, Array.empty
      | _ -> model, Array.empty
    | ApplySlow slow ->
      match model.Motions |> CMap.tryGetValue slow.Enemy with
      | ValueSome mv ->
        model.Motions
        |> CMap.addOrUpdate slow.Enemy { mv with Slow = slow.Factor }

        model.SlowTimers[slow.Enemy] <- slow.Seconds
        model, Array.empty
      | ValueNone -> model, Array.empty
    | Despawn eid ->
      Transaction.run(fun () ->
        model.Healths |> CMap.remove eid
        model.Motions |> CMap.remove eid
        model.Positions |> CMap.remove eid
        model.Defs |> CMap.remove eid)

      model, Array.empty

  // ── Hot path (movement / "physics" phase) — direct values, no closures ──

  // ── Per-enemy movement, staged into inline helpers (the JIT fuses
  // them back together — no closures, no per-frame allocations) ──

  /// Stage 1 — resolve the archetype (defs are written once at spawn;
  /// a miss is a transient row → Grunt).
  let inline archetypeOf defs eid =
    defs
    |> CMap.tryGetValue eid
    |> ValueOption.map _.Archetype
    |> ValueOption.defaultValue Grunt

  /// Stage 2 — fliers: interpolate the straight line spawn → base.
  /// Returns (pos, progress, arrived); PathIndex is meaningless (0).
  let inline flyStep
    (dt: float32)
    (mv: Motion)
    (flyDist: float32)
    (spawn: Vector2)
    (basePos: Vector2)
    : struct (Vector2 * float32 * bool) =
    let step =
      if flyDist <= 0f then
        1f
      else
        mv.Speed * mv.Slow * dt / flyDist

    let progress = min 1f (mv.Progress + step)
    struct (Vector2.Lerp(spawn, basePos, progress), progress, progress >= 1f)

  /// Stage 3 — road walkers (Grunt/Runner/Tank/Boss): consume the
  /// `Speed * Slow * dt` step along the waypoint segments, advancing
  /// PathIndex. Returns (pos, pathIndex, progress, arrived).
  let inline walkStep
    (dt: float32)
    (mv: Motion)
    (pos: Vector2)
    (path: Vector2[])
    : struct (Vector2 * int * float32 * bool) =
    let mutable p = pos
    let mutable idx = mv.PathIndex
    let mutable remaining = mv.Speed * mv.Slow * dt

    while remaining > 0f && idx < path.Length - 1 do
      let target = path[idx + 1]
      let d = target - p
      let dist = d.Length()

      if dist <= remaining then
        p <- target
        remaining <- remaining - dist
        idx <- idx + 1
      else
        p <- p + (d / dist) * remaining
        remaining <- 0f

    let arrived = idx >= path.Length - 1
    let total = float32(path.Length - 1)

    let progress =
      if arrived then
        1f
      else
        let segLen = Vector2.Distance(path[idx], path[idx + 1])

        if segLen <= 0f then
          float32 idx / total
        else
          (Vector2.Distance(path[idx], p) / segLen + float32 idx) / total

    p, idx, progress, arrived

  let tick
    (dt: float32)
    (model: EnemiesModel)
    (path: Vector2[])
    : struct (EnemiesModel * EnemyEvent seq) =
    // Expire slow timers (collect first — mutating during iteration is unsafe).
    let mutable expired: ResizeArray<int<EnemyId>> = null

    for KeyValueV(eid, remaining) in model.SlowTimers do
      let remaining = remaining - dt

      if remaining <= 0f then
        if isNull expired then
          expired <- ResizeArray()

        expired.Add eid
      else
        model.SlowTimers[eid] <- remaining

    if not(isNull expired) then
      for eid in expired do
        match model.Motions |> CMap.tryGetValue eid with
        | ValueSome mv ->
          model.Motions |> CMap.addOrUpdate eid { mv with Slow = 1f }
        | ValueNone -> ()

    // Movement along waypoints. Fliers ignore the road: they interpolate
    // the straight line spawn → base (world-space, not waypoint walking).
    let flyDist = Vector2.Distance(path[0], path[path.Length - 1])

    let mutable events: ResizeArray<EnemyEvent> = null
    let mutable arrivals: ResizeArray<int<EnemyId>> = null

    for KeyValueV(eid, pos) in model.Positions |> AMap.getValue do
      model.Motions
      |> CMap.tryGetValue eid
      |> ValueOption.iter(fun mv ->
        // The archetype picks the locomotion: fliers fly the straight
        // line spawn → base, everyone else walks the waypoints.
        let archetype = archetypeOf model.Defs eid

        let struct (p, idx, progress, arrived) =
          if archetype = EnemyArchetype.Flier then
            let struct (p, progress, arrived) =
              flyStep dt mv flyDist path[0] path[path.Length - 1]

            struct (p, 0, progress, arrived)
          else
            walkStep dt mv pos path

        if arrived then
          if isNull arrivals then
            arrivals <- ResizeArray()

          arrivals.Add eid

          if isNull events then
            events <- ResizeArray()

          events.Add(ReachedBase eid)
        else
          model.Positions |> CMap.addOrUpdate eid p

          model.Motions
          |> CMap.addOrUpdate eid {
            mv with
                Progress = progress
                PathIndex = idx
          })

    // Arrivals are removed atomically (the router also gets ReachedBase).
    if not(isNull arrivals) then
      Transaction.run(fun () ->
        for eid in arrivals do
          model.Healths |> CMap.remove eid
          model.Motions |> CMap.remove eid
          model.Positions |> CMap.remove eid
          model.Defs |> CMap.remove eid)

    model, (if isNull events then Array.empty else events)

  // ── View (sprites + health bars from the Alive projection) ──

  let view
    (ctx: GameContext)
    (model: EnemiesModel)
    (path: Vector2[])
    (buffer: RenderBuffer2D)
    =
    let assets = GameContext.getService<IAssets> ctx
    let tex = assets.Texture Tiles.SheetPath

    let alive = model.Alive |> AMap.getValue

    let defs = model.Defs |> AMap.getValue

    for KeyValueV(eid, v) in alive do
      defs
      |> ReadOnlyDict.tryGetValue eid
      |> ValueOption.iter(fun def ->
        let isBoss = def.Archetype = Boss

        // Boss aura ring (Phase 6): the suppression radius, drawn
        // faintly under everything else the boss overlaps.
        if isBoss then
          buffer
            .circleOutline(
              v.Pos,
              BossAura.Radius,
              Mibo.Color.create 255uy 60uy 60uy 70uy,
              layer = Layers.Effects
            )
            .drop()

        // Heading: fliers fly the straight spawn → base line; the rest
        // aim at the next waypoint (0° = up; raylib rotates CW).
        let angle =
          if def.Archetype = Flier then
            let d = path[path.Length - 1] - path[0]
            MathF.Atan2(d.Y, d.X) * 180f / MathF.PI % 360f
          elif v.PathIndex >= path.Length - 1 then
            0f
          else
            let d = path[v.PathIndex + 1] - v.Pos
            MathF.Atan2(d.Y, d.X) * 180f / MathF.PI % 360f

        // Bosses render 1.6× — the silhouette must read at a glance.
        let sizeBoost = if isBoss then 1.6f else 1f

        def.Sprite
        |> Tiles.tryByName
        |> ValueOption.iter(fun tile ->
          // Scale the baked sprite to a consistent ~44px while keeping aspect.
          let scale =
            44f * sizeBoost / max (float32 tile.Width) (float32 tile.Height)

          let w = float32 tile.Width * scale
          let h = float32 tile.Height * scale

          buffer
            .sprite(
              SpriteState.create(
                tex,
                Rectangle(v.Pos.X - w / 2f, v.Pos.Y - h / 2f, w, h),
                tile.Rect
              )
              |> SpriteState.withOrigin(Vector2(w / 2f, h / 2f))
              |> SpriteState.withRotation angle
              |> SpriteState.withLayer Layers.Entities
            )
            .drop())

        // Turret — centered on the body, aimed at the heading plus the
        // def's built-in orientation correction (0° = up in the sheet).
        def.Turret
        |> ValueOption.bind Tiles.tryByName
        |> ValueOption.iter(fun turretTile ->
          let tscale =
            44f * sizeBoost
            / max (float32 turretTile.Width) (float32 turretTile.Height)

          let tw = float32 turretTile.Width * tscale
          let th = float32 turretTile.Height * tscale

          buffer
            .sprite(
              SpriteState.create(
                tex,
                Rectangle(v.Pos.X - tw / 2f, v.Pos.Y - th / 2f, tw, th),
                turretTile.Rect
              )
              |> SpriteState.withOrigin(Vector2(tw / 2f, th / 2f))
              |> SpriteState.withRotation(angle + def.TurretAngle)
              |> SpriteState.withLayer Layers.Entities
            )
            .drop()))

      // Health bar (only when damaged).
      if v.Hp < v.MaxHp then
        let frac = float32 v.Hp / float32 v.MaxHp

        buffer
          .fillRect(
            v.Pos.X - 16f,
            v.Pos.Y - 28f,
            32f,
            4f,
            Color.Black,
            layer = Layers.Entities
          )
          .drop()

        buffer
          .fillRect(
            v.Pos.X - 16f,
            v.Pos.Y - 28f,
            32f * frac,
            4f,
            Color.Red,
            layer = Layers.Entities
          )
          .drop()
