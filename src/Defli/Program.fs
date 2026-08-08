module Defli.Program

open Mibo
open Mibo.Elmish
open Mibo.Elmish.Graphics
open Mibo.Elmish.Graphics2D
open Mibo.Input
open Defli

[<EntryPoint>]
let main _ =
  let program =
    Program.mkProgram Application.init Application.update
    |> Program.withConfig(
      GameConfig.withWidth 1280
      >> GameConfig.withHeight 800
      >> GameConfig.withTitle "Defli"
      >> GameConfig.withTargetFPS 60
    )
    |> Program.withAssetsBasePath "assets/"
    |> Program.withInput
    |> Program.withSubscription Application.subscribe
    |> Program.withTick Tick
    |> Program.withRenderer(fun () -> Renderer2D.create Application.view)
    |> Program.withRenderer(fun () ->
      Renderer2D.createWith Renderer2DConfig.noClear Application.hudView)

  let game = new RaylibGame<Model, Msg>(program)
  game.Run()
  0
