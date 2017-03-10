namespace GameOfLife

open System
open System.Windows
open System.Windows.Media
open System.Windows.Media.Imaging
open System.Threading.Tasks
open System.Collections.Generic
open System.Linq
open Akka
open Akka.FSharp

type Agent<'a> = MailboxProcessor<'a>

module GameOfLifeAgent =

    let alive = false
    let size = 100
    type Grid = {Width:int; Height:int}
    let gridProduct = size * size
    let grid = {Width=size;Height=size}
    
    [<Struct>]
    type Location = {x:int; y:int}

    type IDictionary<'a,'b> with
        member this.find k = this.[k]
   
    [<Struct>]
    type UpdateView =
        | Reset
        | Update of bool * Location

    let image = Controls.Image(Stretch=Stretch.Uniform)

    let applyGrid f =
        for x = 0 to grid.Width - 1 do
            for y = 0 to grid.Height - 1 do f x y

    let createImage pixels = BitmapSource.Create(grid.Width, grid.Height, 96., 96., PixelFormats.Gray8, null, pixels, size)

    let updateAgent () =
        let ctx = System.Threading.SynchronizationContext.Current
        let pixels = Array.zeroCreate<byte> (size*size)
        let agent = new Agent<UpdateView>(fun inbox ->
            let rec loop agentStates = async {
                let! msg = inbox.Receive()
                match msg with
                | UpdateView.Reset -> return! loop (Dictionary<Location, bool>(HashIdentity.Structural))
                | Update(alive, location) ->
                    agentStates.[location] <- alive
                    if agentStates.Count = gridProduct then
                        applyGrid (fun x y ->
                                match agentStates.TryGetValue({x=x;y=y}) with
                                | true, s when s = true ->
                                    pixels.[x+y*size] <- byte 128
                                | _ -> pixels.[x+y*size] <- byte 0)
                        do! Async.SwitchToContext ctx
                        image.Source <- createImage pixels
                        do! Async.SwitchToThreadPool()
                    return! loop agentStates
            }
            loop (Dictionary<Location, bool>(HashIdentity.Structural)))
        agent

    [<Interface>]
    type ICell =
        inherit IComparable<ICell>
        inherit IComparable

        abstract member location : Location
        abstract member Send : CellMessage -> unit
    
    and CellMessage =
        | NeighbourState of cell:ICell * isalive:bool
        | State of cellstate:ICell
        | Neighbours of cells:ICell list
        | Reset

    type State =
        {
            neighbours:ICell list
            wasAlive:bool
            isAlive:bool 
        }
        with static member createDeafault isAlive = { neighbours=[];isAlive=isAlive; wasAlive=false; }
    
    type CellAkka(location, actorSystem, name, alive, updateAgent:Agent<_>) as this =
        let hashCode = hash location
        let neighbourStates = Dictionary<ICell, bool>()
        let actorCell = 
            spawn actorSystem name <| 
                fun (mailbox:Akka.FSharp.Actors.Actor<CellMessage>) ->
                    let rec loop state =
                        actor {   
                            let! msg = mailbox.Receive()
                            match msg with
                            | Reset ->
                                state.neighbours |> Seq.iter(fun cell -> cell.Send(State(this)))
                                neighbourStates.Clear()
                                return! loop { state with wasAlive=state.isAlive }
                            | Neighbours(neighbours) -> return! loop { state with neighbours=neighbours }
                            | State(c) -> c.Send(NeighbourState(this, state.wasAlive))
                                          return! loop state
                            | NeighbourState(cell, alive) ->
                                neighbourStates.[cell] <- alive
                                if neighbourStates.Count = 8 then
                                    let aliveState =
                                        match neighbourStates |> Seq.filter(fun (KeyValue(_,v)) -> v) |> Seq.length with
                                        | a when a > 3  || a < 2 -> false
                                        | 3 -> true
                                        | _ -> state.isAlive
                                    updateAgent.Post(UpdateView.Update(aliveState, location))
                                    return! loop { state with isAlive = aliveState }
                                else return! loop state
                        }
                    loop (State.createDeafault alive)

        member this.location = location
        member this.Send(msg) = actorCell.Tell(msg, actorCell)

        interface ICell with
            member this.location = this.location
            member this.Send(msg) = this.Send msg

        override this.Equals(o) =
            match o with
            | :? ICell as cell -> this.location = cell.location
            | _ -> false
        override this.GetHashCode() = hashCode

        interface IComparable<ICell> with
            member this.CompareTo(other) = compare this.location other.location
        interface IComparable with
            member this.CompareTo(o) =
              match o with
              | :? ICell as other -> (this :> IComparable<_>).CompareTo other
              | _ -> 1

    let getRandomBool =
        let random = Random(int System.DateTime.Now.Ticks)
        fun () -> random.Next() % 2 = 0

    let run() =
        let updateAgent = updateAgent()
        let actorSystem = System.create "GameOfLifeActors" <| Akka.Configuration.ConfigurationFactory.Default()

        let cells = seq {
            for x = 0 to grid.Width - 1 do
                for y = 0 to grid.Height - 1 do
                    let name = (sprintf "Actor=x%dy%d" x y)
                    yield (x,y), CellAkka({x=x;y=y}, actorSystem, name, alive=getRandomBool(), updateAgent=updateAgent) :> ICell } |> dict

        let neighbours (location:Location) = seq {
            for x = location.x - 1 to location.x + 1 do
                for y = location.y - 1 to location.y + 1 do
                    if x <> location.x || y <> location.y then
                        yield cells.find ((x + grid.Width) % grid.Width, (y + grid.Height) % grid.Height) }

        applyGrid (fun x y ->
                let agent = cells.find (x,y)
                let neighbours = neighbours {x=x;y=y} |> Seq.toList
                agent.Send(Neighbours(neighbours)))

        let updateView() =
           updateAgent.Post(UpdateView.Reset)
           cells.Values.AsParallel().ForAll(fun cell -> cell.Send(Reset))

        do updateAgent.Start()

        let timer = new System.Timers.Timer(200.)
        let dispose = timer.Elapsed |> Observable.subscribe(fun _ -> updateView())
        timer.Start()
        dispose