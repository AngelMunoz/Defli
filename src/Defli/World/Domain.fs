namespace Defli.World

open Raylib_cs
open System.Numerics

// ─────────────────────────────────────────────────────────────
// Typed IDs (units of measure — zero-cost, struct-friendly)
// ─────────────────────────────────────────────────────────────

[<Measure>]
type EnemyId

[<Measure>]
type TowerId

[<Measure>]
type ProjectileId

// ─────────────────────────────────────────────────────────────
// Render layers (deferred RenderBuffer2D sorts by layer)
// ─────────────────────────────────────────────────────────────

module Layers =
  let Ground = 0<Mibo.Elmish.Graphics2D.RenderLayer>
  let Path = 1<Mibo.Elmish.Graphics2D.RenderLayer>
  let Entities = 2<Mibo.Elmish.Graphics2D.RenderLayer>
  let Projectiles = 3<Mibo.Elmish.Graphics2D.RenderLayer>
  let Effects = 4<Mibo.Elmish.Graphics2D.RenderLayer>
  let Hud = 10<Mibo.Elmish.Graphics2D.RenderLayer>

// ─────────────────────────────────────────────────────────────
// Map
// ─────────────────────────────────────────────────────────────

[<Struct>]
type TerrainKind =
  | Grass
  | Dirt
  | Stone
  | Sand

[<Struct>]
type MapTile = {
  Terrain: TerrainKind
  IsPath: bool
  Buildable: bool
  /// True on the path's waypoint cells (spawn/base — Waypoints layer).
  IsWaypoint: bool
}

/// One baked atlas tile (position + size), see Tiles.fs.
/// GENERATED data — the dataset is compile-time, no XML at runtime.
[<Struct>]
type TileInfo = {
  Name: string
  X: int
  Y: int
  Width: int
  Height: int
} with

  member this.Rect =
    Rectangle(
      float32 this.X,
      float32 this.Y,
      float32 this.Width,
      float32 this.Height
    )

// ─────────────────────────────────────────────────────────────
// World config (assembled outside the world — Kimo Phase 6 seam)
// ─────────────────────────────────────────────────────────────

type WorldConfig = {
  Seed: int
  StartingGold: int
  StartingLives: int
  WaveClearBonus: int
  GridCols: int
  GridRows: int
}

module WorldConfig =

  let defaults = {
    Seed = 42
    StartingGold = 60
    StartingLives = 20
    WaveClearBonus = 25
    GridCols = 20
    GridRows = 12
  }

// ─────────────────────────────────────────────────────────────
// Enemy definitions & components (code-authored def store)
// ─────────────────────────────────────────────────────────────

[<Struct>]
type EnemyArchetype =
  | Grunt
  | Runner
  | Tank
  | Flier

[<Struct>]
type EnemyDef = {
  Key: string
  Archetype: EnemyArchetype
  Hp: int
  Speed: float32
  GoldReward: int
  /// Baked sprite name in the Tiles sheet (tower-defense pack).
  Sprite: string
  /// Baked turret name in the Tiles sheet, drawn centered over the
  /// body and aimed at the heading (ValueNone = no turret).
  Turret: string voption
  /// Built-in orientation correction of the turret sprite, degrees
  /// clockwise (0 = the sprite's barrel points up, like the bodies).
  TurretAngle: float32
}

module EnemyDefs =

  let grunt = {
    Key = "grunt"
    Archetype = EnemyArchetype.Grunt
    Hp = 40
    Speed = 60f
    GoldReward = 3
    Sprite = "tank_hull_green"
    Turret = ValueSome "tank_turret_green"
    TurretAngle = 0f
  }

  let runner = {
    Key = "runner"
    Archetype = EnemyArchetype.Runner
    Hp = 20
    Speed = 110f
    GoldReward = 5
    Sprite = "tank_hull_green"
    Turret = ValueNone
    TurretAngle = 0f
  }

  let tank = {
    Key = "tank"
    Archetype = EnemyArchetype.Tank
    Hp = 120
    Speed = 35f
    GoldReward = 7
    Sprite = "tank_hull_beige"
    Turret = ValueSome "tank_turret_beige"
    TurretAngle = 0f
  }

  /// Flies the straight line spawn → base (ignores the road).
  /// A plane — no turret.
  let flier = {
    Key = "flier"
    Archetype = EnemyArchetype.Flier
    Hp = 30
    Speed = 130f
    GoldReward = 10
    Sprite = "plane_gray"
    Turret = ValueNone
    TurretAngle = 0f
  }

  let all = [| grunt; runner; tank; flier |]

