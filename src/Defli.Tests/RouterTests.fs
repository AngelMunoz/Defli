module Defli.Tests.RouterTests

open Expecto
open AdaptiveSlop.Core
open Defli.World
open Defli.World.Systems
open Defli.World.Systems.Waves
open Defli.World.Systems.Economy

// ─────────────────────────────────────────────────────────────
// End-to-end through the HeadlessRunner: the MVU loop drives the
// router, which drives the sub-systems. Assertions read the model
// (component maps + projections) after virtual-time stepping.
// ─────────────────────────────────────────────────────────────

let private cfg = TestData.Fixtures.cfg

let private goldOf(m: WorldModel) = AVal.getValue m.Economy.Gold
let private livesOf(m: WorldModel) = AVal.getValue m.Economy.Lives
let private aliveOf(m: WorldModel) = AVal.getValue m.Enemies.AliveCount

let tests =
  testList "Router (e2e)" [
    testCase "wave 1 runs to completion: spawn → walk → leak → clear" (fun () ->
      let runner = TestData.mkRunner cfg
      let livesStart = livesOf runner.Model

      runner.Dispatch(WorldMsg.StartNextWave)
      // StepUntil: wave starts, spawns drain, enemies walk, all leak,
      // the wave clears.
      let cleared =
        runner.StepUntil(
          (fun m ->
            not(AVal.getValue m.Waves.WaveActive)
            && AVal.getValue m.Waves.WaveNumber >= 1),
          TestData.dt,
          4000
        )

      Expect.isTrue cleared "wave cleared within budget"

      let model = runner.Model
      Expect.equal (AVal.getValue model.Waves.WaveNumber) 1 "wave number"
      Expect.equal (aliveOf model) 0 "no enemies alive"
      Expect.equal model.Spawning.Queue.Count 0 "queue drained"

      // No towers in Phase 1: every wave-1 enemy leaks.
      let wave1 = Waves.composeWave 1

      Expect.equal
        (livesOf model)
        (livesStart - wave1.Count)
        "lives lost to leaks"

      // Gold: starting + wave-clear bonus (no kills yet).
      Expect.equal
        (goldOf model)
        (cfg.StartingGold + cfg.WaveClearBonus)
        "gold after clear")

    testCase "game over blocks new waves" (fun () ->
      let runner = TestData.mkRunner cfg

      // Drain all lives through the router (messages process on Step).
      for _ in 1 .. cfg.StartingLives do
        runner.Dispatch(WorldMsg.EconomyMsg EconomyMsg.LoseLife)

      runner.StepN(2, TestData.dt)

      Expect.isTrue (AVal.getValue runner.Model.Economy.GameOver) "game over"

      runner.Dispatch(WorldMsg.StartNextWave)
      runner.StepN(20, TestData.dt)

      let model = runner.Model
      Expect.equal (AVal.getValue model.Waves.WaveNumber) 0 "no wave started"
      Expect.isFalse (AVal.getValue model.Waves.WaveActive) "not active")

    testCase "deterministic run: same seed, same outcome" (fun () ->
      let run() =
        let runner = TestData.mkRunner cfg
        runner.Dispatch(WorldMsg.StartNextWave)

        runner.StepUntil(
          (fun m ->
            not(AVal.getValue m.Waves.WaveActive)
            && AVal.getValue m.Waves.WaveNumber >= 1),
          TestData.dt,
          4000
        )
        |> ignore

        // Fingerprint: gold, lives, wave number.
        let model = runner.Model

        struct (goldOf model,
                livesOf model,
                AVal.getValue model.Waves.WaveNumber)

      Expect.equal (run()) (run()) "same seed, same fingerprint")

    // ── Phase 2: towers & projectiles through the router ──

    testCase "PlaceTower on buildable cell spends gold and places" (fun () ->
      let runner = TestData.mkRunner cfg
      let cell = struct (1, 1) // grass, not path, not occupied

      runner.Dispatch(WorldMsg.PlaceTower cell)
      runner.StepN(2, TestData.dt)

      let model = runner.Model
      Expect.equal (goldOf model) (cfg.StartingGold - TowerDefs.arrow.Cost) "gold spent"

      match model.Towers.CellIndex |> CMap.tryGetValue cell with
      | ValueSome _ -> ()
      | ValueNone -> failtest "tower must be placed"

      Expect.equal ((model.Towers.Statics |> AMap.getValue).Count) 1 "one tower")

    testCase "PlaceTower on path cell is rejected" (fun () ->
      let runner = TestData.mkRunner cfg
      let cell = struct (1, 4) // the road (spawn row)

      runner.Dispatch(WorldMsg.PlaceTower cell)
      runner.StepN(2, TestData.dt)

      let model = runner.Model
      Expect.equal (goldOf model) cfg.StartingGold "no gold spent"
      Expect.equal ((model.Towers.Statics |> AMap.getValue).Count) 0 "no tower")

    testCase "PlaceTower on an occupied cell is rejected" (fun () ->
      let runner = TestData.mkRunner cfg
      let cell = struct (1, 1)

      runner.Dispatch(WorldMsg.PlaceTower cell)
      runner.StepN(2, TestData.dt)

      runner.Dispatch(WorldMsg.PlaceTower cell)
      runner.StepN(2, TestData.dt)

      let model = runner.Model
      Expect.equal ((model.Towers.Statics |> AMap.getValue).Count) 1 "still one tower"

      Expect.equal
        (goldOf model)
        (cfg.StartingGold - TowerDefs.arrow.Cost)
        "gold spent once")

    testCase "PlaceTower without enough gold is rejected" (fun () ->
      let runner = TestData.mkRunner cfg

      // Drain gold below the cost.
      runner.Dispatch(WorldMsg.EconomyMsg(EconomyMsg.SpendGold(cfg.StartingGold - 1)))
      runner.StepN(2, TestData.dt)

      runner.Dispatch(WorldMsg.PlaceTower(struct (1, 1)))
      runner.StepN(2, TestData.dt)

      let model = runner.Model
      Expect.equal ((model.Towers.Statics |> AMap.getValue).Count) 0 "no tower"
      Expect.equal (goldOf model) 1 "gold untouched")

    testCase "tower fires → projectile homes → impact damages the enemy" (fun () ->
      let runner = TestData.mkRunner cfg

      // Place a tower next to the path (the road runs along row 4).
      runner.Dispatch(WorldMsg.PlaceTower(struct (2, 3)))
      runner.StepN(2, TestData.dt)

      // Spawn one grunt on the path in range of the tower.
      runner.Dispatch(WorldMsg.EnemyMsg(Enemies.EnemyMsg.Spawn TestData.Fixtures.grunt))
      runner.StepN(2, TestData.dt)

      // Step until the tower fires (cooldown 0.5 s) and the projectile
      // reaches the enemy (240 px/s, enemy ~64 px away).
      let fired =
        runner.StepUntil(
          (fun m -> (m.Projectiles.Rows |> AMap.getValue).Count > 0),
          TestData.dt,
          120
        )

      Expect.isTrue fired "tower fired within budget"

      let impacted =
        runner.StepUntil(
          (fun m ->
            (m.Projectiles.Rows |> AMap.getValue).Count = 0
            && AVal.getValue m.Enemies.AliveCount = 0),
          TestData.dt,
          120
        )

      Expect.isTrue impacted "enemy died to tower fire within budget"

      let model = runner.Model

      // Grunt died (despawned by the router): gold includes the reward.
      Expect.equal
        (goldOf model)
        (cfg.StartingGold - TowerDefs.arrow.Cost + TestData.Fixtures.grunt.GoldReward)
        "kill rewarded")
  ]
