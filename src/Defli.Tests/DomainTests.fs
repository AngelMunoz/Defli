module Defli.Tests.DomainTests

open Expecto
open Defli.World

let tests =
  testList "Domain" [
    testCase "baked tower-defense dataset has 299 tiles" (fun () ->
      Expect.equal Tiles.all.Length 299 "all tile count"
      Expect.equal Tiles.byName.Count 299 "byName index count")

    testCase "baked tanks dataset has 187 tiles" (fun () ->
      Expect.equal Tanks.all.Length 187 "all tile count"
      Expect.equal Tanks.byName.Count 187 "byName index count")

    testCase "named accessors resolve to the atlas positions" (fun () ->
      Expect.equal Tiles.grassFullA.Name "grass_full_a" "grassFullA name"
      Expect.equal Tiles.pathVerticalDirt.Width 64 "path tile size"

      Expect.equal
        Tanks.tankBodyGreen.Name
        "tankBody_green"
        "tankBodyGreen name"

      Expect.equal Tanks.tankBodyHuge.Name "tankBody_huge" "tankBodyHuge name")

    testCase "tryByName misses return ValueNone" (fun () ->
      Expect.isTrue (Tiles.tryByName "does_not_exist").IsNone "unknown tile"
      Expect.isTrue (Tanks.tryByName "tankBody_blue").IsSome "known tile")

    testCase "fixture enemy defs are wired to baked sprites" (fun () ->
      for def in TestData.Fixtures.all do
        Expect.isGreaterThan def.Hp 0 $"{def.Key} hp"
        Expect.isGreaterThan def.Speed 0f $"{def.Key} speed"
        Expect.isGreaterThan def.GoldReward 0 $"{def.Key} reward"

        Expect.isTrue
          (Tanks.tryByName def.Sprite).IsSome
          $"{def.Key} sprite baked")
  ]
