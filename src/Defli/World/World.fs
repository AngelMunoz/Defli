namespace Defli.World

open System
open System.Numerics
open AdaptiveSlop.Core
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Mibo.Layout
open Raylib_cs
open Defli
open Defli.World.Systems
// ─────────────────────────────────────────────────────────────
// World — owns WorldModel, the message router, and the view
// composition. The update is a ROUTER: dispatch + event/intent
// translation only, no game logic. Each sub-system owns its slice
// (component maps + projections); cross-system communication is
// declarative — systems emit events, the router translates them
// into Cmd<WorldMsg> for consumers.
// ─────────────────────────────────────────────────────────────

[<Struct>]
type WorldMsg =
  | RoomTick of tick: GameTime
  | StartNextWave
  | PlaceTower of cell: struct (int * int)
  /// Right-click on an own tower: upgrade it (the router validates
  /// gold + level cap).
  | UpgradeTower of cell: struct (int * int)
  | EnemyMsg of msgE: Enemies.EnemyMsg
  | SpawningMsg of msgS: Spawning.SpawnMsg
  | WavesMsg of msgW: Waves.WaveMsg
  | TowersMsg of msgT: Towers.TowerMsg
  | ProjectilesMsg of msgP: Projectiles.ProjectileMsg
  | VfxMsg of msgV: Vfx.VfxMsg
  | EconomyMsg of msgEC: Economy.EconomyMsg
  | CameraMsg of msgC: Camera.CameraMsg
  /// Player switched the tower kind to place (cold path).
  | SelectTower of def: TowerDef

type WorldModel() =
  member val Config: WorldConfig = Unchecked.defaultof<_> with get, set
  member val Map: MapModel = Unchecked.defaultof<_> with get, set
  member val Enemies = Enemies.Enemies.init() with get, set
  member val Spawning = Spawning.Spawning.init 0 with get, set
  member val Waves = Waves.Waves.init() with get, set
  member val Towers = Towers.Towers.init() with get, set
  member val Projectiles = Projectiles.Projectiles.init() with get, set
  member val Vfx = Vfx.Vfx.init() with get, set
  member val Economy = Economy.Economy.init WorldConfig.defaults with get, set
  /// Camera sub-system (headless — the view builds the raylib camera).
  member val Camera = Camera.Camera.init Vector2.Zero with get, set
  /// Tower kind the next placement uses — a CVal because the
  /// PlacementPreview projection joins on it (cold path writes).
  member val SelectedTower = CVal.create TowerDefs.arrow with get, set
  member val Projections: Projections = Unchecked.defaultof<_> with get, set
  /// Hover cell CVal — UI state written by the shell on MouseMoved;
  /// the world projections (PlacementPreview/RangeRing) join on it.
  member val HoverCell = CVal.create ValueNone with get, set
  /// World-sim diagnostics (sampled inside RoomTick — Kimo WorldDiag).
  member val Diag = WorldDiag() with get, set

