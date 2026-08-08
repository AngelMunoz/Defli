namespace Defli.World

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
  | EnemyMsg of msgE: Enemies.EnemyMsg
  | SpawningMsg of msgS: Spawning.SpawnMsg
  | WavesMsg of msgW: Waves.WaveMsg
  | TowersMsg of msgT: Towers.TowerMsg
  | ProjectilesMsg of msgP: Projectiles.ProjectileMsg
  | VfxMsg of msgV: Vfx.VfxMsg
  | EconomyMsg of msgEC: Economy.EconomyMsg

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
  member val Projections: Projections = Unchecked.defaultof<_> with get, set
  /// Hover cell CVal — UI state written by the shell on MouseMoved;
  /// the world projections (PlacementPreview/RangeRing) join on it.
  member val HoverCell = CVal.create ValueNone with get, set
  /// World-sim diagnostics (sampled inside RoomTick — Kimo WorldDiag).
  member val Diag = WorldDiag() with get, set

module World =
  open AdaptiveSlop.Core

  let init(cfg: WorldConfig) : WorldModel =
    let model = WorldModel()
    model.Config <- cfg
    model.Map <- MapModel.create cfg
    model.Spawning <- Spawning.Spawning.init cfg.Seed
    model.Economy <- Economy.Economy.init cfg
    model.Projections <-
      Projections(
        model.Enemies,
        model.Towers,
        model.Projectiles,
        model.Economy,
        model.Map.Grid,
        model.HoverCell
      )

    model

  let cellSize(model: WorldModel) =
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
            match model.Enemies.Positions |> CMap.tryGetValue eid with
            | ValueSome p -> p
            | ValueNone -> Vector2.Zero

          Cmd.ofMsg(EconomyMsg(Economy.EarnGold reward))
          Cmd.ofMsg(EnemyMsg(Enemies.Despawn eid))
          Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.DeathPoof, pos)))
        | Enemies.ReachedBase _ -> Cmd.ofMsg(EconomyMsg Economy.LoseLife)
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
    (events: Waves.WaveEvent[])
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
        | Towers.Fired(tid, eid, damage) ->
          let struct (pos, speed) =
            match model.Towers.Statics |> CMap.tryGetValue tid with
            | ValueSome s -> struct (Cells.center s.Cell (cellSize model), s.Def.ProjectileSpeed)
            | ValueNone -> struct (Vector2.Zero, 0f)

          Cmd.ofMsg(ProjectilesMsg(Projectiles.Spawn(pos, eid, damage, speed)))
          Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.Muzzle, pos)))
    |]

  let private translateProjectileEvents
    (events: Projectiles.ProjectileEvent seq)
    : Cmd<WorldMsg>[] =
    [|
      for ev in events do
        match ev with
        | Projectiles.Impact(_, eid, damage, pos) ->
          Cmd.ofMsg(EnemyMsg(Enemies.ApplyDamage(eid, damage)))
          Cmd.ofMsg(VfxMsg(Vfx.Burst(Vfx.VfxKind.Impact, pos)))
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

      // Kimo's system organization: movement/"physics" first, then the
      // spawn/queue phases; read-only consumers after.
      let struct (_, enemyEvents) =
        Enemies.Enemies.tick dt model.Enemies model.Map.Path

      let struct (_, spawnEvents) = Spawning.Spawning.tick dt model.Spawning

      // One transient read of Alive per frame — the targeting query
      // (Towers) and the wave-clear check share it; the count is the
      // dictionary's, so the AliveCount node is not pulled twice.
      let alive = model.Enemies.Alive |> AMap.getValue

      let struct (_, waveEvents) =
        Waves.Waves.tick dt model.Waves alive.Count (model.Spawning.Queue.Count = 0)

      let struct (_, towerEvents) =
        Towers.Towers.tick dt model.Towers alive (cellSize model)

      let struct (_, projectileEvents) =
        Projectiles.Projectiles.tick
          dt
          model.Projectiles
          (model.Enemies.Positions |> AMap.getValue)

      Vfx.Vfx.tick dt model.Vfx
      Diagnostics.tickEnd t0 model.Diag alive.Count model.Spawning.Queue.Count

      model,
      Cmd.batch [|
        yield! translateEnemyEvents model enemyEvents
        yield! translateSpawnEvents spawnEvents
        yield! translateWaveEvents model.Config.WaveClearBonus waveEvents model
        yield! translateTowerEvents model towerEvents
        yield! translateProjectileEvents projectileEvents
      |]
    | StartNextWave ->
      if AVal.getValue model.Economy.GameOver then
        model, Cmd.none
      else
        let struct (_, events) =
          Waves.Waves.update Waves.WaveMsg.StartNextWave model.Waves

        model,
        Cmd.batch(translateWaveEvents model.Config.WaveClearBonus events model)
    | PlaceTower cell ->
      // Cold path — the router builds the placement query per message
      // (closure query record): buildable tile, occupancy, gold.
      let def = TowerDefs.arrow
      let struct (cx, cy) = cell

      let tileOk =
        match CellGrid2D.get cx cy model.Map.Grid with
        | ValueSome t -> t.Buildable
        | ValueNone -> false

      let occupied = (model.Towers.CellIndex |> CMap.tryGetValue cell).IsSome
      let affordable = AVal.getValue model.Economy.Gold >= def.Cost

      if tileOk && not occupied && affordable then
        model,
        Cmd.batch [|
          Cmd.ofMsg(TowersMsg(Towers.Place(cell, def)))
          Cmd.ofMsg(EconomyMsg(Economy.SpendGold def.Cost))
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
      let struct (_, events) = Projectiles.Projectiles.update m model.Projectiles
      model, Cmd.batch(translateProjectileEvents events)
    | VfxMsg m ->
      Vfx.Vfx.update m model.Vfx
      model, Cmd.none
    | EconomyMsg m ->
      Economy.Economy.update m model.Economy
      model, Cmd.none

  // ── HUD (minimal, inline — full HUD lands in Phase 4) ──

  let private hudView
    (ctx: GameContext)
    (model: WorldModel)
    (buffer: RenderBuffer2D)
    =
    let font = Raylib.GetFontDefault()
    let gold = AVal.getValue model.Economy.Gold
    let lives = AVal.getValue model.Economy.Lives
    let banner = AVal.getValue model.Waves.Banner

    buffer
      .text(
        font,
        sprintf "Gold: %d   Lives: %d   %s" gold lives banner,
        Vector2(12f, 10f),
        22f,
        layer = Layers.Hud
      )
      .drop()

    if AVal.getValue model.Economy.GameOver then
      buffer
        .text(font, "GAME OVER", Vector2(480f, 340f), 48f, layer = Layers.Hud)
        .drop()

  // ── Placement preview + range ring (hover overlays) ──

  let private hoverOverlays
    (model: WorldModel)
    (buffer: RenderBuffer2D)
    =
    let size = float32 Tiles.TileSize

    // Placement preview: the hovered cell's build status.
    let drawOutline (color: Mibo.Color) =
      match model.HoverCell.Value with
      | ValueSome c ->
        let struct (hx, hy) = c
        let p = CellGrid2D.getWorldPos hx hy model.Map.Grid

        buffer
          .rectOutline(
            p.X,
            p.Y,
            size,
            size,
            color,
            thickness = 2f,
            layer = Layers.Hud
          )
          .drop()
      | ValueNone -> ()

    match AVal.getValue model.Projections.PlacementPreview with
    | PlacementStatus.Hidden -> ()
    | PlacementStatus.Blocked -> drawOutline Mibo.Color.Red
    | PlacementStatus.Affordable -> drawOutline Mibo.Color.Green
    | PlacementStatus.TooExpensive -> drawOutline (Mibo.Color.rgb 255uy 210uy 0uy)

    // Range ring: hovering an own tower shows its range circle.
    match AVal.getValue model.Projections.RangeRing with
    | ValueSome def ->
      match model.HoverCell.Value with
      | ValueSome c ->
        let center = Cells.center c (cellSize model)

        buffer
          .circleOutline(
            center,
            float32 def.Range * size,
            Mibo.Color.Blue,
            layer = Layers.Effects
          )
          .drop()
      | ValueNone -> ()
    | ValueNone -> ()

  let view
    (ctx: GameContext)
    (model: WorldModel)
    (buffer: RenderBuffer2D)
    =
    let size = cellSize model
    Map.view ctx model.Map buffer
    Towers.Towers.view ctx model.Towers size buffer
    Enemies.Enemies.view ctx model.Enemies model.Map.Path buffer
    Projectiles.Projectiles.view ctx model.Projectiles model.Projections.Homing buffer
    Vfx.Vfx.view ctx model.Vfx buffer
    hoverOverlays model buffer
    hudView ctx model buffer
