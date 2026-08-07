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

    testCase "path cells are marked and non-buildable" (fun () ->
      let mutable pathCount = 0
      let mutable violations = 0

      for y in 0 .. map.Grid.Height - 1 do
        for x in 0 .. map.Grid.Width - 1 do
          match CellGrid2D.get x y map.Grid with
          | ValueSome tile ->
            if tile.IsPath then
              pathCount <- pathCount + 1

              if tile.Buildable then
                violations <- violations + 1
            elif not tile.Buildable then
              violations <- violations + 1
          | ValueNone -> ()

      Expect.isGreaterThan pathCount 0 "path exists"
      Expect.equal violations 0 "no buildable path / unbuildable grass")

    testCase "waypoint centers sit at cell centers" (fun () ->
      for p in map.Path do
        // Origin (0,0), 64px cells: centers are at x.5 offsets.
        Expect.equal (p.X % 64f) 32f "center x"
        Expect.equal (p.Y % 64f) 32f "center y")

    testCase "spawn and base cells are on the path" (fun () ->
      let spawnTile = CellGrid2D.get 0 4 map.Grid
      let baseTile = CellGrid2D.get 19 2 map.Grid

      match spawnTile, baseTile with
      | ValueSome s, ValueSome b ->
        Expect.isTrue s.IsPath "spawn on path"
        Expect.isTrue b.IsPath "base on path"
      | _ -> failtest "spawn/base cells must exist")
  ]