/// Per-enemy components (rows in the Enemies sub-system's CMaps).
[<Struct>]
type Health = { Hp: int; MaxHp: int }

[<Struct>]
type Motion = {
  Speed: float32
  Slow: float32
  Progress: float32
  PathIndex: int
}

/// One wave's executable content — composed by Waves (director),
/// executed by Spawning (queue + weighted picks).
[<Struct>]
type WaveDef = {
  Table: struct (EnemyDef * int)[]
  Count: int
  Interval: float32
  InitialDelay: float32
}

/// Join row of the EnemyViews projection (Positions × Healths × Motions).
[<Struct>]
type EnemyView = {
  Pos: Vector2
  Hp: int
  MaxHp: int
  Progress: float32
  Slow: float32
  PathIndex: int
}

// ─────────────────────────────────────────────────────────────
// Tower definitions & components
// ─────────────────────────────────────────────────────────────

/// Which enemy a tower picks first among its in-range candidates.
[<Struct>]
type TargetPolicy =
  /// Closest to the base (highest progress).
  | First
  /// Furthest from the base (lowest progress).
  | Last
  /// Highest max HP.
  | Strongest
  /// Lowest current HP.
  | Weakest
  /// Nearest to the tower.
  | Closest

[<Struct>]
type TowerDef = {
  Key: string
  Name: string
  Cost: int
  /// Range in grid cells (Chebyshev ring narrowed by exact distance).
  Range: int
  Damage: int
  /// Shots per second.
  FireRate: float32
  ProjectileSpeed: float32
  /// Head sprite name in the Tiles sheet (drawn over turretBaseA).
  Sprite: string
  TargetPolicy: TargetPolicy
}

module TowerDefs =

  let arrow = {
    Key = "arrow"
    Name = "Arrow"
    Cost = 50
    Range = 3
    Damage = 10
    FireRate = 2.25f
    ProjectileSpeed = 240f
    Sprite = "rocket_pod_single"
    TargetPolicy = TargetPolicy.First
  }

  let all = [| arrow |]

/// Per-tower components (rows in the Towers sub-system's CMaps).
/// Static vs runtime is the write-frequency grouping: Statics is written
/// once (placement), Runtimes every tick (cooldown/target).
[<Struct>]
type TowerStatic = {
  Def: TowerDef
  Cell: struct (int * int)
}

[<Struct>]
type TowerRuntime = {
  Cooldown: float32
  Target: int<EnemyId> voption
}

// ─────────────────────────────────────────────────────────────
// Projectiles
// ─────────────────────────────────────────────────────────────

/// One in-flight shot (a row in Projectiles.Rows).
[<Struct>]
type ProjectileRow = {
  Pos: Vector2
  TargetEnemy: int<EnemyId>
  Damage: int
  Speed: float32
  Lifetime: float32
}

/// Render row of the world-owned Homing projection
/// (Projectiles.Rows × Enemies.Positions).
[<Struct>]
type HomingView = { Pos: Vector2; TargetPos: Vector2 }

// ─────────────────────────────────────────────────────────────
// Placement preview (hover highlight state)
// ─────────────────────────────────────────────────────────────

[<Struct>]
type PlacementStatus =
  | Hidden
  | Blocked
  | Affordable
  | TooExpensive

// ─────────────────────────────────────────────────────────────
// Cell helpers
// ─────────────────────────────────────────────────────────────

module Cells =

  /// World-space center of a grid cell (grid origin is Zero,
  /// cell size is uniform — see MapModel.create).
  let center (cell: struct (int * int)) (cellSize: Vector2) =
    let struct (x, y) = cell

    Vector2(
      float32 x * cellSize.X + cellSize.X / 2f,
      float32 y * cellSize.Y + cellSize.Y / 2f
    )
