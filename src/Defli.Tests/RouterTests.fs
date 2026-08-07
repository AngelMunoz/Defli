module Defli.Tests.RouterTests

open Expecto
open AdaptiveSlop.Core
open Defli.World
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
  ]
