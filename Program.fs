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
        |> Program.withConfig (fun cfg ->
            { cfg with
                Width = 1280
                Height = 800
                Title = "Defli" })
        |> Program.withAssetsBasePath "assets/"
        |> Program.withInput
        |> Program.withSubscription Application.subscribe
        |> Program.withTick Tick
        |> Program.withRenderer (fun () -> Renderer2D.create Application.view)

    let game = new RaylibGame<Model, Msg>(program)
    game.Run()
    0
