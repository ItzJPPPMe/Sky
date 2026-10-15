namespace Sky

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

module Program =

    type Snapshot =
        { Kind: string
          Jobs: DownloadStatus list
          History: HistoryEntry list }

    let private config = DownloaderConfig.defaultConfig ()

    let private jsonOptions =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        options

    let private serialize (value: 'T) =
        JsonSerializer.Serialize(value, jsonOptions)

    let private failure (message: string) =
        Results.Problem(detail = message, statusCode = StatusCodes.Status400BadRequest)

    let private health () =
        {| service = "sky"
           version = "0.1.0"
           status = "ok"
           outputDirectory = config.OutputDirectory
           platforms = [ "YouTube"; "Twitch"; "Kick"; "HLS" ]
           utc = DateTimeOffset.UtcNow |}

    let private sendText (socket: WebSocket) (token: CancellationToken) (payload: string) =
        let bytes = Encoding.UTF8.GetBytes payload
        socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token)

    let private handleWebSocket (context: HttpContext) =
        task {
            if not (context.WebSockets.IsWebSocketRequest) then
                context.Response.StatusCode <- StatusCodes.Status400BadRequest
                return ()
            else
                let channel = Channel.CreateUnbounded<ProgressEvent>()
                let source = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
                use socket = context.WebSockets.AcceptWebSocketAsync().GetAwaiter().GetResult()

                use subscription =
                    Downloader.subscribe(
                        Action<ProgressEvent>(fun event ->
                            channel.Writer.TryWrite event |> ignore)
                    )

                try
                    let snapshot =
                        { Kind = ProgressEventKind.history
                          Jobs = Downloader.listJobs ()
                          History = Downloader.listHistory () }

                    do! sendText socket source.Token (serialize snapshot)

                    let sendLoop =
                        task {
                            let mutable running = true

                            while running do
                                let! event = channel.Reader.ReadAsync().AsTask()
                                do! sendText socket source.Token (serialize event)
                        }

                    let receiveLoop =
                        task {
                            try
                                let buffer = Array.zeroCreate<byte> 4096
                                let mutable running = true

                                while running do
                                    let! incoming = socket.ReceiveAsync(ArraySegment<byte>(buffer), source.Token)

                                    if incoming.MessageType = WebSocketMessageType.Close then
                                        running <- false
                                    else
                                        let message =
                                            Encoding.UTF8
                                                .GetString(buffer, 0, incoming.Count)
                                                .Trim()
                                                .ToLowerInvariant()

                                        if message = "ping" then
                                            channel.Writer.TryWrite
                                                { Kind = ProgressEventKind.log
                                                  Status = DownloadStatus.empty "" ""
                                                  Entry = None }
                                            |> ignore
                            with _ ->
                                ()
                        }

                    do! Task.WhenAll([| sendLoop :> Task; receiveLoop :> Task |])
                finally
                    channel.Writer.TryComplete() |> ignore
                    source.Cancel()
                    source.Dispose()
        }

    let private buildApplication () =
        let builder = WebApplication.CreateBuilder()

        builder.WebHost.UseUrls(sprintf "http://%s:%d" config.Host config.Port) |> ignore

        builder.Services.ConfigureHttpJsonOptions(fun options ->
            options.SerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
            options.SerializerOptions.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull) |> ignore

        builder.Services.AddCors(fun options ->
            options.AddPolicy(
                "sky",
                fun policy ->
                    policy.SetIsOriginAllowed(Func<string, bool>(fun _ -> true)) |> ignore
                    policy.AllowAnyHeader() |> ignore
                    policy.AllowAnyMethod() |> ignore
            )) |> ignore

        let app = builder.Build()

        app.UseWebSockets() |> ignore
        app.UseCors("sky") |> ignore

        app.MapGet("/api/health", Func<IResult>(fun () -> Results.Ok(health()))) |> ignore

        app.MapGet("/api/platforms", Func<IResult>(fun () -> Results.Ok(Resolvers.platforms()))) |> ignore

        app.MapGet(
            "/api/settings",
            Func<IResult>(fun () ->
                let current = AppSettings.load ()

                Results.Ok
                    {| settings = current
                       settingsPath = AppSettings.filePath |> Guard.text
                       defaultOutputDirectory = (DownloaderConfig.defaultConfig ()).OutputDirectory |> Guard.text
                       segments = Resolvers.platforms () |})
        )
        |> ignore

        app.MapPost(
            "/api/settings",
            Func<Settings, Task<IResult>>(fun payload ->
                task {
                    if isNull (box payload) then
                        return failure "Ayar gövdesi okunamadı."
                    else
                        let saved = AppSettings.save payload
                        Tools.invalidate ()
                        do! Sessions.purge ()
                        Sessions.purge () |> ignore
                        return Results.Ok {| settings = saved |}
                })
        )
        |> ignore

        app.MapPost(
            "/api/settings/reset",
            Func<Task<IResult>>(fun () ->
                task {
                    let saved = AppSettings.reset ()
                    Tools.invalidate ()
                    return Results.Ok {| settings = saved |}
                })
        )
        |> ignore

        app.MapPost(
            "/api/tools/refresh",
            Func<Task<IResult>>(fun () ->
                task {
                    Tools.invalidate ()

                    let! statuses = Resolvers.toolStatuses ()
                    return Results.Ok statuses
                })
        )
        |> ignore

        app.MapGet(
            "/api/thumbnail",
            Func<string, Task<IResult>>(fun url ->
                task {
                    let safe = Guard.trimmed url

                    if String.IsNullOrWhiteSpace safe then
                        return Results.NotFound()
                    else
                        match Guard.tryUri safe with
                        | None -> return Results.NotFound()
                        | Some uri ->
                            if uri.Scheme <> Uri.UriSchemeHttps && uri.Scheme <> Uri.UriSchemeHttp then
                                return Results.NotFound()
                            else
                                let! content = Http.getStream safe CancellationToken.None

                                match content with
                                | Some bytes ->
                                    let payload: byte[] = bytes
                                    return Results.File(payload, contentType = "image/jpeg")
                                | None ->
                                    return Results.NotFound()
                })
        )
        |> ignore

        app.MapGet(
            "/api/tools",
            Func<Task<IResult>>(fun () ->
                task {
                    let! statuses = Resolvers.toolStatuses()
                    return Results.Ok statuses
                })
        )
        |> ignore

        app.MapGet("/api/jobs", Func<IResult>(fun () -> Results.Ok(Downloader.listJobs()))) |> ignore

        app.MapGet("/api/history", Func<IResult>(fun () -> Results.Ok(Downloader.listHistory()))) |> ignore

        app.MapPost(
            "/api/resolve",
            Func<ResolveRequest, Task<IResult>>(fun request ->
                task {
                    if isNull (box request) || String.IsNullOrWhiteSpace request.Url then
                        return failure "Çözümlenecek bağlantı boş olamaz."
                    else
                        use timeout =
                            new CancellationTokenSource(TimeSpan.FromSeconds(90.0))

                        let! result = Resolvers.resolve request.Url timeout.Token

                        return
                            match result with
                            | Ok resolution ->
                                Results.Ok(Sessions.save resolution)
                            | Error message ->
                                failure message
                })
        )
        |> ignore

        app.MapGet(
            "/api/jobs/{id}",
            Func<string, IResult>(fun id ->
                match Downloader.tryGetJob id with
                | Some status -> Results.Ok status
                | None -> Results.NotFound())
        )
        |> ignore

        app.MapPost(
            "/api/jobs",
            Func<DownloadRequest, Task<IResult>>(fun request ->
                task {
                    if isNull (box request) then
                        return failure "İstek gövdesi okunamadı."
                    else
                        let! result = Downloader.startJob request

                        return
                            match result with
                            | Ok status -> Results.Ok status
                            | Error message -> failure message
                })
        )
        |> ignore

        app.MapDelete(
            "/api/jobs/{id}",
            Func<string, IResult>(fun id ->
                if Downloader.cancelJob id then
                    Results.NoContent()
                else
                    Results.NotFound())
        )
        |> ignore

        app.MapPost(
            "/api/jobs/{id}/pause",
            Func<string, IResult>(fun id ->
                if Downloader.pauseJob id then
                    Results.NoContent()
                else
                    Results.NotFound())
        )
        |> ignore

        app.MapPost(
            "/api/jobs/{id}/resume",
            Func<string, Task<IResult>>(fun id ->
                task {
                    let! resumed = Downloader.resumeJob id

                    if resumed then
                        return Results.NoContent()
                    else
                        return Results.NotFound()
                })
        )
        |> ignore

        app.MapDelete(
            "/api/history/{id}",
            Func<string, IResult>(fun id ->
                Downloader.removeHistory id
                Results.NoContent())
        )
        |> ignore

        app.Use(Func<HttpContext, Func<Task>, Task>(fun context next ->
            task {
                if context.Request.Path = PathString "/ws" && context.WebSockets.IsWebSocketRequest then
                    do! handleWebSocket context
                else
                    do! next.Invoke()
            })) |> ignore

        app.Lifetime.ApplicationStopping.Register(Action(fun () -> Downloader.shutdown())) |> ignore

        app

    [<EntryPoint>]
    let main _argv =
        let application = buildApplication ()

        Console.WriteLine(sprintf "Sky back-end dinleniyor: http://%s:%d" config.Host config.Port)
        Console.WriteLine(sprintf "Çıktı dizini: %s" config.OutputDirectory)

        let exitCode =
            try
                application.Run()
                0
            with
            | :? IOException as ex ->
                Console.Error.WriteLine(sprintf "Sunucu başlatılamadı: %s" ex.Message)
                1
            | ex ->
                Console.Error.WriteLine(sprintf "Beklenmeyen hata: %s" ex.Message)
                1

        exitCode