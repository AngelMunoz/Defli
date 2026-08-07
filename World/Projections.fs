namespace Defli.World

// ─────────────────────────────────────────────────────────────
// World-owned CROSS-subsystem projections — joins/filters that
// touch two systems' maps (Homing, Buildable, RangeRing). Built
// once at WorldModel construction; sub-systems own projections
// derived purely from their own maps (see each system file).
//
// Phase 0: empty scaffolding — the derivations land in Phases 1-2.
// ─────────────────────────────────────────────────────────────

[<Sealed>]
type Projections() =
    // Cross-subsystem projections (Phases 1-2):
    //   Homing    = Projectiles.Rows × Enemies.Positions
    //   Buildable = Map build tiles × Economy.Gold
    //   RangeRing = hover cell × Towers.Statics
    member _.Empty = ()
