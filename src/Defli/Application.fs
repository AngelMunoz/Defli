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
  | SelectArrow
  | SelectFrost
  | Restart
  | ResetCamera
  | PanLeft
  | PanRight
  | PanUp
  | PanDown

type Model() =
  member val World: WorldModel = Unchecked.defaultof<_> with get, set

  member val Input: ActionState<GameAction> =
    Unchecked.defaultof<_> with get, set

  member val MousePos: Vector2 = Unchecked.defaultof<_> with get, set
  /// Window size in pixels — the render-time constant the camera
  /// conversions need (set once at init; the sim stays headless).
  member val Viewport: Vector2 = Unchecked.defaultof<_> with get, set
  /// Middle-button held → drag pans the camera.
  member val MiddleDown = false with get, set
  /// Main-MVU frame diagnostics (Kimo FrameDiag, simplified).
  member val Diag = FrameDiag() with get, set


[<Struct>]
type Msg =
  | Tick of tick: GameTime
  | InputChanged of inputs: ActionState<GameAction>
  | MouseDelta of delta: MouseDelta
  | MouseClicked of pos: Vector2
  | MouseRightClicked of pos: Vector2
  | WorldMsg of worldMsg: WorldMsg

module Inputs =

  let map =
    InputMap.empty
    |> InputMap.key GameAction.StartNextWave KeyCode.Space
    |> InputMap.key GameAction.StartNextWave KeyCode.Enter
    |> InputMap.key GameAction.ToggleDiagnostics KeyCode.F3
    |> InputMap.key GameAction.SelectArrow KeyCode.D1
    |> InputMap.key GameAction.SelectFrost KeyCode.D2
    |> InputMap.key GameAction.Restart KeyCode.R
    |> InputMap.key GameAction.ResetCamera KeyCode.Home
    |> InputMap.key GameAction.PanLeft KeyCode.A
    |> InputMap.key GameAction.PanLeft KeyCode.Left
    |> InputMap.key GameAction.PanRight KeyCode.D
    |> InputMap.key GameAction.PanRight KeyCode.Right
    |> InputMap.key GameAction.PanUp KeyCode.W
    |> InputMap.key GameAction.PanUp KeyCode.Up
    |> InputMap.key GameAction.PanDown KeyCode.S
    |> InputMap.key GameAction.PanDown KeyCode.Down

