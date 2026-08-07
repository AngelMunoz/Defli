module Defli.Tests.EnemiesTests

open System.Numerics
open Expecto
open AdaptiveSlop.Core
open Defli.World
open Defli.World.Systems
open TestData
open Defli.World.Systems.Enemies

let private cfg = TestData.Fixtures.cfg
let private map = MapModel.create cfg
let private model() = Enemies.init()

let private aliveCount(m: EnemiesModel) =
  (m.Alive :> IAdaptiveMap<int<EnemyId>, EnemyView>).GetValue().Count

let private viewsCount(m: EnemiesModel) =
  (m.Views :> IAdaptiveMap<int<EnemyId>, EnemyView>).GetValue().Count

let private hpOf (m: EnemiesModel) (eid: int<EnemyId>) =
  m.Healths |> CMap.tryGetValue eid

let tests =
  testList "Enemies" [
    testCase "spawn adds rows to all maps + projections" (fun () ->
      let m = model()

      let struct (m', _) =
        Enemies.update (EnemyMsg.Spawn Fixtures.grunt) m map.Path

      Expect.equal ((m'.Healths |> AMap.getValue).Count) 1 "healths"
      Expect.equal ((m'.Motions |> AMap.getValue).Count) 1 "motions"
      Expect.equal ((m'.Positions |> AMap.getValue).Count) 1 "positions"
      Expect.equal ((m'.Defs |> AMap.getValue).Count) 1 "defs"
      Expect.equal (aliveCount m') 1 "alive"
      Expect.equal (viewsCount m') 1 "views"

      Expect.equal
        ((m'.Positions |> AMap.getValue)[0 * 1<EnemyId>])
        map.Path[0]
        "starts at spawn")

    testCase "spawn is atomic across maps" (fun () ->
      let m = model()

      let struct (m', _) =
        Enemies.update (EnemyMsg.Spawn Fixtures.tank) m map.Path

      // All four rows share the same key.
      let eid = 0 * 1<EnemyId>
      Expect.isTrue ((m'.Healths |> CMap.tryGetValue eid).IsSome) "health"
      Expect.isTrue ((m'.Motions |> CMap.tryGetValue eid).IsSome) "motion"
      Expect.isTrue ((m'.Positions |> CMap.tryGetValue eid).IsSome) "position"
      Expect.isTrue ((m'.Defs |> CMap.tryGetValue eid).IsSome) "def")

    testCase "damage reduces HP; death emits Killed with reward" (fun () ->
      let m = model()

      let struct (m', _) =
        Enemies.update (EnemyMsg.Spawn Fixtures.grunt) m map.Path

      let eid = 0 * 1<EnemyId>

      let struct (m2, events) =
        Enemies.update (EnemyMsg.ApplyDamage(eid, 10)) m' map.Path

      Expect.equal events.Length 0 "not dead yet"

      match hpOf m2 eid with
      | ValueSome h ->
        Expect.equal h.Hp (Fixtures.grunt.Hp - 10) "hp after 10 damage"
      | ValueNone -> failtest "enemy must exist"

      let struct (m3, events) =
        Enemies.update (EnemyMsg.ApplyDamage(eid, 100)) m2 map.Path

      match events with
      | [| Killed(dead, reward) |] ->
        Expect.equal dead eid "killed id"
        Expect.equal reward Fixtures.grunt.GoldReward "reward"
      | _ -> failtest "expected exactly one Killed"

      // Alive excludes the corpse; Views still joins it (Hp = 0).
      Expect.equal (aliveCount m3) 0 "alive excludes dead"
      Expect.equal (viewsCount m3) 1 "views keeps corpse until despawn")

    testCase "despawn removes rows everywhere" (fun () ->
      let m = model()

      let struct (m', _) =
        Enemies.update (EnemyMsg.Spawn Fixtures.runner) m map.Path

      let struct (m2, _) =
        Enemies.update (EnemyMsg.Despawn(0 * 1<EnemyId>)) m' map.Path

      Expect.equal ((m2.Healths |> AMap.getValue).Count) 0 "healths"
      Expect.equal ((m2.Motions |> AMap.getValue).Count) 0 "motions"
      Expect.equal ((m2.Positions |> AMap.getValue).Count) 0 "positions"
      Expect.equal ((m2.Defs |> AMap.getValue).Count) 0 "defs"
      Expect.equal (aliveCount m2) 0 "alive"
      Expect.equal (viewsCount m2) 0 "views")

    testCase "movement advances along waypoints" (fun () ->
      let m = model()

      let struct (m', _) =
        Enemies.update (EnemyMsg.Spawn Fixtures.runner) m map.Path

      let eid = 0 * 1<EnemyId>

      // Runner: 90 px/s; 1 second moves ~90px (waypoint 0→1 is 448px).
      let struct (m2, _) = Enemies.tick 1.0f m' map.Path

      match m2.Positions |> CMap.tryGetValue eid with
      | ValueSome pos ->
        Expect.equal pos.X (map.Path[0].X + 90f) "moved along segment"
      | ValueNone -> failtest "enemy must exist")

    testCase "arrival at base emits ReachedBase and removes rows" (fun () ->
      let m = model()

      let struct (m', _) =
        Enemies.update (EnemyMsg.Spawn Fixtures.runner) m map.Path

      let eid = 0 * 1<EnemyId>

      // Path is 29 cells = 1856 px; runner at 90 px/s needs ~21s.
      let mutable m2 = m'
      let mutable events: EnemyEvent[] = Array.empty

      for _ in 1..260 do
        let struct (m3, ev) = Enemies.tick 0.1f m2 map.Path
        m2 <- m3

        if ev.Length > 0 then
          events <- ev

      match events with
      | [| ReachedBase eid |] ->
        Expect.equal
          ((m2.Healths |> AMap.getValue).Count)
          0
          "removed on arrival"

        Expect.equal (aliveCount m2) 0 "alive empty"
      | _ -> failtest "expected ReachedBase")

    testCase "slow modifies speed and expires" (fun () ->
      let m = model()

      let struct (m', _) =
        Enemies.update (EnemyMsg.Spawn Fixtures.grunt) m map.Path

      let eid = 0 * 1<EnemyId>

      let struct (m2, _) =
        Enemies.update (EnemyMsg.ApplySlow(eid, 0.5f, 1.0f)) m' map.Path

      match m2.Motions |> CMap.tryGetValue eid with
      | ValueSome mv -> Expect.equal mv.Slow 0.5f "slowed"
      | ValueNone -> failtest "enemy must exist"

      let struct (m3, _) = Enemies.tick 0.5f m2 map.Path
      let struct (m4, _) = Enemies.tick 0.5f m3 map.Path

      match m4.Motions |> CMap.tryGetValue eid with
      | ValueSome mv -> Expect.equal mv.Slow 1f "slow expired"
      | ValueNone -> failtest "enemy must exist")
  ]
