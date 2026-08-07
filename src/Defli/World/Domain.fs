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

[<Struct>]
type EnemyDef = {
  Key: string
  Archetype: EnemyArchetype
  Hp: int
  Speed: float32
  GoldReward: int
  /// Baked sprite name in the Tanks sheet (resolved at render time).
  Sprite: string
}

module EnemyDefs =

  let grunt = {
    Key = "grunt"
    Archetype = EnemyArchetype.Grunt
    Hp = 40
    Speed = 60f
    GoldReward = 5
    Sprite = "tankBody_green"
  }

  let runner = {
    Key = "runner"
    Archetype = EnemyArchetype.Runner
    Hp = 20
    Speed = 110f
    GoldReward = 7
    Sprite = "tankBody_blue"
  }

  let tank = {
    Key = "tank"
    Archetype = EnemyArchetype.Tank
    Hp = 120
    Speed = 35f
    GoldReward = 12
    Sprite = "tankBody_huge"
  }

  let all = [| grunt; runner; tank |]

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
