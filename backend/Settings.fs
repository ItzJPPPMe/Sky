namespace Sky

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

type Settings =
    { AccentColor: string
      Theme: string
      SegmentConcurrency: int
      SegmentRetries: int
      RequestTimeoutSeconds: int
      LivePollDivisor: int
      PreferredLanguage: string
      OutputDirectory: string
      FileNameTemplate: string
      YtDlpPath: string
      FfMpegPath: string
      MaxParallelJobs: int
      SpeedLimitKbps: int
      AutoOpenFolder: bool
      Notifications: bool
      HistoryLimit: int
      ProxyUrl: string
      AutoRemuxToMp4: bool
      ResumeDownloads: bool
      ConfirmBeforeStart: bool
      AnimationLevel: string }

module AppSettings =

    let private config = DownloaderConfig.defaultConfig ()
    let private lockObject = obj ()
    let private cache = ref None

    let filePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Sky",
            "settings.json"
        )

    let private clampInt name value fallback minimum maximum =
        if value < minimum || value > maximum then
            fallback
        else
            value

    let private normalizeAccent (value: string) =
        let safe = Guard.trimmed value

        if String.IsNullOrWhiteSpace safe then
            "#38bdf8"
        elif safe.StartsWith("#", StringComparison.Ordinal) && safe.Length = 7 then
            safe.ToLowerInvariant()
        else
            "#38bdf8"

    let private normalizeTheme (value: string) =
        match Guard.trimmed value |> fun safe -> safe.ToLowerInvariant() with
        | "light" -> "light"
        | "system" -> "system"
        | "dark" -> "dark"
        | _ -> "dark"

    let private normalizeAnimation (value: string) =
        match Guard.trimmed value |> fun safe -> safe.ToLowerInvariant() with
        | "reduced" -> "reduced"
        | "full" -> "full"
        | _ -> "full"

    let defaultSettings =
        { AccentColor = "#38bdf8"
          Theme = "dark"
          SegmentConcurrency = 8
          SegmentRetries = 3
          RequestTimeoutSeconds = 30
          LivePollDivisor = 2
          PreferredLanguage = "tr"
          OutputDirectory = config.OutputDirectory
          FileNameTemplate = "{title}"
          YtDlpPath = ""
          FfMpegPath = ""
          MaxParallelJobs = 2
          SpeedLimitKbps = 0
          AutoOpenFolder = false
          Notifications = true
          HistoryLimit = 200
          ProxyUrl = ""
          AutoRemuxToMp4 = true
          ResumeDownloads = true
          ConfirmBeforeStart = false
          AnimationLevel = "full" }

    let sanitize (settings: Settings) =
        { settings with
            AccentColor = normalizeAccent settings.AccentColor
            Theme = normalizeTheme settings.Theme
            AnimationLevel = normalizeAnimation settings.AnimationLevel
            SegmentConcurrency =
                clampInt "SegmentConcurrency" settings.SegmentConcurrency defaultSettings.SegmentConcurrency 1 64
            SegmentRetries =
                clampInt "SegmentRetries" settings.SegmentRetries defaultSettings.SegmentRetries 0 10
            RequestTimeoutSeconds =
                clampInt "RequestTimeoutSeconds" settings.RequestTimeoutSeconds defaultSettings.RequestTimeoutSeconds 5 300
            LivePollDivisor =
                clampInt "LivePollDivisor" settings.LivePollDivisor defaultSettings.LivePollDivisor 1 10
            MaxParallelJobs =
                clampInt "MaxParallelJobs" settings.MaxParallelJobs defaultSettings.MaxParallelJobs 1 8
            SpeedLimitKbps =
                clampInt "SpeedLimitKbps" settings.SpeedLimitKbps defaultSettings.SpeedLimitKbps 0 100000
            HistoryLimit =
                clampInt "HistoryLimit" settings.HistoryLimit defaultSettings.HistoryLimit 10 5000
            PreferredLanguage = Guard.trimmed settings.PreferredLanguage
            OutputDirectory = Guard.trimmed settings.OutputDirectory
            FileNameTemplate =
                let value = Guard.trimmed settings.FileNameTemplate

                if String.IsNullOrWhiteSpace value then
                    defaultSettings.FileNameTemplate
                else
                    value
            YtDlpPath = Guard.trimmed settings.YtDlpPath
            FfMpegPath = Guard.trimmed settings.FfMpegPath
            ProxyUrl = Guard.trimmed settings.ProxyUrl }

    let private serializerOptions =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        options.WriteIndented <- true
        options

    let private saveToDisk (settings: Settings) =
        try
            let directory = Guard.text (Path.GetDirectoryName filePath)

            if not (String.IsNullOrWhiteSpace directory) then
                Directory.CreateDirectory directory |> ignore

            File.WriteAllText(filePath, JsonSerializer.Serialize(settings, serializerOptions))
            true
        with _ ->
            false

    let load () =
        let locked = lock lockObject (fun () -> !cache)

        match locked with
        | Some settings ->
            settings
        | None ->
            let loaded =
                try
                    if File.Exists filePath then
                        let text = File.ReadAllText filePath
                        let parsed = JsonSerializer.Deserialize<Settings>(text, serializerOptions)

                        match parsed with
                        | null -> defaultSettings
                        | value -> sanitize value
                    else
                        defaultSettings
                with _ ->
                    defaultSettings

            lock lockObject (fun () -> cache := Some loaded)
            loaded

    let save (settings: Settings) =
        let clean = sanitize settings
        lock lockObject (fun () -> cache := Some clean)
        saveToDisk clean |> ignore
        clean

    let update (mapper: Settings -> Settings) =
        mapper (load ()) |> save

    let reset () =
        save defaultSettings

    let fileNameFor (template: string) (title: string) (platform: string) (quality: string) (extension: string) =
        let now = DateTimeOffset.Now

        let replacements =
            [ ("{title}", title)
              ("{platform}", platform)
              ("{quality}", quality)
              ("{date}", now.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture))
              ("{time}", now.ToString("HH-mm-ss", Globalization.CultureInfo.InvariantCulture))
              ("{datetime}", now.ToString("yyyy-MM-dd_HH-mm-ss", Globalization.CultureInfo.InvariantCulture))
              ("{ext}", extension) ]

        let mutable value = Guard.trimmed template

        for (token, replacement) in replacements do
            value <- value.Replace(token, replacement)

        let builder = System.Text.StringBuilder()

        for character in value do
            let invalid = Path.GetInvalidFileNameChars()

            builder.Append(if invalid |> Array.contains character then '-' else character) |> ignore

        let cleaned = builder.ToString().Trim()

        if String.IsNullOrWhiteSpace cleaned then
            "sky-video"
        else
            cleaned

    let speedLimitBytes () =
        let settings = load ()
        int64 settings.SpeedLimitKbps * 1024L

