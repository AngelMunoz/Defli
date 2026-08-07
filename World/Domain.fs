namespace Defli.World

open Raylib_cs

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
type MapTile =
    { Terrain: TerrainKind
      IsPath: bool
      Buildable: bool }

/// One baked atlas tile (position + size), see Tiles.fs.
/// GENERATED data — the dataset is compile-time, no XML at runtime.
[<Struct>]
type TileInfo =
    { Name: string
      X: int
      Y: int
      Width: int
      Height: int }

    member this.Rect =
        Rectangle(float32 this.X, float32 this.Y, float32 this.Width, float32 this.Height)

// ─────────────────────────────────────────────────────────────
// World config (assembled outside the world — Kimo Phase 6 seam)
// ─────────────────────────────────────────────────────────────

type WorldConfig =
    { Seed: int
      StartingGold: int
      StartingLives: int
      GridCols: int
      GridRows: int }

module WorldConfig =

    let defaults =
        { Seed = 42
          StartingGold = 60
          StartingLives = 20
          GridCols = 20
          GridRows = 12 }
