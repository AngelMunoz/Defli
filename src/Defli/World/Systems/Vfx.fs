module Defli.World.Systems.Vfx

open System
open System.Collections.Generic
open System.Numerics
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Mibo.Elmish.Graphics2D.Lighting
open Raylib_cs
open Defli.World

// ─────────────────────────────────────────────────────────────
// VFX sub-system — the ONE deliberately non-adaptive system in the
// world (plan §4.10): per-particle adaptive cells would be node
// churn for fire-and-forget presentation. Pooled particles pattern
// (Mibo pooled-particles), one pool per effect kind — each kind
// draws with its own texture in a single .particles call.
//
// Kinds use the kenney_particle_pack assets (added for Phase 2):
//   Impact   → spark_01  (projectile hits)
//   DeathPoof→ smoke_01  (enemy killed)
//   Muzzle   → muzzle_01 (tower firing)
//   Placement→ dirt_01   (tower placement dust)
//   BaseHit  → smoke_01  (enemy reached the base)
//
// Deterministic bursts — no RNG stream (index-based angles/speeds).
//
// Texture handles are resolved ONCE and cached on the model: the
// per-frame `assets.Texture(string)` calls were flagged by the trace
// (string allocation per call — resolvePath). The cache is
// presentation state (asset handles, not adaptive reads).
// ─────────────────────────────────────────────────────────────

[<Struct>]
type VfxKind =
  | Impact
  | DeathPoof
  | Muzzle
  | Placement
  | BaseHit

/// One pooled particle store (SoA-ish: particles + parallel velocities).
[<Sealed>]
type VfxPool(capacity: int) =
  member val Particles = Array.zeroCreate<Particle2D> capacity with get, set
  member val Velocities = Array.zeroCreate<Vector2> capacity with get, set
  member val Count = 0 with get, set

type VfxModel() =
  member val Impact = VfxPool 256 with get, set
  member val DeathPoof = VfxPool 256 with get, set
  member val Muzzle = VfxPool 128 with get, set
  member val Placement = VfxPool 128 with get, set
  member val BaseHit = VfxPool 128 with get, set
  /// Resolved texture handle per kind (view-time, cached once).
  member val Textures = Dictionary<string, Texture2D>() with get, set

[<Struct>]
type VfxMsg = Burst of kind: VfxKind * pos: Vector2

