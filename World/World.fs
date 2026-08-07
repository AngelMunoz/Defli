namespace Defli.World

open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Defli.World.Systems

// ─────────────────────────────────────────────────────────────
// World — owns WorldModel, the message router, and the view
// composition. The update is a ROUTER: dispatch + event/intent
// translation only, no game logic. Sub-systems land in later
// phases and each owns its slice (component maps + projections).
// ─────────────────────────────────────────────────────────────

[<Struct>]
type WorldMsg =
  | RoomTick of tick: GameTime
  | StartNextWave
  | PlaceTower of cell: struct (int * int)

type WorldModel() =
  member val Config: WorldConfig = Unchecked.defaultof<_> with get, set
  member val Map: MapModel = Unchecked.defaultof<_> with get, set
  member val Projections: Projections = Projections() with get, set

module World =

  let init(cfg: WorldConfig) : WorldModel =
    let model = WorldModel()
    model.Config <- cfg
    model.Map <- MapModel.create cfg
    model

  /// Router — dispatch + translate only. No game logic.
  let update
    (msg: WorldMsg)
    (model: WorldModel)
    : struct (WorldModel * Cmd<WorldMsg>) =
    match msg with
    | RoomTick _ ->
      // The ordered sim phases (Enemies/Waves/Towers/Projectiles)
      // run here in later phases.
      model, Cmd.none
    | StartNextWave ->
      // → Waves.StartNextWave in Phase 1.
      model, Cmd.none
    | PlaceTower cell ->
      // → Towers.Place (validation + gold check) in Phase 2.
      model, Cmd.none

  let view
    (ctx: GameContext)
    (model: WorldModel)
    (hoverCell: struct (int * int) voption)
    (buffer: RenderBuffer2D)
    =
    Map.view ctx model.Map hoverCell buffer
