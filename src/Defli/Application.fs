namespace Defli

open System.Numerics
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Mibo.Input
open Mibo.Layout
open Raylib_cs
open Defli.World
open Defli.World.Systems

// ─────────────────────────────────────────────────────────────
// Shell — the thin MVU layer: input mapping, window config,
// renderer wiring. Holds NO gameplay state beyond the world
// reference itself; all game state lives in WorldModel.
// ─────────────────────────────────────────────────────────────

[<Struct>]
type GameAction =
  | StartNextWave
  | ToggleDiagnostics

type Model() =
  member val World: WorldModel = Unchecked.defaultof<_> with get, set

  member val Input: ActionState<GameAction> =
    Unchecked.defaultof<_> with get, set

  member val MousePos: Vector2 = Unchecked.defaultof<_> with get, set
  /// Main-MVU frame diagnostics (Kimo FrameDiag, simplified).
  member val Diag = FrameDiag() with get, set


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
    |> InputMap.key GameAction.ToggleDiagnostics KeyCode.F3

module Application =
  open AdaptiveSlop.Core

  /// Cursor compensation (pixels) applied to the raw mouse position before
  /// cell conversion. Tune this single constant if the hover/click highlight
  /// disagrees with the OS cursor — do not touch Mibo's worldToCell.
  let cursorOffset = Vector2(-24f, -24f)

  let init(_ctx: GameContext) : struct (Model * Cmd<Msg>) =
    let world = World.init WorldConfig.defaults

    let model =
      Model(World = world, Input = ActionState.empty, MousePos = Vector2.Zero)

    model, Cmd.none

  let update (msg: Msg) (model: Model) : struct (Model * Cmd<Msg>) =
    match msg with
    | Tick gt ->
      Diagnostics.update model.Diag
      let struct (world, cmd) = World.update (RoomTick gt) model.World
      model.World <- world
      model, Cmd.map WorldMsg cmd
    | InputChanged inputs ->
      let started = inputs.Started

      if started.Contains GameAction.ToggleDiagnostics then
        model.Diag.Visible <- not model.Diag.Visible

      let cmd =
        if started.Contains GameAction.StartNextWave then
          Cmd.ofMsg(WorldMsg WorldMsg.StartNextWave)
        else
          Cmd.none

      model.Input <- inputs
      model, cmd
    | MouseMoved pos ->
      model.MousePos <- pos

      // Hover cell — shell writes the CVal the world projections join on.
      let cell =
        model.World.Map
        |> MapModel.terrain
        |> Grid2DSpatial.worldToCell(pos + cursorOffset)

      model.World.HoverCell |> CVal.set cell
      model, Cmd.none
    | MouseClicked pos ->
      // Click → cell (tower placement intent; the router validates).
      let cell =
        model.World.Map
        |> MapModel.terrain
        |> Grid2DSpatial.worldToCell(pos + cursorOffset)

      match cell with
      | ValueSome c -> model, Cmd.ofMsg(WorldMsg(PlaceTower c))
      | ValueNone -> model, Cmd.none
    | WorldMsg wm ->
      let struct (world, cmd) = World.update wm model.World
      model.World <- world
      model, Cmd.map WorldMsg cmd

  let view (ctx: GameContext) (model: Model) (buffer: RenderBuffer2D) =
    Diagnostics.drawn (Diagnostics.tickStart()) model.Diag
    World.view ctx model.World buffer

    if model.Diag.Visible then
      let font = Raylib.GetFontDefault()
      buffer.frameDiagnostics(font, model.Diag, Vector2(12f, 40f)).drop()
      buffer.worldDiagnostics(font, model.World.Diag, Vector2(12f, 64f)).drop()

  let subscribe (ctx: GameContext) (_model: Model) : Sub<Msg> =
    Sub.batch [
      InputMapper.subscribeStatic Inputs.map InputChanged ctx
      Mouse.onMove MouseMoved ctx
      Mouse.onLeftClick MouseClicked ctx
    ]
