open GameOfLife
open System
open System.Windows
open System.Threading
open System.Windows.Threading
open System.Windows.Media.Imaging
open GameOfLifeAgent

[<System.STAThread>]
[<EntryPoint>]
let main argv =

    SynchronizationContext.SetSynchronizationContext(
        new DispatcherSynchronizationContext(
            Dispatcher.CurrentDispatcher))

    let form = Window(Content=image, Title="Game of Life")
    let application = new Application()
    use dipose = GameOfLifeAgent.run()
    application.Run(form) |> ignore

    0

