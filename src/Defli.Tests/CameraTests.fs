module Defli.Tests.CameraTests

open Expecto
open System.Numerics
open Defli.World.Systems
open Defli.World.Systems.Camera

// Phase 4 — the Camera sub-system (Kimo analog): the state IS the
// mutable raylib Camera2D, mutated in place by the subsystem. All
// assertions read the underlying camera's fields (Target/Zoom).

let private worldSize = Vector2(1280f, 768f)
let private viewport = Vector2(1280f, 800f)
let private model() = Camera.Camera.init worldSize

let tests =
  testList "Camera" [
    testCase "init centers on the world center at zoom 1" (fun () ->
      let m = model()
      Expect.equal m.Camera.Target (Vector2(640f, 384f)) "target"
      Expect.equal m.Camera.Zoom 1f "zoom"
      Expect.equal m.WorldSize worldSize "world size")

    testCase "setViewport sets the screen offset once" (fun () ->
      let m = model()
      Camera.setViewport viewport m
      Expect.equal m.Camera.Offset (viewport / 2f) "offset")

    testCase "Pan moves the target opposite the drag, scaled by zoom" (fun () ->
      let m = model()
      // Drag right 100 px at zoom 1 → the world moves left 100.
      let m = Camera.Camera.update (CameraMsg.Pan(Vector2(100f, 0f))) m
      Expect.equal m.Camera.Target (Vector2(540f, 384f)) "pan at zoom 1"

      // At zoom 2 the same drag moves the world half as far.
      let m = Camera.Camera.update (CameraMsg.ZoomBy 2f) m
      let m = Camera.Camera.update (CameraMsg.Pan(Vector2(100f, 0f))) m
      Expect.equal m.Camera.Target (Vector2(490f, 384f)) "pan at zoom 2")

    testCase "ZoomBy multiplies and clamps to the zoom limits" (fun () ->
      let m = model()
      let m = Camera.Camera.update (CameraMsg.ZoomBy 2f) m
      Expect.equal m.Camera.Zoom 2f "zoomed in"

      let m = Camera.Camera.update (CameraMsg.ZoomBy 2f) m
      Expect.equal m.Camera.Zoom Camera.MaxZoom "clamped at max"

      let m = Camera.Camera.update (CameraMsg.ZoomBy 0.01f) m
      Expect.equal m.Camera.Zoom Camera.MinZoom "clamped at min")

    testCase "Shake sets the timer, tick decays it, offset expires" (fun () ->
      let m = model()
      let m = Camera.Camera.update (CameraMsg.Shake 8f) m
      Expect.equal m.ShakeRemaining Camera.ShakeDuration "timer set"
      Expect.notEqual (shakeOffset m) Vector2.Zero "offset active"

      Camera.Camera.tick 0.2f m
      Expect.equal m.ShakeRemaining (Camera.ShakeDuration - 0.2f) "decayed"

      Camera.Camera.tick 1f m
      Expect.equal m.ShakeRemaining 0f "expired"
      Expect.equal (shakeOffset m) Vector2.Zero "offset zero when expired")

    testCase "Reset restores the world center at zoom 1" (fun () ->
      let m = model()
      let m = Camera.Camera.update (CameraMsg.Pan(Vector2(400f, 300f))) m
      let m = Camera.Camera.update (CameraMsg.ZoomBy 2f) m
      let m = Camera.Camera.update (CameraMsg.Shake 8f) m
      let m = Camera.Camera.update CameraMsg.Reset m
      Expect.equal m.Camera.Target (Vector2(640f, 384f)) "target"
      Expect.equal m.Camera.Zoom 1f "zoom"
      Expect.equal m.ShakeRemaining 0f "shake cleared")

    testCase "cullingBounds inflates the viewport rect by 30%" (fun () ->
      // Zoom 1, world-centered: exact view = (0, -16, 1280, 800);
      // culling = that rect grown 15% per side (width/height × 1.3).
      let m = model()
      let rect = Camera.cullingBounds m viewport
      Expect.equal rect.X -192f "left margin"
      Expect.equal rect.Y -136f "top margin"
      Expect.equal rect.Width 1664f "width × 1.3"
      Expect.equal rect.Height 1040f "height × 1.3")

    testCase "clampToWorld pins an axis whose view is wider than the world" (fun () ->
      // Zoom 1: view = (1280, 800) vs world (1280, 768) — both axes
      // wider or equal → both pinned to the center. Mibo's
      // clampTarget mutates the underlying camera in place.
      let m = model()
      m.Camera.Target <- Vector2.Zero
      clampToWorld m viewport
      Expect.equal m.Camera.Target (Vector2(640f, 384f)) "centered")

    testCase "clampToWorld clamps a smaller view to the world edges" (fun () ->
      // Zoom 2: view = (640, 400) — target range X [320..960], Y [200..568].
      let m = model()
      m.Camera.Zoom <- 2f
      m.Camera.Target <- Vector2(10000f, 10000f)
      clampToWorld m viewport
      Expect.equal m.Camera.Target (Vector2(960f, 568f)) "far corner"

      m.Camera.Target <- Vector2(-10000f, -10000f)
      clampToWorld m viewport
      Expect.equal m.Camera.Target (Vector2(320f, 200f)) "near corner")
  ]
