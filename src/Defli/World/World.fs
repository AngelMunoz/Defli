namespace Defli.World

open System.Numerics
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Raylib_cs
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
  | EconomyMsg of msgEC: Economy.EconomyMsg

type WorldModel() =
  member val Config: WorldConfig = Unchecked.defaultof<_> with get, set
  member val Map: MapModel = Unchecked.defaultof<_> with get, set
  member val Enemies = Enemies.Enemies.init() with get, set
  member val Spawning = Spawning.Spawning.init 0 with get, set
  member val Waves = Waves.Waves.init() with get, set
  member val Economy = Economy.Economy.init WorldConfig.defaults with get, set
  member val Projections: Projections = Projections() with get, set

module World =
  open AdaptiveSlop.Core

  let init(cfg: WorldConfig) : WorldModel =
    let model = WorldModel()
    model.Config <- cfg
    model.Map <- MapModel.create cfg
    model.Enemies <- Enemies.Enemies.init()
    model.Spawning <- Spawning.Spawning.init cfg.Seed
    model.Waves <- Waves.Waves.init()
    model.Economy <- Economy.Economy.init cfg
    model

  // ── Event → Cmd translation (the router's only job) ──

  let private translateEnemyEvents
    (events: Enemies.EnemyEvent[])
    : Cmd<WorldMsg>[] =
    [|
      for ev in events do
        match ev with
        | Enemies.Killed(eid, reward) ->
          Cmd.ofMsg(EconomyMsg(Economy.EarnGold reward))
          Cmd.ofMsg(EnemyMsg(Enemies.Despawn eid))
        | Enemies.ReachedBase _ -> Cmd.ofMsg(EconomyMsg Economy.LoseLife)
    |]

  let private translateSpawnEvents
    (events: Spawning.SpawnEvent[])
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
          yield Cmd.ofMsg(EconomyMsg(Economy.EarnGold waveClearBonus))
    |]

  /// Router — dispatch + translate only. No game logic.
  let update
    (msg: WorldMsg)
    (model: WorldModel)
    : struct (WorldModel * Cmd<WorldMsg>) =
    match msg with
    | RoomTick gt ->
      let dt = float32 gt.ElapsedGameTime.TotalSeconds

      // Kimo's system organization: movement/"physics" first, then the
      // spawn/queue phases; read-only consumers after.
      let struct (_, enemyEvents) =
        Enemies.Enemies.tick dt model.Enemies model.Map.Path

      let struct (_, spawnEvents) = Spawning.Spawning.tick dt model.Spawning

      let aliveCount = AVal.getValue model.Enemies.AliveCount
      let queueEmpty = model.Spawning.Queue.Count = 0

      let struct (_, waveEvents) =
        Waves.Waves.tick dt model.Waves aliveCount queueEmpty

      let cmds =
        Array.concat [
          translateEnemyEvents enemyEvents
          translateSpawnEvents spawnEvents
          translateWaveEvents model.Config.WaveClearBonus waveEvents model
        ]

      model, Cmd.batch cmds
    | StartNextWave ->
      if AVal.getValue model.Economy.GameOver then
        model, Cmd.none
      else
        let struct (_, events) =
          Waves.Waves.update Waves.WaveMsg.StartNextWave model.Waves

        model,
        Cmd.batch(translateWaveEvents model.Config.WaveClearBonus events model)
    | PlaceTower cell ->
      // → Towers.Place (validation + gold check) in Phase 2.
      model, Cmd.none
    | EnemyMsg m ->
      let struct (_, events) =
        Enemies.Enemies.update m model.Enemies model.Map.Path

      model, Cmd.batch(translateEnemyEvents events)
    | SpawningMsg m ->
      let struct (_, events) = Spawning.Spawning.update m model.Spawning
      model, Cmd.batch(translateSpawnEvents events)
    | WavesMsg m ->
      let struct (_, events) = Waves.Waves.update m model.Waves

      model,
      Cmd.batch(translateWaveEvents model.Config.WaveClearBonus events model)
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

  let view
    (ctx: GameContext)
    (model: WorldModel)
    (hoverCell: struct (int * int) voption)
    (buffer: RenderBuffer2D)
    =
    Map.view ctx model.Map hoverCell buffer
    Enemies.Enemies.view ctx model.Enemies model.Map.Path buffer
    hudView ctx model buffer
