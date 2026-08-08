module Defli.Tests.WavesTests

open Expecto
open AdaptiveSlop.Core
open Defli.World
open Defli.World.Systems
open Defli.World.Systems.Waves

let tests =
  testList "Waves" [
    testCase "composeWave scales with wave number" (fun () ->
      let w1 = Waves.composeWave 1
      let w5 = Waves.composeWave 5
      Expect.isGreaterThan w5.Count w1.Count "count grows"
      Expect.isLessThan w5.Interval w1.Interval "interval shrinks"
      Expect.isGreaterThanOrEqual w5.Interval 0.3f "interval floors"
      Expect.isGreaterThan w1.Count 0 "non-empty")

    testCase "fliers enter the tables from wave 4" (fun () ->
      let w4 = Waves.composeWave 4

      Expect.contains
        (w4.Table |> Array.map(fun struct (def, _) -> def.Key))
        EnemyDefs.flier.Key
        "wave 4 has fliers"

      let w5 = Waves.composeWave 5

      Expect.contains
        (w5.Table |> Array.map(fun struct (def, _) -> def.Key))
        EnemyDefs.flier.Key
        "boss wave has fliers"

      let w2 = Waves.composeWave 2

      Expect.isFalse
        (w2.Table |> Array.exists(fun struct (def, _) -> def.Key = EnemyDefs.flier.Key))
        "early waves have no fliers")

    testCase "composition is deterministic (no RNG in the director)" (fun () ->
      let tableOf n =
        Waves.composeWave n
        |> fun w -> w.Table |> Array.map(fun struct (def, _) -> def.Key)

      Expect.equal (tableOf 4) (tableOf 4) "wave 4 table stable"
      Expect.equal (tableOf 5) (tableOf 5) "wave 5 table stable"
      Expect.equal (tableOf 12) (tableOf 12) "wave 12 table stable")

    testCase "StartNextWave composes + activates, then refuses" (fun () ->
      let m = Waves.init()
      let struct (m', events) = Waves.update WaveMsg.StartNextWave m

      match events with
      | [| WaveStarted wave |] ->
        Expect.equal wave.Count (Waves.composeWave 1).Count "wave 1"
      | _ -> failtest "expected WaveStarted"

      Expect.isTrue m'.WaveActive.Value "active"
      Expect.equal m'.WaveNumber.Value 1 "wave number"

      let struct (m2, events) = Waves.update WaveMsg.StartNextWave m'
      Expect.equal events.Length 0 "refuses while active"
      Expect.equal m2.WaveNumber.Value 1 "wave number unchanged")

    testCase "clear detection via direct values" (fun () ->
      let m = Waves.init()
      let struct (m', _) = Waves.update WaveMsg.StartNextWave m

      // Still spawning: no clear.
      let struct (m2, events) = Waves.tick 0.1f m' 3 false
      Expect.equal (events |> Seq.length) 0 "not cleared with enemies alive"

      // Queue empty and no enemies: cleared.
      let struct (m3, events) = Waves.tick 0.1f m2 0 true

      match events |> Seq.tryHead with
      | Some WaveCleared -> ()
      | _ -> failtest "expected WaveCleared"

      Expect.isFalse m3.WaveActive.Value "inactive after clear")

    testCase "banner projection follows state" (fun () ->
      let m = Waves.init()

      Expect.stringContains
        (AVal.getValue m.Banner)
        "Press Enter"
        "idle banner"

      let struct (m', _) = Waves.update WaveMsg.StartNextWave m
      Expect.stringContains (AVal.getValue m'.Banner) "Wave 1" "active banner")
  ]