module Application =
  open AdaptiveSlop.Core

  /// The grid cell CONTAINING a world position (floor of world/size) —
  /// the tile under the cursor. Mibo's Grid2DSpatial.worldToCell rounds
  /// to the NEAREST CENTER (a cursor in the right/bottom half of a tile
  /// picks the NEXT one — the outline visibly cuts tiles in half); the
  /// game wants the containing tile, so the pick is floor-based and
  /// bounds-checked. Origin-aware (the map origin is Zero).
  let inline cellAt
    (worldPos: Vector2)
    (grid: CellGrid2D<MapTile>)
    : struct (int * int) voption =
    // floor, not int: int truncates toward zero, which would map a
    // position just left of the origin into cell 0.
    let x =
      int (floor ((worldPos.X - grid.Origin.X) / grid.CellSize.X))

    let y =
      int (floor ((worldPos.Y - grid.Origin.Y) / grid.CellSize.Y))

    if x >= 0 && x < grid.Width && y >= 0 && y < grid.Height then
      ValueSome(struct (x, y))
    else
      ValueNone

  /// Keyboard pan speed in screen pixels per second (the Camera
  /// subsystem converts by its zoom — panning feels constant on screen).
  let panSpeed = 500f

  let init(ctx: GameContext) : struct (Model * Cmd<Msg>) =
    // The raylib loader forces TRILINEAR filtering on every texture
    // (docs/assets.md): a gutterless spritesheet sampled bilinearly at
    // tile borders bleeds adjacent (black) texels in — the seam lines
    // between tiles. Point filtering stops it. Applied ONCE at init
    // (mutates the cached texture's sampler — not per frame).
    GameContext.getService<IAssets> ctx
    |> fun assets ->
      assets.Texture Tiles.SheetPath
      |> Texture.filter TextureFilter.Point
      |> ignore

    let world = World.init WorldConfig.defaults

    let model =
      Model(
        World = world,
        Input = ActionState.empty,
        MousePos = Vector2.Zero,
        Viewport = Vector2(float32 ctx.WindowWidth, float32 ctx.WindowHeight)
      )

    // Render-time fact, once at boot: the camera's screen offset
    // (window size — the window is fixed).
    Camera.Camera.setViewport model.Viewport model.World.Camera

    model, Cmd.none

  let update (msg: Msg) (model: Model) : struct (Model * Cmd<Msg>) =
    match msg with
    | Tick gt ->
      Diagnostics.update model.Diag
      let struct (world, cmd) = World.update (RoomTick gt) model.World
      model.World <- world

      // Keyboard pan: the pressed key moves the CAMERA (Up → the view
      // pans north). Camera.Pan subtracts its input (drag semantics:
      // the world follows the cursor), so keyboard deltas carry the
      // OPPOSITE sign of the drag they mirror.
      let dt = float32 gt.ElapsedGameTime.TotalSeconds
      let held = model.Input.Held
      let mutable dx = 0f
      let mutable dy = 0f

      if held.Contains GameAction.PanLeft then
        dx <- dx + 1f

      if held.Contains GameAction.PanRight then
        dx <- dx - 1f

      if held.Contains GameAction.PanUp then
        dy <- dy + 1f

      if held.Contains GameAction.PanDown then
        dy <- dy - 1f

      let panCmd =
        if dx <> 0f || dy <> 0f then
          Cmd.ofMsg(
            WorldMsg(
              WorldMsg.CameraMsg(Camera.Pan(Vector2(dx, dy) * panSpeed * dt))
            )
          )
        else
          Cmd.none

      model, Cmd.batch [| Cmd.map WorldMsg cmd; panCmd |]
    | InputChanged inputs ->
      let started = inputs.Started

      if started.Contains GameAction.ToggleDiagnostics then
        model.Diag.Visible <- not model.Diag.Visible

      // Restart: only from the game-over state (misclicks must not
      // wipe a run) — fresh world from the same config. The new
      // camera's screen offset is a render-time fact the sim can't
      // know (init leaves it Zero), so re-apply the viewport here
      // just like boot — otherwise the world center pins to (0,0)
      // and only a quarter of the map is visible.
      if
        started.Contains GameAction.Restart
        && AVal.getValue model.World.Economy.GameOver
      then
        model.World <- World.init WorldConfig.defaults
        Camera.Camera.setViewport model.Viewport model.World.Camera

      let cmd =
        if started.Contains GameAction.StartNextWave then
          Cmd.ofMsg(WorldMsg WorldMsg.StartNextWave)
        elif started.Contains GameAction.SelectArrow then
          Cmd.ofMsg(WorldMsg(WorldMsg.SelectTower TowerDefs.arrow))
        elif started.Contains GameAction.SelectFrost then
          Cmd.ofMsg(WorldMsg(WorldMsg.SelectTower TowerDefs.frost))
        elif started.Contains GameAction.ResetCamera then
          Cmd.ofMsg(WorldMsg(WorldMsg.CameraMsg Camera.Reset))
        else
          Cmd.none

      model.Input <- inputs
      model, cmd
    | MouseDelta delta ->
      model.MousePos <- delta.Position

      if delta.Buttons.Pressed |> Array.contains MouseButtonCode.Middle then
        model.MiddleDown <- true

      if delta.Buttons.Released |> Array.contains MouseButtonCode.Middle then
        model.MiddleDown <- false

      // Hover cell — shell writes the CVal the world projections join on
      // (screen → world through the camera, then the containing cell).
      let worldPos =
        Camera.screenToWorld model.World.Camera model.Viewport delta.Position

      let cell =
        model.World.Map |> MapModel.terrain |> cellAt worldPos

      model.World.HoverCell |> CVal.set cell

      let cmd =
        if delta.ScrollDelta <> 0f then
          // Wheel zoom: multiplicative steps toward the camera target.
          let factor = float32(1.1 ** float delta.ScrollDelta)
          Cmd.ofMsg(WorldMsg(WorldMsg.CameraMsg(Camera.ZoomBy factor)))
        elif model.MiddleDown && delta.PositionDelta <> Vector2.Zero then
          // Middle-drag pan: world moves opposite the drag (screen px).
          Cmd.ofMsg(
            WorldMsg(WorldMsg.CameraMsg(Camera.Pan delta.PositionDelta))
          )
        else
          Cmd.none

      model, cmd

    | MouseClicked pos ->
      // Click → cell (tower placement intent; the router validates).
      let worldPos =
        Camera.screenToWorld model.World.Camera model.Viewport pos

      let cell =
        model.World.Map |> MapModel.terrain |> cellAt worldPos

      match cell with
      | ValueSome c -> model, Cmd.ofMsg(WorldMsg(PlaceTower c))
      | ValueNone -> model, Cmd.none
    | MouseRightClicked pos ->
      // Right-click → cell (tower upgrade intent; the router resolves
      // the tower and validates gold/cap).
      let worldPos =
        Camera.screenToWorld model.World.Camera model.Viewport pos

      let cell =
        model.World.Map |> MapModel.terrain |> cellAt worldPos

      match cell with
      | ValueSome c -> model, Cmd.ofMsg(WorldMsg(UpgradeTower c))
      | ValueNone -> model, Cmd.none
    | WorldMsg wm ->
      let struct (world, cmd) = World.update wm model.World
      model.World <- world
      model, Cmd.map WorldMsg cmd

  let view (ctx: GameContext) (model: Model) (buffer: RenderBuffer2D) =
    Diagnostics.drawn (Diagnostics.tickStart()) model.Diag
    World.view ctx model.World buffer

  /// HUD pass — its OWN renderer (noClear) so screen-space UI draws
  /// over the camera'd world (layered-rendering pattern).
  let hudView (ctx: GameContext) (model: Model) (buffer: RenderBuffer2D) =
    World.hudView ctx model.World buffer

    if model.Diag.Visible then
      let font = Raylib.GetFontDefault()
      buffer.frameDiagnostics(font, model.Diag, Vector2(12f, 40f)).drop()
      buffer.worldDiagnostics(font, model.World.Diag, Vector2(12f, 64f)).drop()

  let subscribe (ctx: GameContext) (_model: Model) : Sub<Msg> =
    Sub.batch [
      InputMapper.subscribeStatic Inputs.map InputChanged ctx
      Mouse.listen MouseDelta ctx
      Mouse.onLeftClick MouseClicked ctx
      Mouse.onRightClick MouseRightClicked ctx
    ]
