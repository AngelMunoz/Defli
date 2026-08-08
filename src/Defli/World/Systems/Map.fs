namespace Defli.World.Systems

open System.Numerics
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Mibo.Layout
open Raylib_cs
open Defli.World

// ─────────────────────────────────────────────────────────────
// Map sub-system — owns the CellGrid2D<MapTile> and the path.
// Static content (built once at world init, never mutated —
// same rule as Kimo's map/stores; NOT adaptive).
// ─────────────────────────────────────────────────────────────

type MapModel = {
  Grid: CellGrid2D<MapTile>
  /// World-space waypoint centers (spawn → base) — the movement
  /// (physics) phase walks these.
  Path: Vector2[]
  SpawnCell: struct (int * int)
  BaseCell: struct (int * int)
}

module MapModel =

  /// Hand-authored Level-1 path, in cells (spawn left → base right).
  let private waypointCells = [|
    struct (0, 4)
    struct (7, 4)
    struct (7, 8)
    struct (14, 8)
    struct (14, 2)
    struct (19, 2)
  |]

  let create(cfg: WorldConfig) : MapModel =
    let cellSize = Vector2(float32 Tiles.TileSize, float32 Tiles.TileSize)
    let grid = CellGrid2D.create cfg.GridCols cfg.GridRows cellSize Vector2.Zero

    // Grass everywhere, buildable.
    for y in 0 .. cfg.GridRows - 1 do
      for x in 0 .. cfg.GridCols - 1 do
        grid
        |> CellGrid2D.set x y {
          Terrain = TerrainKind.Grass
          IsPath = false
          Buildable = true
        }

    // Carve the road: walk each axis-aligned waypoint segment.
    let markPathCell(struct (x, y)) =
      grid
      |> CellGrid2D.set x y {
        Terrain = TerrainKind.Dirt
        IsPath = true
        Buildable = false
      }

    markPathCell waypointCells[0]

    for i in 1 .. waypointCells.Length - 1 do
      let struct (px, py) = waypointCells[i - 1]
      let struct (tx, ty) = waypointCells[i]
      let dx = sign(tx - px)
      let dy = sign(ty - py)
      let mutable x = px
      let mutable y = py

      while x <> tx || y <> ty do
        x <- x + dx
        y <- y + dy
        markPathCell(struct (x, y))

    // World-space waypoint centers.
    let path =
      waypointCells
      |> Array.map(fun (struct (x, y)) ->
        let topLeft = CellGrid2D.getWorldPos x y grid
        topLeft + cellSize / 2f)

    {
      Grid = grid
      Path = path
      SpawnCell = waypointCells[0]
      BaseCell = waypointCells[waypointCells.Length - 1]
    }

module Map =

  /// Picks the path tile frame for a cell from its path neighbors.
  /// Corners fall back to the vertical piece (placeholder — a nicer
  /// corner mapping can land with the Level-2 generator).
  let private pathFrame
    (grid: CellGrid2D<MapTile>)
    (x: int)
    (y: int)
    : struct (TileInfo * float32) =
    let isPath x y =
      match CellGrid2D.get x y grid with
      | ValueSome t -> t.IsPath
      | ValueNone -> false

    let n = isPath x (y - 1)
    let s = isPath x (y + 1)
    let e = isPath (x + 1) y
    let w = isPath (x - 1) y

    let count =
      (if n then 1 else 0)
      + (if s then 1 else 0)
      + (if e then 1 else 0)
      + (if w then 1 else 0)

    match count with
    | 1 ->
      // End piece — rotate so the opening faces the road's continuation.
      if n then struct (Tiles.pathEndUpDirt, 0f)
      elif s then struct (Tiles.pathEndUpDirt, 180f)
      elif e then struct (Tiles.pathEndLeftDirt, 180f)
      else struct (Tiles.pathEndLeftDirt, 0f)
    | 2 when n && s -> struct (Tiles.pathVerticalDirt, 0f)
    | 2 when e && w -> struct (Tiles.pathHorizontalDirt, 0f)
    | _ -> struct (Tiles.pathVerticalDirt, 0f) // straight / corner placeholder

  /// Deterministic grass variety — no RNG needed for static content.
  let private grassVariant (x: int) (y: int) =
    Tiles.groundGrass[(x * 7 + y * 13) % 3]

  let view (ctx: GameContext) (model: MapModel) (buffer: RenderBuffer2D) =
    let assets = GameContext.getService<IAssets> ctx
    let tex = assets.Texture Tiles.SheetPath
    let size = float32 Tiles.TileSize

    for y in 0 .. model.Grid.Height - 1 do
      for x in 0 .. model.Grid.Width - 1 do
        match CellGrid2D.get x y model.Grid with
        | ValueSome tile ->
          let pos = CellGrid2D.getWorldPos x y model.Grid
          let dest = Rectangle(pos.X, pos.Y, size, size)

          if tile.IsPath then
            let struct (frame, rotation) = pathFrame model.Grid x y

            buffer
              .sprite(
                SpriteState.create(tex, dest, frame.Rect)
                |> SpriteState.withOrigin(Vector2(size / 2f, size / 2f))
                |> SpriteState.withRotation rotation
                |> SpriteState.withLayer Layers.Path
              )
              .drop()
          else
            let frame = grassVariant x y

            buffer
              .sprite(
                SpriteState.create(tex, dest, frame.Rect)
                |> SpriteState.withLayer Layers.Ground
              )
              .drop()
        | ValueNone -> ()

    // Base marker — the mount pad reads as "the base" until entities land.
    let struct (bx, by) = model.BaseCell
    let basePos = CellGrid2D.getWorldPos bx by model.Grid

    buffer
      .sprite(
        SpriteState.create(
          tex,
          Rectangle(basePos.X, basePos.Y, size, size),
          Tiles.turretMountEmpty.Rect
        )
        |> SpriteState.withLayer Layers.Path
      )
      .drop()

    // Hover highlight + range ring live in World.view (they join
    // projections — see hoverOverlays).
