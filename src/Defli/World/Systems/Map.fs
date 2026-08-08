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
// Map sub-system — owns a LayeredGrid2D<MapTile> (one parallel
// CellGrid2D per concern) and the path. Static content (built once
// at world init, never mutated — same rule as Kimo's map/stores;
// NOT adaptive).
//
// Layers (MapLayers):
//   Terrain    — grass fill (visual base)
//   Path       — the road, stamped over the waypoint segments
//   Buildable  — build permission; the road stamp overwrites the
//                cells under it with the non-buildable path tile
//   Waypoints  — the path's vertex cells (spawn/base markers)
//
// The road is carved with the stamp machinery (Layout.fill /
// repeatX / repeatY over GridSection2D), never hand-rolled loops.
// The view iterates with CellGrid2D.iterVisible over the camera's
// world-space view rect — culled to the visible cells even though
// the fixed screen currently shows the whole grid.
// ─────────────────────────────────────────────────────────────

/// Layer indices of the map's parallel grids.
[<RequireQualifiedAccess>]
module MapLayers =
  [<Literal>]
  let Terrain = 0

  [<Literal>]
  let Path = 1

  [<Literal>]
  let Buildable = 2

  [<Literal>]
  let Waypoints = 3

type MapModel = {
  Grid: LayeredGrid2D<MapTile>
  /// World-space waypoint centers (spawn → base) — the movement
  /// (physics) phase walks these.
  Path: Vector2[]
  SpawnCell: struct (int * int)
  BaseCell: struct (int * int)
}

