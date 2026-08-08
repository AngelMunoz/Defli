module Defli.World.Systems.Towers

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
// Towers sub-system — owns placement, targeting, firing.
//
//   Statics   — { Def, Cell } written once at placement
//   Runtimes  — { Cooldown, Target } written every tick
//   CellIndex — cell → tower id (placement occupancy + the
//               RangeRing projection's hover lookup)
//
// Targeting reads the Enemies.Alive TRANSIENT VIEW passed in as a
// direct value by the router (hot path, no closures). Phase 3 adds
// the TargetPolicy field; Phase 2 always picks "first" (the enemy
// closest to the base — highest progress).
// ─────────────────────────────────────────────────────────────

[<Struct>]
type TowerMsg = Place of struct (struct (int * int) * TowerDef)

[<Struct>]
type TowerEvent = Fired of shot: TowerShot

type TowersModel() =
  member val Statics = CMap.empty<int<TowerId>, TowerStatic> with get, set
  member val Runtimes = CMap.empty<int<TowerId>, TowerRuntime> with get, set

  member val CellIndex =
    CMap.empty<struct (int * int), int<TowerId>> with get, set

  /// Tagged from the start — ids never pass through a plain int.
  member val NextId = 0<TowerId> with get, set

module Towers =

  let init() = TowersModel()

  /// Cold path: place a tower. The ROUTER validates (buildable tile,
  /// occupancy, gold) before sending — this only writes the rows.
  let update
    (msg: TowerMsg)
    (model: TowersModel)
    : struct (TowersModel * TowerEvent[]) =
    match msg with
    | Place(cell, def) ->
      let tid = model.NextId
      model.NextId <- model.NextId + 1<TowerId>

      Transaction.run(fun () ->
        model.Statics |> CMap.addOrUpdate tid { Def = def; Cell = cell }

        model.Runtimes
        |> CMap.addOrUpdate tid { Cooldown = 0f; Target = ValueNone }

        model.CellIndex |> CMap.addOrUpdate cell tid)

      model, Array.empty

  /// Hot path: cooldown decay + target acquisition + fire.
  /// `alive` is a transient read of Enemies.Alive (direct value from
  /// the router); `cellSize` is the grid's uniform cell size.
  let tick
    (dt: float32)
    (model: TowersModel)
    (alive: amap<int<EnemyId>, EnemyView>)
    (cellSize: Vector2)
    : struct (TowersModel * TowerEvent seq) =
    let mutable events: ResizeArray<TowerEvent> = null

    for KeyValueV(tid, s) in model.Statics |> AMap.getValue do
      let center = Cells.center s.Cell cellSize
      let rangeWorld = float32 s.Def.Range * cellSize.X
      let runtimes = model.Runtimes |> AMap.tryFind tid
      let targetA = runtimes |> AVal.map(ValueOption.bind _.Target)

      let cooldownA =
        runtimes
        |> AVal.map(ValueOption.map _.Cooldown >> ValueOption.defaultValue 0f)

      let cooldown = cooldownA |> AVal.getValue

      let cooldown' = max 0f (cooldown - dt)

      if cooldown' <= 0f then
        // Acquire a target: in range + exact distance, then the def's
        // policy decides among the candidates (Phase 3).
        let mutable best: struct (int<EnemyId> * EnemyView * float32) voption =
          ValueNone

        for KeyValueV(eid, v) in alive |> AMap.getValue do
          let d = Vector2.Distance(center, v.Pos)

          if d <= rangeWorld then
            let better =
              match best with
              | ValueNone -> true
              | ValueSome struct (_, bv, bd) ->
                match s.Def.TargetPolicy with
                | TargetPolicy.First -> v.Progress > bv.Progress
                | TargetPolicy.Last -> v.Progress < bv.Progress
                | TargetPolicy.Strongest -> v.MaxHp > bv.MaxHp
                | TargetPolicy.Weakest -> v.Hp < bv.Hp
                | TargetPolicy.Closest -> d < bd

            if better then
              best <- ValueSome struct (eid, v, d)

        match best with
        | ValueSome struct (eid, _, _) ->
          if isNull events then
            events <- ResizeArray()

          events.Add(
            Fired {
              Tower = tid
              Enemy = eid
              Damage = s.Def.Damage
              SlowFactor = s.Def.SlowFactor
              SlowSeconds = s.Def.SlowSeconds
            }
          )

          model.Runtimes
          |> CMap.addOrUpdate tid {
            Cooldown = 1f / max 0.1f s.Def.FireRate
            Target = ValueSome eid
          }
        | ValueNone ->
          model.Runtimes
          |> CMap.addOrUpdate tid { Cooldown = 0f; Target = ValueNone }
      else
        let target = targetA |> AVal.getValue

        model.Runtimes
        |> CMap.addOrUpdate tid {
          Cooldown = cooldown'
          Target = target
        }

    model, (if isNull events then Array.empty else events)

  // ── View (base + head sprites from the Tiles sheet) ──

  let view
    (ctx: GameContext)
    (model: TowersModel)
    (cellSize: Vector2)
    (buffer: RenderBuffer2D)
    =
    let assets = GameContext.getService<IAssets> ctx
    let tex = assets.Texture Tiles.SheetPath
    let size = cellSize

    for KeyValueV(tid, s) in model.Statics |> AMap.getValue do
      let center = Cells.center s.Cell cellSize

      let cellRect =
        Rectangle(
          center.X - size.X / 2f,
          center.Y - size.Y / 2f,
          size.X,
          size.Y
        )

      // Base plate.
      buffer
        .sprite(
          SpriteState.create(tex, cellRect, Tiles.turretBaseA.Rect)
          |> SpriteState.withLayer Layers.Entities
        )
        .drop()

      // Head (the def's sprite — rocket pod).
      s.Def.Sprite
      |> Tiles.tryByName
      |> ValueOption.iter(fun tile ->
        buffer
          .sprite(
            SpriteState.create(tex, cellRect, tile.Rect)
            |> SpriteState.withLayer Layers.Entities
          )
          .drop())
