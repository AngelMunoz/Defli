namespace Defli

open System.Numerics
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Mibo.Input
open Mibo.Layout
open Defli.World

// ─────────────────────────────────────────────────────────────
// Shell — the thin MVU layer: input mapping, window config,
// renderer wiring. Holds NO gameplay state beyond the world
// reference itself; all game state lives in WorldModel.
// ─────────────────────────────────────────────────────────────

[<Struct>]
type GameAction = | StartNextWave

type Model = {
  World: WorldModel
  Input: ActionState<GameAction>
  MousePos: Vector2
}

[<Struct>]
type Msg =
  | Tick of tick: GameTime
  | InputChanged of inputs: ActionState<GameAction>
  | MouseMoved of pos: Vector2
  | MouseClicked of pos: Vector2
  | WorldMsg of worldMsg: WorldMsg

module Inputs =

  let map =
    InputMap.empty
    |> InputMap.key GameAction.StartNextWave KeyCode.Space
    |> InputMap.key GameAction.StartNextWave KeyCode.Enter

module Application =

  let init(_ctx: GameContext) : struct (Model * Cmd<Msg>) =
    let world = World.init WorldConfig.defaults

    {
      World = world
      Input = ActionState.empty
      MousePos = Vector2.Zero
    },
    Cmd.none

  let update (msg: Msg) (model: Model) : struct (Model * Cmd<Msg>) =
    match msg with
    | Tick gt ->
      let struct (world, cmd) = World.update (RoomTick gt) model.World
      { model with World = world }, Cmd.map WorldMsg cmd
    | InputChanged inputs ->
      let cmd =
        if inputs.Started.Contains GameAction.StartNextWave then
          Cmd.ofMsg(WorldMsg WorldMsg.StartNextWave)
        else
          Cmd.none

      { model with Input = inputs }, cmd
    | MouseMoved pos -> { model with MousePos = pos }, Cmd.none
    | MouseClicked pos ->
      // Click → cell (tower placement intent; Towers validates in Phase 2).
      let cell = Grid2DSpatial.worldToCell pos model.World.Map.Grid

      match cell with
      | ValueSome c -> model, Cmd.ofMsg(WorldMsg(PlaceTower c))
      | ValueNone -> model, Cmd.none
    | WorldMsg wm ->
      let struct (world, cmd) = World.update wm model.World
      { model with World = world }, Cmd.map WorldMsg cmd

  let view (ctx: GameContext) (model: Model) (buffer: RenderBuffer2D) =
    let hoverCell =
      Grid2DSpatial.worldToCell model.MousePos model.World.Map.Grid

    World.view ctx model.World hoverCell buffer

  let subscribe (ctx: GameContext) (_model: Model) : Sub<Msg> =
    Sub.batch [
      InputMapper.subscribeStatic Inputs.map InputChanged ctx
      Mouse.onMove MouseMoved ctx
      Mouse.onLeftClick MouseClicked ctx
    ]