module Vfx =

  let init() = VfxModel()

  /// Per-kind spawn parameters: count, base speed, size, fade speed.
  let inline private paramsOf(kind: VfxKind) =
    match kind with
    | Impact -> struct (8, 140f, 12f, 150f)
    | DeathPoof -> struct (6, 50f, 24f, 60f)
    | Muzzle -> struct (4, 80f, 16f, 220f)
    | Placement -> struct (6, 60f, 18f, 140f)
    | BaseHit -> struct (10, 40f, 30f, 50f)

  /// Cold path: spawn a burst into the kind's pool (deterministic
  /// spread — index-based angles, three speed tiers).
  let update (msg: VfxMsg) (model: VfxModel) : unit =
    match msg with
    | Burst(kind, pos) ->
      let struct (count, speed, size, _) = paramsOf kind

      let pool =
        match kind with
        | Impact -> model.Impact
        | DeathPoof -> model.DeathPoof
        | Muzzle -> model.Muzzle
        | Placement -> model.Placement
        | BaseHit -> model.BaseHit

      let mutable i = 0

      while i < count && pool.Count < pool.Particles.Length do
        let angle = float32 i / float32 count * 2f * MathF.PI
        let tier = float32(i % 3 + 1)
        let dir = Vector2(MathF.Cos angle, MathF.Sin angle)
        let velocity = dir * (speed * tier)

        pool.Particles[pool.Count] <-
          {
            Position = pos
            Size = Vector2(size, size)
            Rotation = angle * 180f / MathF.PI
            SourceRect = Rectangle(0f, 0f, 0f, 0f) // full texture — patched in the view
            Color = Color(255uy, 255uy, 255uy, 255uy)
          }

        pool.Velocities[pool.Count] <- velocity
        pool.Count <- pool.Count + 1
        i <- i + 1

  let inline private stepPool dt (pool: VfxPool) (fadeSpeed: float32) =
    for i in 0 .. pool.Count - 1 do
      let p = pool.Particles[i]

      pool.Particles[i] <-
        {
          p with
              Position = p.Position + pool.Velocities[i] * dt
        }

    let fadeAmount = fadeSpeed * dt
    let mutable write = 0

    for read in 0 .. pool.Count - 1 do
      let p = pool.Particles[read]
      let newAlpha = max 0uy (byte(float32 p.Color.A - fadeAmount))

      if newAlpha > 0uy then
        let c = Color(p.Color.R, p.Color.G, p.Color.B, newAlpha)
        pool.Particles[write] <- { p with Color = c }
        pool.Velocities[write] <- pool.Velocities[read]
        write <- write + 1

    pool.Count <- write

  /// Hot path: integrate velocities, fade, compact (in place, zero alloc).
  /// Velocities are compacted in parallel with the particles.
  let tick (dt: float32) (model: VfxModel) : unit =
    let struct (_, _, _, fadeImpact) = paramsOf VfxKind.Impact
    let struct (_, _, _, fadeDeath) = paramsOf VfxKind.DeathPoof
    let struct (_, _, _, fadeMuzzle) = paramsOf VfxKind.Muzzle
    let struct (_, _, _, fadePlacement) = paramsOf VfxKind.Placement
    let struct (_, _, _, fadeBaseHit) = paramsOf VfxKind.BaseHit
    stepPool dt model.Impact fadeImpact
    stepPool dt model.DeathPoof fadeDeath
    stepPool dt model.Muzzle fadeMuzzle
    stepPool dt model.Placement fadePlacement
    stepPool dt model.BaseHit fadeBaseHit

  // ── View (one .particles draw call per kind/texture) ──
  [<Literal>]
  let ImpactPath = "kenney_particle_pack/spark_01.png"

  [<Literal>]
  let DeathPoofPath = "kenney_particle_pack/smoke_01.png"

  [<Literal>]
  let MuzzlePath = "kenney_particle_pack/muzzle_01.png"

  [<Literal>]
  let PlacementPath = "kenney_particle_pack/dirt_01.png"

  [<Literal>]
  let BaseHitPath = "kenney_particle_pack/smoke_01.png"

  /// Texture per kind (kenney_particle_pack).
  let inline private textureOf(kind: VfxKind) =
    match kind with
    | Impact -> ImpactPath
    | DeathPoof -> DeathPoofPath
    | Muzzle -> MuzzlePath
    | Placement -> PlacementPath
    | BaseHit -> BaseHitPath

  /// Cached handle per kind: resolves through IAssets once, then
  /// reuses the stored Texture2D (no per-frame string work).
  let inline textureOfCached
    (kind: VfxKind)
    (model: VfxModel)
    (assets: IAssets)
    : Texture2D =
    let key = textureOf kind

    match model.Textures |> Dictionary.tryGetValue key with
    | ValueSome tex -> tex
    | ValueNone ->
      let tex = assets.Texture key
      model.Textures[key] <- tex
      tex

  let inline drawPool
    (kind: VfxKind)
    (pool: VfxPool)
    (model: VfxModel)
    (assets: IAssets)
    buffer
    =
    if pool.Count > 0 then
      let tex = textureOfCached kind model assets
      let full = Rectangle(0f, 0f, float32 tex.Width, float32 tex.Height)

      // Patch the full-texture source rect (the pool stores placeholders).
      for i in 0 .. pool.Count - 1 do
        pool.Particles[i] <-
          {
            pool.Particles[i] with
                SourceRect = full
          }

      buffer
        .particles(tex, pool.Particles, pool.Count, layer = Layers.Effects)
        .drop()

  let view (ctx: GameContext) (model: VfxModel) (buffer: RenderBuffer2D) =
    let assets = GameContext.getService<IAssets> ctx
    drawPool VfxKind.Impact model.Impact model assets buffer
    drawPool VfxKind.DeathPoof model.DeathPoof model assets buffer
    drawPool VfxKind.Muzzle model.Muzzle model assets buffer
    drawPool VfxKind.Placement model.Placement model assets buffer
    drawPool VfxKind.BaseHit model.BaseHit model assets buffer
