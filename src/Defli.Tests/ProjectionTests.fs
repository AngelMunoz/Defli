module Defli.Tests.ProjectionTests

open Expecto
open AdaptiveSlop.Core
open TestData
open Defli.World
open Defli.World.Systems
open Defli
open Defli.World.Systems.Enemies
open Defli.World.Systems.Economy

// ─────────────────────────────────────────────────────────────
// The ECS "query returns what the tables contain" contract: the
// reactive projections must agree with the component maps at
// every step. These are the AdaptiveSlop stress assertions.
// ─────────────────────────────────────────────────────────────

let private cfg = Fixtures.cfg
let private map = MapModel.create cfg

let private aliveView(m: EnemiesModel) =
  (m.Alive :> IAdaptiveMap<int<EnemyId>, EnemyView>).GetValue()

let private viewsView(m: EnemiesModel) =
  (m.Views :> IAdaptiveMap<int<EnemyId>, EnemyView>).GetValue()

let private spawn (m: EnemiesModel) (def: EnemyDef) =
  let struct (m', _) = Enemies.update (EnemyMsg.Spawn def) m map.Path
  m'

let tests =
  testList "Projections" [
    testCase "Views joins all three component maps" (fun () ->
      let m = spawn (Enemies.init()) Fixtures.grunt
      let views = viewsView m
      Expect.equal views.Count 1 "one row"

      for KeyValueV(eid, v) in views do
        Expect.equal v.Pos map.Path[0] "pos from Positions"
        Expect.equal v.Hp Fixtures.grunt.Hp "hp from Healths"
        Expect.equal v.MaxHp Fixtures.grunt.Hp "maxHp from Healths"
        Expect.equal v.Progress 0f "progress from Motions"
        Expect.equal v.Slow 1f "slow from Motions")

    testCase "damage delta lands on exactly the damaged enemy" (fun () ->
      let mutable m = Enemies.init()
      m <- spawn m Fixtures.grunt // id 0
      m <- spawn m Fixtures.tank // id 1
      m <- spawn m Fixtures.runner // id 2

      // Damage the tank (id 1).
      let struct (m', _) =
        Enemies.update (EnemyMsg.ApplyDamage(1 * 1<EnemyId>, 50)) m map.Path

      let expectedHp(eid: int<EnemyId>) =
        if eid = 1 * 1<EnemyId> then Fixtures.tank.Hp - 50
        elif eid = 0 * 1<EnemyId> then Fixtures.grunt.Hp
        else Fixtures.runner.Hp

      let views = viewsView m'

      for KeyValueV(eid, v) in views do
        Expect.equal v.Hp (expectedHp eid) $"hp of enemy %d{int eid}"

      // Alive still holds all three (nothing dead).
      Expect.equal (aliveView m').Count 3 "all alive")

    testCase "Alive drops corpses; Views keeps them until despawn" (fun () ->
      let mutable m = spawn (Enemies.init()) Fixtures.grunt
      m <- spawn m Fixtures.runner

      let struct (m', _) =
        Enemies.update (EnemyMsg.ApplyDamage(0 * 1<EnemyId>, 999)) m map.Path

      Expect.equal (aliveView m').Count 1 "only runner alive"
      Expect.equal (viewsView m').Count 2 "corpse still joined"
      Expect.equal (AVal.getValue m'.AliveCount) 1 "count follows Alive"

      let struct (m2, _) =
        Enemies.update (EnemyMsg.Despawn(0 * 1<EnemyId>)) m' map.Path

      Expect.equal (viewsView m2).Count 1 "corpse removed"
      Expect.equal (AVal.getValue m2.AliveCount) 1 "count unchanged")

    testCase "repeated reads at a settled state are stable" (fun () ->
      let mutable m = spawn (Enemies.init()) Fixtures.tank

      let struct (m', _) =
        Enemies.update (EnemyMsg.ApplyDamage(0 * 1<EnemyId>, 10)) m map.Path

      let first = viewsView m'
      let second = viewsView m'
      Expect.equal first.Count second.Count "stable count"

      for KeyValueV(eid, v) in first do
        match second |> ReadOnlyDict.tryGetValue eid with
        | ValueSome v2 -> Expect.equal v v2 "stable row"
        | ValueNone -> failtest "row vanished")

    testCase "game over aval follows lives" (fun () ->
      let e = Economy.init cfg
      Expect.isFalse (AVal.getValue e.GameOver) "not over"
      Economy.update EconomyMsg.LoseLife e
      Expect.isFalse (AVal.getValue e.GameOver) "still not over"

      for _ in 2 .. cfg.StartingLives do
        Economy.update EconomyMsg.LoseLife e

      Expect.isTrue (AVal.getValue e.GameOver) "over at zero")
  ]