type RateLimiter(limitBytesPerSecond: int64) =

    let gate = obj ()
    let mutable credits = limitBytesPerSecond
    let mutable lastRefill = DateTimeOffset.UtcNow

    let refill () =
        let now = DateTimeOffset.UtcNow
        let elapsed = (now - lastRefill).TotalSeconds
        let maximum = limitBytesPerSecond

        if elapsed > 0.0 then
            credits <- min maximum (credits + int64 (elapsed * float limitBytesPerSecond))
            lastRefill <- now

    member _.Limit = limitBytesPerSecond

    member this.ConsumeAsync(count: int64, token: CancellationToken) =
        task {
            if limitBytesPerSecond <= 0L || count <= 0L then
                return ()
            else
                let mutable delayMilliseconds = 0

                let settled =
                    lock gate (fun () ->
                        refill ()

                        if credits >= count then
                            credits <- credits - count
                            true
                        else
                            let missing = count - credits
                            delayMilliseconds <- int (float missing / float limitBytesPerSecond * 1000.0) + 10
                            false)

                if not settled then
                    do! Task.Delay(max 5 delayMilliseconds, token)

                    lock gate (fun () ->
                        refill ()
                        credits <- credits - count)

                return ()
        }

module Throttle =

    let none = RateLimiter 0L
