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
type TowerMsg = | Place of cell: struct (int * int) * def: TowerDef

[<Struct>]
type TowerEvent =
  | Fired of tower: int<TowerId> * enemy: int<EnemyId> * damage: int

type TowersModel() =
  member val Statics = CMap.empty<int<TowerId>, TowerStatic> with get, set
  member val Runtimes = CMap.empty<int<TowerId>, TowerRuntime> with get, set
  member val CellIndex = CMap.empty<struct (int * int), int<TowerId>> with get, set
  member val NextId = 0 with get, set

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
      let tid = model.NextId * 1<TowerId>
      model.NextId <- model.NextId + 1

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
    (alive: IReadOnlyDictionary<int<EnemyId>, EnemyView>)
    (cellSize: Vector2)
    : struct (TowersModel * TowerEvent seq) =
    let mutable events: ResizeArray<TowerEvent> = null

    for KeyValueV(tid, s) in model.Statics |> AMap.getValue do
      let center = Cells.center s.Cell cellSize
      let rangeWorld = float32 s.Def.Range * cellSize.X

      let runtime = model.Runtimes |> CMap.tryGetValue tid

      let cooldown =
        match runtime with
        | ValueSome r -> r.Cooldown
        | ValueNone -> 0f

      let cooldown' = max 0f (cooldown - dt)

      if cooldown' <= 0f then
        // Acquire a target: in range + exact distance, policy "first"
        // (highest progress = closest to the base).
        let mutable best: struct (int<EnemyId> * EnemyView) voption = ValueNone

        for KeyValueV(eid, v) in alive do
          if Vector2.Distance(center, v.Pos) <= rangeWorld then
            match best with
            | ValueSome(struct (_, bv)) when bv.Progress >= v.Progress -> ()
            | _ -> best <- ValueSome struct (eid, v)

        match best with
        | ValueSome struct (eid, _) ->
          if isNull events then
            events <- ResizeArray()

          events.Add(Fired(tid, eid, s.Def.Damage))

          model.Runtimes
          |> CMap.addOrUpdate tid {
            Cooldown = 1f / max 0.1f s.Def.FireRate
            Target = ValueSome eid
          }
        | ValueNone ->
          model.Runtimes
          |> CMap.addOrUpdate tid { Cooldown = 0f; Target = ValueNone }
      else
        let target =
          match runtime with
          | ValueSome r -> r.Target
          | ValueNone -> ValueNone

        model.Runtimes
        |> CMap.addOrUpdate tid { Cooldown = cooldown'; Target = target }

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
      match Tiles.tryByName s.Def.Sprite with
      | ValueSome tile ->
        buffer
          .sprite(
            SpriteState.create(tex, cellRect, tile.Rect)
            |> SpriteState.withLayer Layers.Entities
          )
          .drop()
      | ValueNone -> ()
