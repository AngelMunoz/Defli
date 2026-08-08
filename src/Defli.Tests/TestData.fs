module Defli.Tests.TestData

open System
open Mibo.Elmish
open Defli.World

// ─────────────────────────────────────────────────────────────
// Test-owned fixtures — never production data (Kimo convention:
// tests build their own `test_*` stores/configs with distinct
// values, so a mix-up fails loudly and production tuning is
// never frozen by a test).
// ─────────────────────────────────────────────────────────────

module Fixtures =

  /// Test world config — distinct from WorldConfig.defaults.
  let cfg = {
    Seed = 7
    StartingGold = 100
    StartingLives = 20
    WaveClearBonus = 10
    GridCols = 20
    GridRows = 12
  }

  /// Test enemy definitions — distinct values catch mix-ups.
  let grunt = {
    Key = "test_grunt"
    Archetype = EnemyArchetype.Grunt
    Hp = 30
    Speed = 40f
    GoldReward = 2
    Sprite = "tank_hull_green"
    Turret = ValueSome "tank_turret_green"
    TurretAngle = 0f
  }

  let runner = {
    Key = "test_runner"
    Archetype = EnemyArchetype.Runner
    Hp = 10
    Speed = 90f
    GoldReward = 3
    Sprite = "tank_hull_beige"
    Turret = ValueSome "turret_missiles_dual"
    TurretAngle = 90f
  }

  let tank = {
    Key = "test_tank"
    Archetype = EnemyArchetype.Tank
    Hp = 100
    Speed = 20f
    GoldReward = 5
    Sprite = "tank_hull_beige"
    Turret = ValueSome "tank_turret_beige"
    TurretAngle = 0f
  }

  /// Test flier — distinct values catch production mix-ups.
  let flier = {
    Key = "test_flier"
    Archetype = EnemyArchetype.Flier
    Hp = 15
    Speed = 60f
    GoldReward = 4
    Sprite = "plane_gray"
    Turret = ValueNone
    TurretAngle = 0f
  }

  let all = [| grunt; runner; tank; flier |]

// ─────────────────────────────────────────────────────────────
// Headless program plumbing
// ─────────────────────────────────────────────────────────────

/// A headless program over the world router: RoomTick each frame.
let mkProgram(cfg: WorldConfig) =
  HeadlessProgram.mkHeadless (fun _ctx -> World.init cfg, Cmd.none) World.update
  |> HeadlessProgram.withTick(fun gt -> WorldMsg.RoomTick gt)

let mkRunner(cfg: WorldConfig) =
  new HeadlessRunner<WorldModel, WorldMsg>(mkProgram cfg)

/// Coarse step for e2e timing tests (the sim is dt-agnostic — the
/// movement/spawn math consumes dt directly).
let dt = TimeSpan.FromSeconds 0.1

/// Fine step for frame-accurate tests.
let frameDt = TimeSpan.FromSeconds(1.0 / 60.0)
