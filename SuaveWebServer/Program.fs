open System
open System.Threading
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.Json
open System.Net

open System.Runtime.Serialization

//http://putridparrot.com/blog/getting-restful-with-suave/
//https://suave.io/restful.html
    //https://www.youtube.com/watch?v=AMjcjXIMzmA&index=4&list=PLEoMzSkcN8oNiJ67Hd7oRGgD1d4YBxYGC

//TODO: Enhance LocationAgent (MailboxProcessor) to support notification of new events (see Petricek http://tomasp.net/blog/agent-event-reporting.aspx/)
//TODO: FUTURE: server-side web-socket support to push real-time region updates to connected mobile device app (https://suave.io/websockets.html) - https://blog.xamarin.com/developing-real-time-communication-apps-with-websocket/

type RegionEventType = 
 | EnterRegion
 | ExitRegion
  
[<DataContract>]
type RegionEvent =
   { 
      [<field: DataMember(Name = "regionName")>]
      regionName : string;
      [<field: DataMember(Name = "deviceID")>]
      deviceID : string;
      [<field: DataMember(Name = "timeStamp")>]
      timeStamp : int64;
      [<field: DataMember(Name = "eventType")>]
      eventType : RegionEventType;
   }

// unused left as an example
let sleep milliseconds message: WebPart =
  fun (x : HttpContext) ->
    async {
      do! Async.Sleep milliseconds
      return! OK message x
    }

let parseAlert (str:String) =
   let prefix = "channel1.data.raw%20-%3E%20"
   let suffix = "%0D"
   let index1 = str.LastIndexOf prefix + String.length prefix
   let index2 = str.LastIndexOf suffix
   str.Substring (index1, index2 - index1 )
    
let regionEventProcessor func:WebPart = 
   mapJson (fun (regEvent:RegionEvent) -> 
                        func regEvent
                        regEvent)

let rec updateDeviceState re list =
      match list with
      | [] -> []
      | reHead :: xs -> if reHead.deviceID = re.deviceID then re :: xs else (reHead :: updateDeviceState re xs)
 
type LocationAgentMsg = 
    | Exit
    | Reset
    | UpdateWith of RegionEvent
    | IsKnownDevice of String * AsyncReplyChannel<Boolean>
    | DumpDevicesState  of AsyncReplyChannel<String>

type DevicesState = 
   { KnownDevicesLocationList : RegionEvent list } 
   static member Empty = {KnownDevicesLocationList = [] }
   member x.IsKnownDevice address = 
      List.exists (fun devLoc -> devLoc.deviceID = address) x.KnownDevicesLocationList
   member x.UpdateDeviceState re = updateDeviceState re x.KnownDevicesLocationList
   member x.UpdateWith (re:RegionEvent) = 
      { KnownDevicesLocationList = 
          if x.IsKnownDevice re.deviceID then
              x.UpdateDeviceState re
          else
              re :: x.KnownDevicesLocationList}

type LocationAgent() =
    let locationAgentMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec locationAgentLoop dsi =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Reset -> return! locationAgentLoop DevicesState.Empty
                        | UpdateWith re -> return! locationAgentLoop (dsi.UpdateWith re)
                        | IsKnownDevice (addr, replyChannel) -> 
                            replyChannel.Reply (dsi.IsKnownDevice addr)
                            return! locationAgentLoop dsi
                        | DumpDevicesState replyChannel -> 
                            replyChannel.Reply (sprintf "%A" dsi)
                            return! locationAgentLoop dsi
                      }
            locationAgentLoop DevicesState.Empty
        )
    member this.Exit() = locationAgentMailboxProcessor.Post(Exit)
    member this.Empty() = locationAgentMailboxProcessor.Post(Reset)
    member this.UpdateWith re = locationAgentMailboxProcessor.Post(UpdateWith re)
    member this.IsKnownDevice addr = locationAgentMailboxProcessor.PostAndReply((fun reply -> IsKnownDevice(addr,reply)), timeout = 2000)
    member this.DumpDevicesState addr = locationAgentMailboxProcessor.PostAndReply((fun reply -> DumpDevicesState reply), timeout = 2000)

       
[<EntryPoint>]
let main argv = 
  let cts = new CancellationTokenSource()
  let conf = { defaultConfig with bindings = [ HttpBinding.create HTTP IPAddress.Any (uint16 argv.[0]) ] 
                                             cancellationToken = cts.Token }
  // 8082us

  let locationAgent = new LocationAgent()
  let mutable key = 'X'

  let app =
      choose
        [ GET >=> choose
            [ path "/hello" >=> OK "Hello GET"
              path "/goodbye" >=> OK "Good bye GET"  ]
          POST >=> choose
            [ path "/hello" >=> OK "Hello POST"
              path "/zd620Alert" >=> request (fun r -> do printf "Scanned Barcode: %s\n" (parseAlert (System.Text.Encoding.ASCII.GetString r.rawForm))
                                                       OK ("REPLY"))
              path "/regionEvent" >=> regionEventProcessor locationAgent.UpdateWith  ]  ]
  let listening, server = startWebServerAsync conf app
 
  Async.Start(server, cts.Token)

  printfn "Make requests now"
  do key <- Char.ToUpper (Console.ReadKey true).KeyChar
  while key <> 'X' do
     if key = 'U' then 
        printfn "Known position of devices:\n%s\n" (locationAgent.DumpDevicesState())
     else
        printfn "Invalid Character typed: %c" key
     do key <- Char.ToUpper (Console.ReadKey true).KeyChar
    
  cts.Cancel()

  0 // return an integer exit code