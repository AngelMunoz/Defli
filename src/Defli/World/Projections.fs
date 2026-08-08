namespace Defli.World

open System.Numerics
open AdaptiveSlop.Core
open Mibo.Layout
open Defli.World.Systems

// ─────────────────────────────────────────────────────────────
// World-owned CROSS-subsystem projections — joins/filters that
// touch two systems' maps. Sub-systems own projections derived
// purely from their own maps (see each system file).
//
//   Homing (#3)          — Projectiles.Rows × Enemies.Positions
//                          (the bind showcase: per-projectile
//                          dynamic dependency on the target's row)
//   RangeRing (#10)      — hover cell × Towers.CellIndex/Statics
//                          (AVal.bind UI-state join)
//   PlacementPreview (#5)— hover cell × Towers.CellIndex ×
//                          Economy.Gold (per-hover map2 fan-in;
//                          the full-tile filterA variant is exactly
//                          the wide fan-out the join assessment
//                          flagged — the per-hover fan-in gives the
//                          same UX with a shallow graph)
// ─────────────────────────────────────────────────────────────

[<Sealed>]
type Projections
  (
    enemies: Enemies.EnemiesModel,
    towers: Towers.TowersModel,
    projectiles: Projectiles.ProjectilesModel,
    economy: Economy.EconomyModel,
    buildable: CellGrid2D<MapTile>,
    hover: aval<struct (int * int) voption>
  ) =

  /// #3 Homing — one aval per projectile tracking its target's live
  /// position row through the graph. A dead target (row removed from
  /// Enemies.Positions) yields ValueNone ⇒ chooseA drops the entry:
  /// the render side stops drawing it while the sim expires the row.
  member val Homing: amap<int<ProjectileId>, HomingView> =
    projectiles.Rows
    |> AMap.chooseA(fun _ row ->
      enemies.Positions
      |> AMap.tryFind row.TargetEnemy
      |> AVal.map(fun pos ->
        pos
        |> ValueOption.map(fun p -> { Pos = row.Pos; TargetPos = p })
        |> ValueOption.toOption))

  /// #10 RangeRing — hovered own tower → its def (the view draws the
  /// range circle). Hover is shell-owned; CellIndex/Statics are Towers'.
  member val RangeRing: aval<TowerDef voption> =
    hover
    |> AVal.bind(fun cell ->
      match cell with
      | ValueNone -> AVal.constant ValueNone
      | ValueSome c -> towers.CellIndex |> AMap.tryFind c)
    |> AVal.bind(fun tid ->
      match tid with
      | ValueNone -> AVal.constant ValueNone
      | ValueSome tid -> towers.Statics |> AMap.tryFind tid)
    |> AVal.map(fun s -> s |> ValueOption.map(fun s -> s.Def))

  /// #5 PlacementPreview — the hovered cell's build status: blocked
  /// (path/occupied/out of grid), affordable, or too expensive.
  /// map2 fan-in over Gold; re-derives only when hover or gold moves.
  member val PlacementPreview: aval<PlacementStatus> =
    hover
    |> AVal.bind(fun cell ->
      match cell with
      | ValueNone -> AVal.constant PlacementStatus.Hidden
      | ValueSome struct (x, y) ->
        // The Buildable layer row decides (road cells were stamped
        // over with the non-buildable path tile; out of grid = absent).
        let buildableOk =
          buildable |> CellGrid2D.get x y |> ValueOption.exists _.Buildable

        if not buildableOk then
          AVal.constant PlacementStatus.Blocked
        else
          let cellKey = struct (x, y)

          towers.CellIndex
          |> AMap.tryFind cellKey
          |> AVal.map2
            (fun gold occupied ->
              if ValueOption.isSome occupied then
                PlacementStatus.Blocked
              elif gold >= TowerDefs.arrow.Cost then
                PlacementStatus.Affordable
              else
                PlacementStatus.TooExpensive)
            economy.Gold)
