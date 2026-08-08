module Defli.World.Systems.Vfx

open System
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
//
// Deterministic bursts — no RNG stream (index-based angles/speeds).
// ─────────────────────────────────────────────────────────────

[<Struct>]
type VfxKind =
  | Impact
  | DeathPoof
  | Muzzle

/// One pooled particle store (SoA-ish: particles + parallel velocities).
[<Sealed>]
type VfxPool(capacity: int) =
  member val Particles = Array.zeroCreate<Particle2D> capacity with get, set
  member val Velocities = Array.zeroCreate<Vector2> capacity with get, set
  member val Count = 0 with get, set

type VfxModel() =
  member val Impact = VfxPool(256) with get, set
  member val DeathPoof = VfxPool(256) with get, set
  member val Muzzle = VfxPool(128) with get, set

[<Struct>]
type VfxMsg = | Burst of kind: VfxKind * pos: Vector2

module Vfx =

  let init() = VfxModel()

  /// Per-kind spawn parameters: count, base speed, size, fade speed.
  let private paramsOf(kind: VfxKind) =
    match kind with
    | Impact -> struct (8, 140f, 12f, 150f)
    | DeathPoof -> struct (6, 50f, 24f, 60f)
    | Muzzle -> struct (4, 80f, 16f, 220f)

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

      let mutable i = 0

      while i < count && pool.Count < pool.Particles.Length do
        let angle = (float32 i / float32 count) * 2f * MathF.PI
        let tier = float32 (i % 3 + 1)
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

  /// Hot path: integrate velocities, fade, compact (in place, zero alloc).
  /// Velocities are compacted in parallel with the particles.
  let tick (dt: float32) (model: VfxModel) : unit =
    let stepPool (pool: VfxPool) (fadeSpeed: float32) =
      for i in 0 .. pool.Count - 1 do
        let p = pool.Particles[i]
        pool.Particles[i] <- { p with Position = p.Position + pool.Velocities[i] * dt }

      let fadeAmount = fadeSpeed * dt
      let mutable write = 0

      for read in 0 .. pool.Count - 1 do
        let p = pool.Particles[read]
        let newAlpha = max 0uy (byte (float32 p.Color.A - fadeAmount))

        if newAlpha > 0uy then
          let c = Color(p.Color.R, p.Color.G, p.Color.B, newAlpha)
          pool.Particles[write] <- { p with Color = c }
          pool.Velocities[write] <- pool.Velocities[read]
          write <- write + 1

      pool.Count <- write

    let struct (_, _, _, fadeImpact) = paramsOf VfxKind.Impact
    let struct (_, _, _, fadeDeath) = paramsOf VfxKind.DeathPoof
    let struct (_, _, _, fadeMuzzle) = paramsOf VfxKind.Muzzle
    stepPool model.Impact fadeImpact
    stepPool model.DeathPoof fadeDeath
    stepPool model.Muzzle fadeMuzzle

  // ── View (one .particles draw call per kind/texture) ──

  /// Texture per kind (kenney_particle_pack).
  let private textureOf(kind: VfxKind) =
    match kind with
    | Impact -> "kenney_particle_pack/spark_01.png"
    | DeathPoof -> "kenney_particle_pack/smoke_01.png"
    | Muzzle -> "kenney_particle_pack/muzzle_01.png"

  let view
    (ctx: GameContext)
    (model: VfxModel)
    (buffer: RenderBuffer2D)
    =
    let assets = GameContext.getService<IAssets> ctx

    let drawPool (kind: VfxKind) (pool: VfxPool) =
      if pool.Count > 0 then
        let tex = assets.Texture(textureOf kind)
        let full = Rectangle(0f, 0f, float32 tex.Width, float32 tex.Height)

        // Patch the full-texture source rect (the pool stores placeholders).
        for i in 0 .. pool.Count - 1 do
          pool.Particles[i] <- { pool.Particles[i] with SourceRect = full }

        buffer
          .particles(tex, pool.Particles, pool.Count, layer = Layers.Effects)
          .drop()

    drawPool VfxKind.Impact model.Impact
    drawPool VfxKind.DeathPoof model.DeathPoof
    drawPool VfxKind.Muzzle model.Muzzle