module World =
  open AdaptiveSlop.Core

  let init(cfg: WorldConfig) : WorldModel =
    let model =
      WorldModel(
        Config = cfg,
        Map = MapModel.create cfg,
        Spawning = Spawning.Spawning.init cfg.Seed,
        Economy = Economy.Economy.init cfg,
        Camera =
          Camera.Camera.init(
            Vector2(
              float32(cfg.GridCols * Tiles.TileSize),
              float32(cfg.GridRows * Tiles.TileSize)
            )
          )
      )

    model.Projections <-
      Projections(
        model.Enemies,
        model.Towers,
        model.Projectiles,
        model.Economy,
        MapModel.buildableGrid model.Map,
        model.HoverCell,
        model.SelectedTower
      )

    model

  let inline cellSize(model: WorldModel) =
    Vector2(float32 Tiles.TileSize, float32 Tiles.TileSize)

  // ── Event → Cmd translation (the router's only job) ──

  let private translateEnemyEvents
    (model: WorldModel)
    (events: Enemies.EnemyEvent seq)
    : Cmd<WorldMsg>[] =
    [|
      for ev in events do
        match ev with
        | Enemies.Killed(eid, reward) ->
          let pos =
            model.Enemies.Positions
            |> CMap.tryGetValue eid
            |> ValueOption.defaultValue Vector2.Zero

          Cmd.ofMsg(EconomyMsg(Economy.EarnGold reward))
          Cmd.ofMsg(EnemyMsg(Enemies.Despawn eid))
          Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.DeathPoof, pos)))

          // Boss split-on-death (Phase 6): grunts burst from the
          // corpse. Spawned SYNCHRONOUSLY (the FillWave-on-WaveStarted
          // precedent): a Cmd round-trip would leave one frame with
          // aliveCount = 0 and the wave would clear before the
          // children exist. Children carry the wave's tier scale.
          let isBoss =
            model.Enemies.Defs
            |> CMap.tryGetValue eid
            |> ValueOption.exists(fun d -> d.Archetype = Boss)

          if isBoss then
            let struct (progress, pathIndex) =
              model.Enemies.Motions
              |> CMap.tryGetValue eid
              |> ValueOption.map(fun mv -> struct (mv.Progress, mv.PathIndex))
              |> ValueOption.defaultValue struct (0f, 0)

            let scale = AVal.getValue model.Waves.Scale
            let childDef = WaveScale.apply scale BossAura.SplitInto

            for i in 0 .. BossAura.SplitCount - 1 do
              // Small deterministic radial offsets so the children
              // don't stack on one pixel.
              let angle =
                float32 i / float32 BossAura.SplitCount * 2f * MathF.PI

              let childPos =
                pos + Vector2(MathF.Cos angle, MathF.Sin angle) * 16f

              Enemies.Enemies.update
                (Enemies.SpawnAt(childDef, childPos, progress, pathIndex))
                model.Enemies
                model.Map.Path
              |> ignore
        | Enemies.ReachedBase _ ->
          let basePos = Cells.center model.Map.BaseCell (cellSize model)

          Cmd.ofMsg(EconomyMsg Economy.LoseLife)
          Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.BaseHit, basePos)))
          Cmd.ofMsg(CameraMsg(Camera.Shake 8f))
    |]

  let private translateSpawnEvents
    (events: Spawning.SpawnEvent seq)
    : Cmd<WorldMsg>[] =
    [|
      for ev in events do
        match ev with
        | Spawning.SpawnEnemy def -> Cmd.ofMsg(EnemyMsg(Enemies.Spawn def))
        | Spawning.SpawnFailed _ -> ()
    |]

  let private translateWaveEvents
    (waveClearBonus: int)
    (events: Waves.WaveEvent seq)
    (model: WorldModel)
    : Cmd<WorldMsg>[] =
    [|
      for ev in events do
        match ev with
        | Waves.WaveStarted wave ->
          // Fill the spawn queue IN THE SAME MESSAGE: a Cmd round-trip
          // would leave one frame where the wave is active with an empty
          // queue, and the clear check would fire instantly (the wave
          // starts and clears without spawning anything).
          let struct (_, spawnEvents) =
            Spawning.Spawning.update
              (Spawning.SpawnMsg.FillWave wave)
              model.Spawning

          yield! translateSpawnEvents spawnEvents
        | Waves.WaveCleared ->
          Cmd.ofMsg(EconomyMsg(Economy.EarnGold waveClearBonus))
    |]

  let private translateTowerEvents
    (model: WorldModel)
    (events: Towers.TowerEvent seq)
    : Cmd<WorldMsg>[] =
    [|
      for ev in events do
        match ev with
        | Towers.Fired shot ->
          // Muzzle pos from the static row; projectile speed from the
          // EFFECTIVE def (the upgrade projection) — the +10 %/level
          // fire-rate/range upgrades must not be dropped here.
          let struct (pos, speed) =
            model.Towers.Statics
            |> CMap.tryGetValue shot.Tower
            |> ValueOption.map(fun s ->
              let eff =
                model.Towers.EffectiveDef
                |> AMap.getValue
                |> ReadOnlyDict.tryGetValue shot.Tower
                |> ValueOption.defaultValue s.Def

              struct (Cells.center s.Cell (cellSize model), eff.ProjectileSpeed))
            |> ValueOption.defaultValue struct (Vector2.Zero, 0f)

          // Seed the shot's last-known target position from the live
          // row (fall back to the muzzle): a target that dies
          // mid-flight still gets detonated on.
          let lastTargetPos =
            model.Enemies.Positions
            |> CMap.tryGetValue shot.Enemy
            |> ValueOption.defaultValue pos

          Cmd.ofMsg(
            ProjectilesMsg(
              Projectiles.Spawn {
                Pos = pos
                TargetEnemy = shot.Enemy
                LastTargetPos = lastTargetPos
                Damage = shot.Damage
                Speed = speed
                SlowFactor = shot.SlowFactor
                SlowSeconds = shot.SlowSeconds
                SplashRadius = shot.SplashRadius
                ProjectileSprite = shot.ProjectileSprite
              }
            )
          )

          Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.Muzzle, pos)))
    |]

  let private translateProjectileEvents
    (model: WorldModel)
    (events: Projectiles.ProjectileEvent seq)
    : Cmd<WorldMsg>[] =
    [|
      for ev in events do
        match ev with
        | Projectiles.Impact impact ->
          if impact.SplashRadius > 0f then
            // Splash: the blast fans out from the DETONATION POINT to
            // every enemy within radius (flat full damage, no falloff).
            // Each ApplyDamage zero-crossing emits its own Killed →
            // gold + DeathPoof via translateEnemyEvents. A transient
            // read of Positions — cold path, once per impact.
            let positions = model.Enemies.Positions |> AMap.getValue

            for KeyValueV(eid, epos) in positions do
              if Vector2.Distance(epos, impact.Pos) <= impact.SplashRadius then
                Cmd.ofMsg(EnemyMsg(Enemies.ApplyDamage(eid, impact.Damage)))

            Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.Explosion, impact.Pos)))
          else
            Cmd.ofMsg(
              EnemyMsg(Enemies.ApplyDamage(impact.Enemy, impact.Damage))
            )

            if impact.SlowFactor < 1f then
              Cmd.ofMsg(
                EnemyMsg(
                  Enemies.ApplySlow {
                    Enemy = impact.Enemy
                    Factor = impact.SlowFactor
                    Seconds = impact.SlowSeconds
                  }
                )
              )

            Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.Impact, impact.Pos)))
    |]

  /// Router — dispatch + translate only. No game logic.
  let update
    (msg: WorldMsg)
    (model: WorldModel)
    : struct (WorldModel * Cmd<WorldMsg>) =
    match msg with
    | RoomTick gt ->
      let dt = float32 gt.ElapsedGameTime.TotalSeconds
      let t0 = Diagnostics.tickStart()
      let aliveCount = model.Enemies.Alive |> AMap.count

      // Kimo's system organization: movement/"physics" first, then the
      // spawn/queue phases; read-only consumers after.
      let struct (_, enemyEvents) =
        Enemies.Enemies.tick dt model.Enemies model.Map.Path

      let struct (_, spawnEvents) = Spawning.Spawning.tick dt model.Spawning

      let struct (_, waveEvents) =
        Waves.Waves.tick
          dt
          model.Waves
          aliveCount
          (model.Spawning.Queue.Count = 0)

      let struct (_, towerEvents) =
        // LAZY SETTLE IS BOTTOM-UP (Phase 6 learning): a transform node
        // (chooseA/filter) only pushes downstream when it is itself
        // read, and MapCountNode gates on its DIRECT source's version —
        // so reading the Suppression tail alone would serve the value
        // from the last time the middle of the chain was read (here:
        // never → permanently stale). Read BossPositions first: its
        // rescan journals the per-tower filters, and the Suppression
        // read then settles the whole chain in the same frame.
        let _bossPositions = model.Enemies.BossPositions |> AMap.getValue

        Towers.Towers.tick
          dt
          model.Towers
          model.Enemies.Alive
          (model.Projections.Suppression |> AMap.getValue)
          (cellSize model)

      let struct (_, projectileEvents) =
        Projectiles.Projectiles.tick
          dt
          model.Projectiles
          (model.Enemies.Positions |> AMap.getValue)

      Vfx.Vfx.tick dt model.Vfx
      Camera.Camera.tick dt model.Camera
      Diagnostics.tickEnd t0 model.Diag aliveCount model.Spawning.Queue.Count

      model,
      Cmd.batch [|
        yield! translateEnemyEvents model enemyEvents
        yield! translateSpawnEvents spawnEvents
        yield! translateWaveEvents model.Config.WaveClearBonus waveEvents model
        yield! translateTowerEvents model towerEvents
        yield! translateProjectileEvents model projectileEvents
      |]
    | StartNextWave ->
      if AVal.getValue model.Economy.GameOver then
        model, Cmd.none
      else
        let struct (_, events) =
          Waves.Waves.update Waves.WaveMsg.StartNextWave model.Waves

        model,
        Cmd.batch(translateWaveEvents model.Config.WaveClearBonus events model)
    | UpgradeTower cell ->
      // Cold path — resolve the tower under the cursor, validate gold
      // and the level cap, then write (Towers.Upgrade) + pay.
      match model.Towers.CellIndex |> CMap.tryGetValue cell with
      | ValueNone -> model, Cmd.none
      | ValueSome tid ->
        let level =
          model.Towers.Levels
          |> CMap.tryGetValue tid
          |> ValueOption.defaultValue 1

        let def =
          model.Towers.Statics
          |> CMap.tryGetValue tid
          |> ValueOption.map(fun s -> s.Def)
          |> ValueOption.defaultValue TowerDefs.arrow

        let capped = level >= def.MaxLevel
        let affordable = AVal.getValue model.Economy.Gold >= def.UpgradeCost

        if capped || not affordable then
          model, Cmd.none
        else
          model,
          Cmd.batch [|
            Cmd.ofMsg(TowersMsg(Towers.Upgrade tid))
            Cmd.ofMsg(EconomyMsg(Economy.SpendGold def.UpgradeCost))
          |]

    | PlaceTower cell ->
      // Cold path — the router builds the placement query per message
      // (closure query record): buildable tile, occupancy, gold.
      let def = AVal.getValue model.SelectedTower
      let struct (cx, cy) = cell

      let tileOk = MapModel.isBuildable cx cy model.Map

      let occupied =
        ValueOption.isSome(model.Towers.CellIndex |> CMap.tryGetValue cell)

      let affordable = AVal.getValue model.Economy.Gold >= def.Cost

      if tileOk && not occupied && affordable then
        model,
        Cmd.batch [|
          Cmd.ofMsg(TowersMsg(Towers.Place(cell, def)))
          Cmd.ofMsg(EconomyMsg(Economy.SpendGold def.Cost))
          Cmd.ofMsg(
            VfxMsg(
              Vfx.Burst(
                Vfx.VfxKind.Placement,
                Cells.center cell (cellSize model)
              )
            )
          )
        |]
      else
        model, Cmd.none
    | EnemyMsg m ->
      let struct (_, events) =
        Enemies.Enemies.update m model.Enemies model.Map.Path

      model, Cmd.batch(translateEnemyEvents model events)
    | SpawningMsg m ->
      let struct (_, events) = Spawning.Spawning.update m model.Spawning
      model, Cmd.batch(translateSpawnEvents events)
    | WavesMsg m ->
      let struct (_, events) = Waves.Waves.update m model.Waves

      model,
      Cmd.batch(translateWaveEvents model.Config.WaveClearBonus events model)
    | TowersMsg m ->
      let struct (_, events) = Towers.Towers.update m model.Towers
      model, Cmd.batch(translateTowerEvents model events)
    | ProjectilesMsg m ->
      let struct (_, events) =
        Projectiles.Projectiles.update m model.Projectiles

      model, Cmd.batch(translateProjectileEvents model events)
    | VfxMsg m ->
      Vfx.Vfx.update m model.Vfx
      model, Cmd.none
    | EconomyMsg m ->
      Economy.Economy.update m model.Economy
      model, Cmd.none
    | CameraMsg m ->
      model.Camera <- Camera.Camera.update m model.Camera
      model, Cmd.none
    | SelectTower def ->
      model.SelectedTower |> CVal.set def
      model, Cmd.none

  // ── Placement preview + range ring (hover overlays) ──
  let inline drawOutline
    size
    (color: Mibo.Color)
    (model: WorldModel)
    (buffer: RenderBuffer2D)
    =
    let cell = model.HoverCell |> AVal.getValue

    cell
    |> ValueOption.iter(fun c ->
      let struct (hx, hy) = c
      let p = CellGrid2D.getWorldPos hx hy (MapModel.terrain model.Map)

      buffer
        .rectOutline(
          p.X,
          p.Y,
          size,
          size,
          color,
          thickness = 2f,
          layer = Layers.Effects
        )
        .drop())

  let private hoverOverlays (model: WorldModel) (buffer: RenderBuffer2D) =
    let size = float32 Tiles.TileSize

    // Placement preview: the hovered cell's build status.

    match AVal.getValue model.Projections.PlacementPreview with
    | PlacementStatus.Hidden -> ()
    | PlacementStatus.Blocked -> drawOutline size Mibo.Color.Red model buffer
    | PlacementStatus.Affordable ->
      drawOutline size Mibo.Color.Green model buffer
    | PlacementStatus.TooExpensive ->
      drawOutline size (Mibo.Color.rgb 255uy 210uy 0uy) model buffer

    // Range ring: hovering an own tower shows its range circle.
    let rangeRing = AVal.getValue model.Projections.RangeRing
    let hoverCel = AVal.getValue model.HoverCell

    hoverCel
    |> ValueOption.iter2
      (fun def c ->
        let center = Cells.center c (cellSize model)

        buffer
          .circleOutline(
            center,
            float32 def.Range * size,
            Mibo.Color.Blue,
            layer = Layers.Effects
          )
          .drop())
      rangeRing


  let view (ctx: GameContext) (model: WorldModel) (buffer: RenderBuffer2D) =
    let size = cellSize model
    let viewport = Vector2(float32 ctx.WindowWidth, float32 ctx.WindowHeight)

    // Camera block: the subsystem clamps + shakes + records the
    // underlying camera; everything world-space renders inside; the
    // HUD renderer (separate noClear pass) owns screen space.
    Camera.beginFrame model.Camera viewport buffer

    let visible = Camera.cullingBounds model.Camera viewport

    Map.view ctx model.Map visible buffer
    Towers.Towers.view ctx model.Towers size buffer
    Enemies.Enemies.view ctx model.Enemies model.Map.Path buffer

    Projectiles.Projectiles.view
      ctx
      model.Projectiles
      model.Projections.Homing
      buffer

    Vfx.Vfx.view ctx model.Vfx buffer
    hoverOverlays model buffer

    buffer.endCamera(layer = Layers.Effects).drop()

  /// Screen-space HUD pass (own renderer, noClear): reads the avals
  /// transiently at view time — no view caches.
  let hudView (ctx: GameContext) (model: WorldModel) (buffer: RenderBuffer2D) =
    let font = Raylib.GetFontDefault()
    let gold = AVal.getValue model.Economy.Gold
    let lives = AVal.getValue model.Economy.Lives
    let banner = AVal.getValue model.Waves.Banner
    let def = AVal.getValue model.SelectedTower

    buffer
      .text(
        font,
        $"Gold: %d{gold}   Lives: %d{lives}   %s{banner}   Tower: %s{def.Name} (1/2/3)",
        Vector2(12f, 10f),
        22f,
        layer = Layers.Hud
      )
      .drop()

    buffer
      .text(
        font,
        "WASD/arrows or middle-drag: pan   wheel: zoom   Home: reset   right-click: upgrade",
        Vector2(12f, float32 ctx.WindowHeight - 30f),
        16f,
        layer = Layers.Hud
      )
      .drop()

    if AVal.getValue model.Economy.GameOver then
      buffer
        .text(
          font,
          "GAME OVER — press R to restart",
          Vector2(430f, 360f),
          40f,
          layer = Layers.Hud
        )
        .drop()
