module Defli.Tests.SpawningTests

open Expecto
open TestData
open Defli.World
open Defli.World.Systems
open Defli.World.Systems.Spawning

/// A grunt-only wave — deterministic, no weighted-pick variance.
let private gruntWave = {
  Table = [| struct (Fixtures.grunt, 1) |]
  Count = 4
  Interval = 0.5f
  InitialDelay = 1.0f
  ExtraSpawns = Array.empty
}

/// A mixed wave — exercises the weighted pick.
let private mixedWave = {
  Table = [|
    struct (Fixtures.grunt, 1)
    struct (Fixtures.runner, 1)
    struct (Fixtures.tank, 1)
  |]
  Count = 30
  Interval = 0.25f
  InitialDelay = 0.5f
  ExtraSpawns = Array.empty
}

let tests =
  testList "Spawning" [
    testCase "FillWave builds the queue with spaced delays" (fun () ->
      let m = Spawning.init 42
      let struct (m', events) = Spawning.update (SpawnMsg.FillWave gruntWave) m
      Expect.equal events.Length 0 "no failures"
      Expect.equal m'.Queue.Count gruntWave.Count "queue count"

      let struct (def0, delay0) = m'.Queue[0]
      let struct (def1, delay1) = m'.Queue[1]
      Expect.equal def0 Fixtures.grunt "first pick"
      Expect.equal delay0 gruntWave.InitialDelay "initial delay"

      Expect.equal
        delay1
        (gruntWave.InitialDelay + gruntWave.Interval)
        "interval spacing")

    testCase "tick drains due spawns in order" (fun () ->
      let m = Spawning.init 42
      let struct (m', _) = Spawning.update (SpawnMsg.FillWave gruntWave) m

      // Before the initial delay: nothing.
      let struct (m2, events) = Spawning.tick 0.5f m'
      Expect.hasLength events 0 "nothing before delay"
      Expect.equal m2.Queue.Count gruntWave.Count "queue intact"

      // Past the initial delay: one spawn.
      let struct (m3, events) = Spawning.tick 0.6f m2

      match events |> Seq.tryHead with
      | Some(SpawnEnemy def) -> Expect.equal def Fixtures.grunt "first spawn"
      | _ -> failtest "expected one spawn"

      Expect.equal m3.Queue.Count (gruntWave.Count - 1) "one drained")

    testCase "drain is deterministic per seed" (fun () ->
      let run seed =
        let m = Spawning.init seed
        let struct (m', _) = Spawning.update (SpawnMsg.FillWave mixedWave) m
        let mutable spawns = []

        for _ in 1 .. mixedWave.Count do
          let struct (m2, events) = Spawning.tick 10.0f m'

          let spawns' =
            events
            |> Seq.choose (function
              | SpawnEnemy def -> Some def.Key
              | SpawnFailed _ -> None)

          spawns <- spawns @ List.ofSeq spawns'
          m'.Queue <- m2.Queue

        spawns

      Expect.equal (run 42) (run 42) "same seed, same spawns"

      // A different seed yields a different composition (mixed table).
      Expect.notEqual (run 42) (run 1337) "different seed, different spawns")

    testCase "empty table fails loudly" (fun () ->
      let m = Spawning.init 42

      let struct (m', events) =
        Spawning.update
          (SpawnMsg.FillWave {
            Table = [||]
            Count = 3
            Interval = 0.5f
            InitialDelay = 0f
            ExtraSpawns = Array.empty
          })
          m

      match events with
      | [| SpawnFailed _ |] -> ()
      | _ -> failtest "expected SpawnFailed"

      Expect.equal m'.Queue.Count 0 "empty queue")

    testCase "ExtraSpawns queue ahead of the weighted picks at fixed delays" (fun () ->
      let m = Spawning.init 42

      let struct (m', events) =
        Spawning.update
          (SpawnMsg.FillWave {
            gruntWave with
                ExtraSpawns = [| struct (Fixtures.tank, 0.25f) |]
          })
          m

      Expect.equal events.Length 0 "no failures"

      // The extra spawn is queued in ADDITION to the Count picks.
      Expect.equal m'.Queue.Count (gruntWave.Count + 1) "extra queued"

      let struct (def0, delay0) = m'.Queue[0]
      Expect.equal def0 Fixtures.tank "extra leads"
      Expect.equal delay0 0.25f "fixed delay")
  ]
