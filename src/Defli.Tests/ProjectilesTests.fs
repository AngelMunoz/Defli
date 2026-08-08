module Defli.Tests.ProjectilesTests

open System.Collections.Generic
open System.Numerics
open Expecto
open AdaptiveSlop.Core
open Defli
open Defli.World
open Defli.World.Systems
open TestData
open Defli.World.Systems.Projectiles
open Defli.World.Systems.Enemies

let private cfg = TestData.Fixtures.cfg
let private map = MapModel.create cfg
let private model() = Projectiles.init()

let private target = 0<EnemyId>

/// A transient Positions-shaped dict with one enemy at pos.
let private positionsAt(pos: Vector2) =
  let d = Dictionary<int<EnemyId>, Vector2>()
  d[target] <- pos
  d

let private spawnAt (m: ProjectilesModel) (pos: Vector2) =
  let struct (m', _) =
    Projectiles.update
      (ProjectileMsg.Spawn {
        Pos = pos
        TargetEnemy = target
        Damage = 5
        Speed = 100f
        SlowFactor = 1f
        SlowSeconds = 0f
      })
      m

  m'

let tests =
  testList "Projectiles" [
    testCase "spawn adds a row" (fun () ->
      let struct (m', _) =
        Projectiles.update
          (ProjectileMsg.Spawn {
            Pos = Vector2.Zero
            TargetEnemy = target
            Damage = 5
            Speed = 100f
            SlowFactor = 1f
            SlowSeconds = 0f
          })
          (model())

      Expect.equal ((m'.Rows |> AMap.getValue).Count) 1 "one row"

      match m'.Rows |> CMap.tryGetValue(0<ProjectileId>) with
      | ValueSome row ->
        Expect.equal row.TargetEnemy target "target"
        Expect.equal row.Damage 5 "damage"
      | ValueNone -> failtest "row must exist")

    testCase "homing: seeks the target's live position and impacts" (fun () ->
      let mutable m = model()
      m <- spawnAt m (Vector2(0f, 0f))

      // Enemy stands 50 px away; speed 100 px/s.
      let struct (m2, _) =
        Projectiles.tick 0.1f m (positionsAt(Vector2(50f, 0f)))

      match m2.Rows |> CMap.tryGetValue(0<ProjectileId>) with
      | ValueSome row -> Expect.equal row.Pos.X 10f "moved toward target"
      | ValueNone -> failtest "row must exist"

      // Enough time to cover the remaining 40 px.
      let struct (m3, events) =
        Projectiles.tick 1.0f m2 (positionsAt(Vector2(50f, 0f)))

      match events |> Seq.toArray with
      | [| Impact impact |] ->
        Expect.equal impact.Projectile (0<ProjectileId>) "projectile id"
        Expect.equal impact.Enemy target "enemy id"
        Expect.equal impact.Damage 5 "damage"
      | _ -> failtest "expected exactly one Impact"

      Expect.equal ((m3.Rows |> AMap.getValue).Count) 0 "removed on impact")

    testCase "spawn carries the slow payload to Impact" (fun () ->
      // Frost-style shot: slowFactor 0.5 for 2 s.
      let mutable m = model()

      let struct (m', _) =
        Projectiles.update
          (ProjectileMsg.Spawn {
            Pos = Vector2(10f, 0f)
            TargetEnemy = target
            Damage = 4
            Speed = 200f
            SlowFactor = 0.5f
            SlowSeconds = 2f
          })
          m

      m <- m'

      let struct (m2, _) =
        Projectiles.tick 0.01f m (positionsAt(Vector2(50f, 0f)))

      // Enough time to cover the remaining 40 px.
      let struct (m3, events) =
        Projectiles.tick 1.0f m2 (positionsAt(Vector2(50f, 0f)))

      match events |> Seq.toArray with
      | [| Impact impact |] ->
        Expect.equal impact.Damage 4 "damage"
        Expect.equal impact.SlowFactor 0.5f "slow factor"
        Expect.equal impact.SlowSeconds 2f "slow seconds"
      | _ -> failtest "expected exactly one Impact"

      Expect.equal ((m3.Rows |> AMap.getValue).Count) 0 "removed on impact")

    testCase "target despawned mid-flight → row removed, no impact" (fun () ->
      let mutable m = model()
      m <- spawnAt m (Vector2(0f, 0f))

      let struct (m2, events) =
        Projectiles.tick 0.1f m (Dictionary<int<EnemyId>, Vector2>())

      Expect.isEmpty events "no impact"
      Expect.equal ((m2.Rows |> AMap.getValue).Count) 0 "removed")

    testCase "lifetime expiry removes the row" (fun () ->
      let mutable m = model()
      m <- spawnAt m (Vector2(0f, 0f))

      // Enemy is far; lifetime is 2.5 s → expires without impacting.
      let positions = positionsAt(Vector2(9999f, 9999f))

      for _ in 1..30 do
        let struct (m', events) = Projectiles.tick 0.1f m positions
        m <- m'

        if not(Seq.isEmpty events) then
          failtest "must not impact at that distance"

      Expect.equal ((m.Rows |> AMap.getValue).Count) 0 "expired")

    testCase
      "Homing projection: render row tracks the target's live position"
      (fun () ->
        // World-owned projection — build the pieces it joins.
        let enemies = Enemies.Enemies.init()

        let struct (enemies', _) =
          Enemies.Enemies.update
            (EnemyMsg.Spawn Fixtures.runner)
            enemies
            map.Path

        let eid = 0<EnemyId>
        let projectiles = model()
        let projectiles' = spawnAt projectiles (Vector2(0f, 0f))
        let towers = Towers.Towers.init()
        let economy = Economy.Economy.init cfg
        let hover = CVal.create ValueNone

        let projections =
          Projections(
            enemies',
            towers,
            projectiles',
            economy,
            MapModel.buildableGrid map,
            hover,
            CVal.create TowerDefs.arrow
          )

        let rows = projections.Homing |> AMap.getValue
        Expect.equal rows.Count 1 "one homing row"

        for KeyValueV(pid, v) in rows do
          Expect.equal v.Pos Vector2.Zero "projectile pos"
          Expect.equal v.TargetPos map.Path[0] "target pos from Positions"

        // Move the enemy; the homing row follows.
        let struct (enemies2, _) = Enemies.Enemies.tick 1.0f enemies' map.Path

        let rows2 = projections.Homing |> AMap.getValue

        for KeyValueV(pid, v) in rows2 do
          Expect.equal
            v.TargetPos
            (map.Path[0].X + 90f |> fun x -> Vector2(x, map.Path[0].Y))
            "tracked movement"

        // Kill the enemy (despawn): the homing entry DROPS (chooseA).
        let struct (enemies3, _) =
          Enemies.Enemies.update (EnemyMsg.Despawn eid) enemies2 map.Path

        let rows3 = projections.Homing |> AMap.getValue
        Expect.equal rows3.Count 0 "entry dropped with the target")
  ]
