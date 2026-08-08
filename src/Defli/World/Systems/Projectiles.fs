module Defli.World.Systems.Projectiles

open System
open System.Collections.Generic
open System.Numerics
open AdaptiveSlop.Core
open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Raylib_cs
open Defli.World

// ─────────────────────────────────────────────────────────────
// Projectiles sub-system — owns in-flight shots. One map is enough
// (projectiles have no cross-component reads). Its render position
// is the world-owned Homing projection (Projectiles.Rows ×
// Enemies.Positions — see Projections.fs).
//
// The homing feel: the projectile seeks the target's LIVE position
// row each tick (passed in as a direct transient read of
// Enemies.Positions — hot path, no closures).
// ─────────────────────────────────────────────────────────────

[<Struct>]
type ProjectileMsg = Spawn of spawn: ProjectileSpawn

[<Struct>]
type ProjectileEvent = Impact of impact: ProjectileImpact

type ProjectilesModel() =
  member val Rows = CMap.empty<int<ProjectileId>, ProjectileRow> with get, set
  member val NextId = 0<ProjectileId> with get, set

module Projectiles =

  let private lifetime = 2.5f
  let private hitThreshold = 6f

  let init() = ProjectilesModel()

  /// Cold path: spawn one shot (router-translated from TowerEvent.Fired).
  let update
    (msg: ProjectileMsg)
    (model: ProjectilesModel)
    : struct (ProjectilesModel * ProjectileEvent[]) =
    match msg with
    | Spawn spawn ->
      let pid = model.NextId
      model.NextId <- model.NextId + 1<ProjectileId>

      model.Rows
      |> CMap.addOrUpdate pid {
        Pos = spawn.Pos
        TargetEnemy = spawn.TargetEnemy
        Damage = spawn.Damage
        Speed = spawn.Speed
        Lifetime = lifetime
        SlowFactor = spawn.SlowFactor
        SlowSeconds = spawn.SlowSeconds
      }

      model, Array.empty

  /// Hot path: advance toward the target's live position; impact or
  /// expire. `positions` is a transient read of Enemies.Positions
  /// (direct value from the router). Writes are collected and applied
  /// after iteration (transient views die on the next write).
  let tick
    (dt: float32)
    (model: ProjectilesModel)
    (positions: IReadOnlyDictionary<int<EnemyId>, Vector2>)
    : struct (ProjectilesModel * ProjectileEvent seq) =
    let mutable events: ResizeArray<ProjectileEvent> = null

    let mutable updates: ResizeArray<struct (int<ProjectileId> * ProjectileRow)> =
      null

    let mutable removes: ResizeArray<int<ProjectileId>> = null

    for KeyValueV(pid, row) in model.Rows |> AMap.getValue do
      let lifetime = row.Lifetime - dt

      if lifetime <= 0f then
        if isNull removes then
          removes <- ResizeArray()

        removes.Add pid
      else
        match positions |> ReadOnlyDict.tryGetValue row.TargetEnemy with
        | ValueNone ->
          // Target despawned mid-flight — nothing left to hit.
          if isNull removes then
            removes <- ResizeArray()

          removes.Add pid
        | ValueSome targetPos ->
          let d = targetPos - row.Pos
          let dist = d.Length()
          let step = row.Speed * dt

          if dist <= step + hitThreshold then
            if isNull events then
              events <- ResizeArray()

            events.Add(
              Impact {
                Projectile = pid
                Enemy = row.TargetEnemy
                Damage = row.Damage
                Pos = row.Pos
                SlowFactor = row.SlowFactor
                SlowSeconds = row.SlowSeconds
              }
            )

            if isNull removes then
              removes <- ResizeArray()

            removes.Add pid
          else
            if isNull updates then
              updates <- ResizeArray()

            updates.Add
              struct (pid,
                      {
                        row with
                            Pos = row.Pos + (d / dist) * step
                            Lifetime = lifetime
                      })

    if not(isNull updates) then
      for struct (pid, row) in updates do
        model.Rows |> CMap.addOrUpdate pid row

    if not(isNull removes) then
      Transaction.run(fun () ->
        for pid in removes do
          model.Rows |> CMap.remove pid)

    model, (if isNull events then Array.empty else events)

  // ── View (rocket sprite from the Homing projection) ──

  let view
    (ctx: GameContext)
    (model: ProjectilesModel)
    (homing: amap<int<ProjectileId>, HomingView>)
    (buffer: RenderBuffer2D)
    =
    let assets = GameContext.getService<IAssets> ctx
    let tex = assets.Texture Tiles.SheetPath
    let tile = Tiles.rocketSmall

    let scale = 28f / max (float32 tile.Width) (float32 tile.Height)
    let w = float32 tile.Width * scale
    let h = float32 tile.Height * scale

    for KeyValueV(pid, v) in homing |> AMap.getValue do
      // Heading toward the live target position (0° = up; raylib rotates CW).
      let d = v.TargetPos - v.Pos
      let angle = 90f + MathF.Atan2(d.Y, d.X) * 180f / MathF.PI

      buffer
        .sprite(
          SpriteState.create(
            tex,
            Rectangle(v.Pos.X - w / 2f, v.Pos.Y - h / 2f, w, h),
            tile.Rect
          )
          |> SpriteState.withOrigin(Vector2(w / 2f, h / 2f))
          |> SpriteState.withRotation angle
          |> SpriteState.withLayer Layers.Projectiles
        )
        .drop()
