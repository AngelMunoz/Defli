module Defli.Tests.MapTests

open Expecto
open Mibo.Layout
open Defli.World
open Defli.World.Systems

let private cfg = TestData.Fixtures.cfg
let private map = MapModel.create cfg

let tests =
  testList "Map" [
    testCase "grid dimensions match config" (fun () ->
      Expect.equal map.Grid.Width cfg.GridCols "cols"
      Expect.equal map.Grid.Height cfg.GridRows "rows")

    testCase "path is continuous spawn → base" (fun () ->
      Expect.equal map.SpawnCell (struct (0, 4)) "spawn cell"
      Expect.equal map.BaseCell (struct (19, 2)) "base cell"
      Expect.isGreaterThan map.Path.Length 1 "waypoints")

    testCase "path layer: every cell marked, none buildable" (fun () ->
      let pathGrid = MapModel.pathGrid map
      let mutable pathCount = 0

      CellGrid2D.iter
        (fun _ _ tile ->
          pathCount <- pathCount + 1
          Expect.isTrue tile.IsPath "path tile marked"
          Expect.isFalse tile.Buildable "path tile not buildable")
        pathGrid

      Expect.isGreaterThan pathCount 0 "path exists")

    testCase "buildable layer: no buildable cell sits on the road" (fun () ->
      let pathGrid = MapModel.pathGrid map
      let buildable = MapModel.buildableGrid map

      CellGrid2D.iter
        (fun x y tile ->
          if tile.Buildable then
            match CellGrid2D.get x y pathGrid with
            | ValueSome p -> Expect.isFalse p.IsPath "buildable not on path"
            | ValueNone -> ())
        buildable)

    testCase "isBuildable: grass yes, road no, out of grid no" (fun () ->
      Expect.isTrue (MapModel.isBuildable 0 0 map) "grass buildable"
      Expect.isFalse (MapModel.isBuildable 1 4 map) "road not buildable"
      Expect.isFalse (MapModel.isBuildable -1 0 map) "out of grid")

    testCase "waypoints layer marks the path vertices" (fun () ->
      let waypoints = MapModel.waypoints map

      let marked =
        CellGrid2D.get 0 4 waypoints
        |> ValueOption.exists(fun t -> t.IsWaypoint)

      Expect.isTrue marked "spawn vertex marked"

      let baseMarked =
        CellGrid2D.get 19 2 waypoints
        |> ValueOption.exists(fun t -> t.IsWaypoint)

      Expect.isTrue baseMarked "base vertex marked"

      let offPath =
        CellGrid2D.get 3 3 waypoints
        |> ValueOption.exists(fun t -> t.IsWaypoint)

      Expect.isFalse offPath "off-path cell not marked")

    testCase "waypoint centers sit at cell centers" (fun () ->
      for p in map.Path do
        // Origin (0,0), 64px cells: centers are at x.5 offsets.
        Expect.equal (p.X % 64f) 32f "center x"
        Expect.equal (p.Y % 64f) 32f "center y")

    testCase "spawn and base cells are on the path" (fun () ->
      let pathGrid = MapModel.pathGrid map
      let spawnTile = CellGrid2D.get 0 4 pathGrid
      let baseTile = CellGrid2D.get 19 2 pathGrid

      match spawnTile, baseTile with
      | ValueSome s, ValueSome b ->
        Expect.isTrue s.IsPath "spawn on path"
        Expect.isTrue b.IsPath "base on path"
      | _ -> failtest "spawn/base cells must exist")
  ]
