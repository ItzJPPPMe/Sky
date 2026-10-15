namespace Sky

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

type ToolInvocation =
    { Kind: string
      Executable: string
      Arguments: string list }

module Tools =

    let private config = DownloaderConfig.defaultConfig ()

    let private suffix =
        if OperatingSystem.IsWindows() then
            ".exe"
        else
            ""

    let private existingFiles (paths: string list) =
        paths
        |> List.filter (fun path -> not (String.IsNullOrWhiteSpace path))
        |> List.filter (fun path -> File.Exists path)

    let private candidatePaths (name: string) (configuredPath: string) =
        let fromEnvironment =
            if name = "yt-dlp" then
                [ config.YtDlpPath ]
            elif name = "ffmpeg" then
                [ config.FfMpegPath ]
            else
                []

        let explicitPaths =
            [ Guard.text configuredPath ]
            @ fromEnvironment
            |> List.filter (fun path -> not (String.IsNullOrWhiteSpace path))
        let bundled =
            existingFiles
                [ Path.Combine(AppContext.BaseDirectory, "tools", name + suffix)
                  Path.Combine(AppContext.BaseDirectory, name + suffix) ]

        let onPath =
            let separator = if OperatingSystem.IsWindows() then ';' else ':'

            (Environment.GetEnvironmentVariable("PATH") |> Guard.text)
                .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.collect (fun directory ->
                try
                    let safe = Guard.text directory
                    existingFiles [ Path.Combine(safe.Trim(), name + suffix) ]
                with _ ->
                    [])

        explicitPaths @ bundled @ onPath

    let private pythonExecutables =
        [ "python"
          "python3"
          "py" ]

    let private runProcess (invocation: ToolInvocation) (arguments: string list) (timeoutMs: int) =
        task {
            try
                let info = new ProcessStartInfo()
                info.FileName <- invocation.Executable
                info.UseShellExecute <- false
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                info.CreateNoWindow <- true

                for argument in invocation.Arguments do
                    info.ArgumentList.Add argument

                for argument in arguments do
                    info.ArgumentList.Add argument

                let started = Process.Start(info)

                match started with
                | null ->
                    return Error "Araç başlatılamadı."
                | child ->
                    use childProcess = child
                    let! output = childProcess.StandardOutput.ReadToEndAsync()
                    let! errorOutput = childProcess.StandardError.ReadToEndAsync()
                    let mutable exited = childProcess.WaitForExit(timeoutMs)

                    if not exited then
                        try
                            childProcess.Kill(true)
                        with _ ->
                            ()

                        exited <- true

                    let combined =
                        [ output.Trim(); errorOutput.Trim() ]
                        |> List.filter (fun value -> value.Length > 0)
                        |> String.concat Environment.NewLine

                    let exitCode = childProcess.ExitCode

                    if exitCode = 0 then
                        return Ok combined
                    else
                        return Error combined
            with ex ->
                return Error ex.Message
        }

    let private probeList (attempts: (ToolInvocation * string list) list) =
        task {
            let mutable found: (ToolInvocation * string) option = None
            let mutable pending = attempts

            while found.IsNone && not (List.isEmpty pending) do
                let invocation, arguments = List.head pending
                let! result = runProcess invocation arguments 20000

                match result with
                | Ok version when not (String.IsNullOrWhiteSpace version) ->
                    found <- Some(invocation, version.Trim())
                | _ ->
                    pending <- List.tail pending

            return found
        }
    let private ytdlpAttempts () =
        let settings = AppSettings.load ()

        [ for path in candidatePaths "yt-dlp" settings.YtDlpPath do
              yield
                  { Kind = "direct"
                    Executable = path
                    Arguments = [] },
                  [ "--version" ]

          for executable in pythonExecutables do
              yield
                  { Kind = "python"
                    Executable = executable
                    Arguments = [ "-m"; "yt_dlp" ] },
                  [ "--version" ] ]

    let private ffmpegAttempts () =
        let settings = AppSettings.load ()

        [ for path in candidatePaths "ffmpeg" settings.FfMpegPath do
              yield
                  { Kind = "direct"
                    Executable = path
                    Arguments = [] },
                  [ "-version" ] ]

    let private probeSlot = obj ()
    let private ytdlpProbe : Lazy<Task<(ToolInvocation * string) option>> option ref = ref None
    let private ffmpegProbe : Lazy<Task<(ToolInvocation * string) option>> option ref = ref None

    let private resolveProbe (slot: Lazy<Task<(ToolInvocation * string) option>> option ref) (factory: unit -> Task<(ToolInvocation * string) option>) =
        lock probeSlot (fun () ->
            match !slot with
            | Some existing ->
                existing.Value
            | None ->
                let created = Lazy<_>(factory, LazyThreadSafetyMode.ExecutionAndPublication)
                slot := Some created
                created.Value)

    let ytdlp () =
        resolveProbe ytdlpProbe (fun () -> probeList (ytdlpAttempts ()))

    let ffmpeg () =
        resolveProbe ffmpegProbe (fun () -> probeList (ffmpegAttempts ()))

    let invalidate () =
        lock probeSlot (fun () ->
            ytdlpProbe := None
            ffmpegProbe := None)

    let capture (invocation: ToolInvocation) (arguments: string list) (timeoutMs: int) =
        runProcess invocation arguments timeoutMs

    let private status (name: string) (result: (ToolInvocation * string) option) =
        { Name = name
          Available = result.IsSome
          Path =
            match result with
            | Some(invocation, _) -> invocation.Executable
            | None -> ""
          Version =
            match result with
            | Some(_, version) ->
                let lines = version.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

                if lines.Length = 0 then
                    ""
                else
                    lines.[0].Trim()
            | None -> "" }

    let toolStatuses () =
        task {
            let! ytdlp = ytdlp ()
            let! ffmpeg = ffmpeg ()
            return [ status "yt-dlp" ytdlp; status "ffmpeg" ffmpeg ]
        }

    let toolRequiredMessage (name: string) =
        match name with
        | "yt-dlp" ->
            "YouTube desteği için yt-dlp gerekli. 'pip install yt-dlp' komutuyla kurun ya da SKY_YTDLP_PATH ortam değişkeniyle yolunu tanımlayın."
        | _ ->
            String.Format("{0} bulunamadı.", name)
