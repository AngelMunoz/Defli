module Defli.Tests.TowersTests

open System.Collections.Generic
open System.Numerics
open Expecto
open AdaptiveSlop.Core
open Defli.World
open Defli.World.Systems
open TestData
open Defli.World.Systems.Towers

let private cfg = TestData.Fixtures.cfg
let private map = MapModel.create cfg
let private cellSize = Vector2(float32 Tiles.TileSize, float32 Tiles.TileSize)
let private model() = Towers.init()

/// Test-owned tower def — distinct values catch production mix-ups.
let private def = {
  Key = "test_arrow"
  Name = "Test Arrow"
  Cost = 30
  Range = 2
  Damage = 5
  FireRate = 4f
  ProjectileSpeed = 200f
  Sprite = "rocket_pod_single"
}

/// A single enemy standing at a position (transient Alive-shaped dict).
let private enemyAt (pos: Vector2) (progress: float32) =
  let d = Dictionary<int<EnemyId>, EnemyView>()

  d[0 * 1<EnemyId>] <- {
    Pos = pos
    Hp = 100
    MaxHp = 100
    Progress = progress
    Slow = 1f
    PathIndex = 1
  }

  d

let private cellCenter(struct (x, y)) =
  Vector2(
    float32 x * cellSize.X + cellSize.X / 2f,
    float32 y * cellSize.Y + cellSize.Y / 2f
  )

let tests =
  testList "Towers" [
    testCase "place writes Statics + Runtimes + CellIndex atomically" (fun () ->
      let m = model()
      let cell = struct (3, 3)

      let struct (m', _) = Towers.update (TowerMsg.Place(cell, def)) m

      Expect.equal (m'.Statics |> AMap.getValue).Count 1 "statics"
      Expect.equal (m'.Runtimes |> AMap.getValue).Count 1 "runtimes"

      match m'.CellIndex |> CMap.tryGetValue cell with
      | ValueSome tid -> Expect.equal tid (0 * 1<TowerId>) "indexed"
      | ValueNone -> failtest "cell must be indexed"

      match m'.Statics |> CMap.tryGetValue(0 * 1<TowerId>) with
      | ValueSome s -> Expect.equal s.Def def "def stored"
      | ValueNone -> failtest "tower must exist")

    testCase "no target in range → no fire, cooldown stays ready" (fun () ->
      let m = model()
      let cell = struct (3, 3)

      let struct (m', _) = Towers.update (TowerMsg.Place(cell, def)) m

      // Range 2 cells ≈ 128 px; enemy is far away.
      let alive = AMap.constant(fun () -> enemyAt (Vector2(900f, 900f)) 0.5f)
      let struct (m2, events) = Towers.tick 0.1f m' alive cellSize
      Expect.isEmpty events "no fire"
      Expect.equal (Seq.length events) 0 "no events"

      match m2.Runtimes |> CMap.tryGetValue(0 * 1<TowerId>) with
      | ValueSome r -> Expect.equal r.Cooldown 0f "ready"
      | ValueNone -> failtest "runtime must exist")

    testCase "enemy in range → Fired with damage; cooldown set" (fun () ->
      let m = model()
      let cell = struct (3, 3)

      let struct (m', _) = Towers.update (TowerMsg.Place(cell, def)) m

      // Tower center (3,3) = (224, 224); enemy one cell east = in range 2.
      let alive =
        AMap.constant(fun () -> enemyAt (cellCenter struct (4, 3)) 0.5f)

      let struct (m2, events) = Towers.tick 0.1f m' alive cellSize

      match events |> Seq.toArray with
      | [| Fired(tid, eid, damage) |] ->
        Expect.equal tid (0 * 1<TowerId>) "tower id"
        Expect.equal eid (0 * 1<EnemyId>) "enemy id"
        Expect.equal damage def.Damage "damage"
      | _ -> failtest "expected exactly one Fired"

      match m2.Runtimes |> CMap.tryGetValue(0 * 1<TowerId>) with
      | ValueSome r ->
        Expect.equal r.Cooldown (1f / def.FireRate) "cooldown set"
        Expect.equal r.Target (ValueSome(0 * 1<EnemyId>)) "target stored"
      | ValueNone -> failtest "runtime must exist")

    testCase "first policy: picks the enemy closest to the base" (fun () ->
      let m = model()
      let cell = struct (3, 3)

      let struct (m', _) = Towers.update (TowerMsg.Place(cell, def)) m

      // Two enemies both in range; the one with higher progress wins.
      let alive = Dictionary<int<EnemyId>, EnemyView>()

      alive[1 * 1<EnemyId>] <- {
        Pos = cellCenter struct (4, 3)
        Hp = 100
        MaxHp = 100
        Progress = 0.2f
        Slow = 1f
        PathIndex = 1
      }

      alive[2 * 1<EnemyId>] <- {
        Pos = cellCenter struct (4, 2)
        Hp = 100
        MaxHp = 100
        Progress = 0.8f
        Slow = 1f
        PathIndex = 1
      }

      let alive = AMap.constant(fun () -> alive)

      let struct (_, events) = Towers.tick 0.1f m' alive cellSize

      match events |> Seq.toArray with
      | [| Fired(_, eid, _) |] ->
        Expect.equal eid (2 * 1<EnemyId>) "first = highest progress"
      | _ -> failtest "expected exactly one Fired")

    testCase "cooldown gates firing" (fun () ->
      let m = model()
      let cell = struct (3, 3)

      let struct (m', _) = Towers.update (TowerMsg.Place(cell, def)) m

      let alive =
        AMap.constant(fun () -> enemyAt (cellCenter struct (4, 3)) 0.5f)

      // Fire (cooldown = 0.25 at FireRate 4).
      let struct (m2, events) = Towers.tick 0.1f m' alive cellSize
      Expect.equal (Seq.length events) 1 "fired once"

      // 0.1 s later: still cooling down (0.25 - 0.1 = 0.15).
      let struct (m3, events2) = Towers.tick 0.1f m2 alive cellSize
      Expect.isEmpty events2 "not ready yet"

      // 0.2 s more: 0.15 - 0.2 ≤ 0 → fires again.
      let struct (_, events3) = Towers.tick 0.2f m3 alive cellSize
      Expect.equal (Seq.length events3) 1 "fired again")
  ]