module MapModel =

  let private grassTile = {
    Terrain = TerrainKind.Grass
    IsPath = false
    Buildable = true
    IsWaypoint = false
  }

  let private pathTile = {
    Terrain = TerrainKind.Dirt
    IsPath = true
    Buildable = false
    IsWaypoint = false
  }

  /// A layer's CellGrid2D (all layers exist after create).
  let inline layer (index: int) (m: MapModel) : CellGrid2D<MapTile> =
    let struct (grid, _) = LayeredGrid2D.getOrAddLayer index m.Grid
    grid

  let inline terrain(m: MapModel) = layer MapLayers.Terrain m
  let inline pathGrid(m: MapModel) = layer MapLayers.Path m
  let inline buildableGrid(m: MapModel) = layer MapLayers.Buildable m
  let inline waypoints(m: MapModel) = layer MapLayers.Waypoints m

  /// A cell is buildable iff its Buildable layer row carries Buildable
  /// (the road stamp overwrote the cells under it).
  let inline isBuildable (x: int) (y: int) (m: MapModel) : bool =
    m |> buildableGrid |> CellGrid2D.get x y |> ValueOption.exists _.Buildable

  /// Hand-authored Level-1 path, in cells (spawn left → base right).
  let private waypointCells = [|
    struct (0, 4)
    struct (7, 4)
    struct (7, 8)
    struct (14, 8)
    struct (14, 2)
    struct (19, 2)
  |]

  /// One axis-aligned road segment as a stamp (repeatX for horizontal,
  /// repeatY for vertical — inclusive of both endpoints).
  let inline private stampSegment
    (struct (px, py): struct (int * int))
    (struct (tx, ty): struct (int * int))
    (section: GridSection2D<MapTile>)
    : GridSection2D<MapTile> =
    if py = ty then
      Layout.repeatX (min px tx) py (abs(tx - px) + 1) pathTile section
    else
      Layout.repeatY px (min py ty) (abs(ty - py) + 1) pathTile section

  /// The whole road as one stamp chain (all waypoint segments).
  let inline private stampPath
    (section: GridSection2D<MapTile>)
    : GridSection2D<MapTile> =
    let mutable acc = section

    for i in 1 .. waypointCells.Length - 1 do
      acc <- stampSegment waypointCells[i - 1] waypointCells[i] acc

    acc

  let create(cfg: WorldConfig) : MapModel =
    let cellSize = Vector2(float32 Tiles.TileSize, float32 Tiles.TileSize)

    let grid =
      LayeredGrid2D.create cfg.GridCols cfg.GridRows cellSize Vector2.Zero

    // Terrain + buildable start as a full grass fill…
    grid
    |> LayeredLayout.layer MapLayers.Terrain (fun s ->
      Layout.fill 0 0 cfg.GridCols cfg.GridRows grassTile s)
    |> LayeredLayout.layer MapLayers.Buildable (fun s ->
      Layout.fill 0 0 cfg.GridCols cfg.GridRows grassTile s)
    // …then the road is stamped over both: the Path layer gets the
    // road; the Buildable layer's cells under the road are overwritten
    // with the non-buildable path tile (parallel-grid semantics).
    |> LayeredLayout.layer MapLayers.Path stampPath
    |> LayeredLayout.layer MapLayers.Buildable stampPath
    |> ignore

    // Waypoints layer: the path's vertex cells (spawn/base markers).
    let waypointTile = { grassTile with IsWaypoint = true }

    LayeredLayout.layer
      MapLayers.Waypoints
      (fun s ->
        waypointCells
        |> Array.fold
          (fun acc struct (x, y) -> Layout.set x y waypointTile acc)
          s)
      grid
    |> ignore

    // World-space waypoint centers (from the terrain layer's grid).
    let struct (terrainLayer, _) =
      LayeredGrid2D.getOrAddLayer MapLayers.Terrain grid

    let path =
      waypointCells
      |> Array.map(fun struct (x, y) ->
        let topLeft = CellGrid2D.getWorldPos x y terrainLayer
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
      grid |> CellGrid2D.get x y |> ValueOption.exists(fun t -> t.IsPath)

    let n = isPath x (y - 1)
    let s = isPath x (y + 1)
    let e = isPath (x + 1) y
    let w = isPath (x - 1) y

    let count =
      (if n then 1 else 0)
      + (if s then 1 else 0)
      + (if e then 1 else 0)
      + if w then 1 else 0

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
  let inline private grassVariant (x: int) (y: int) =
    Tiles.groundGrass[(x * 7 + y * 13) % 3]

  /// `visible` is the camera's world-space view rect (camera bounds
  /// from the Camera sub-system — iterVisible culls to it).
  let view
    (ctx: GameContext)
    (model: MapModel)
    (visible: Rectangle)
    (buffer: RenderBuffer2D)
    =
    let assets = GameContext.getService<IAssets> ctx
    let tex = assets.Texture Tiles.SheetPath
    let size = float32 Tiles.TileSize

    let terrain = MapModel.terrain model
    let pathGrid = MapModel.pathGrid model
    let waypoints = MapModel.waypoints model

    let left = int visible.X
    let top = int visible.Y
    let right = left + int visible.Width
    let bottom = top + int visible.Height

    // Terrain (grass) — only the visible cells.
    CellGrid2D.iterVisible
      left
      top
      right
      bottom
      (fun x y _ ->
        let pos = CellGrid2D.getWorldPos x y terrain
        let frame = grassVariant x y

        buffer
          .sprite(
            SpriteState.create(
              tex,
              Rectangle(pos.X, pos.Y, size, size),
              frame.Rect
            )
            |> SpriteState.withLayer Layers.Ground
          )
          .drop())
      terrain

    // Road — the carved cells with path-aware frames.
    CellGrid2D.iterVisible
      left
      top
      right
      bottom
      (fun x y _ ->
        let struct (frame, rotation) = pathFrame pathGrid x y
        let pos = CellGrid2D.getWorldPos x y pathGrid

        buffer
          .sprite(
            SpriteState.create(
              tex,
              Rectangle(pos.X, pos.Y, size, size),
              frame.Rect
            )
            |> SpriteState.withOrigin(Vector2(size / 2f, size / 2f))
            |> SpriteState.withRotation rotation
            |> SpriteState.withLayer Layers.Path
          )
          .drop())
      pathGrid

    // Base mount pad — from the waypoints layer (the base vertex).
    CellGrid2D.iterVisible
      left
      top
      right
      bottom
      (fun x y tile ->
        if tile.IsWaypoint && struct (x, y) = model.BaseCell then
          let pos = CellGrid2D.getWorldPos x y waypoints

          buffer
            .sprite(
              SpriteState.create(
                tex,
                Rectangle(pos.X, pos.Y, size, size),
                Tiles.turretMountEmpty.Rect
              )
              |> SpriteState.withLayer Layers.Path
            )
            .drop())
      waypoints
