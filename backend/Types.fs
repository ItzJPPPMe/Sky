namespace Sky

open System
open System.Globalization
open System.IO

[<RequireQualifiedAccess>]
module internal Guard =

    let text (value: string | null) =
        match value with
        | null -> String.Empty
        | "" -> String.Empty
        | safe -> safe

    let trimmed (value: string | null) =
        text value |> fun safe -> safe.Trim()

    let tryUri (value: string) =
        let parsed = ref (Unchecked.defaultof<Uri>)
        let created = Uri.TryCreate(text value, UriKind.Absolute, parsed)

        if created && not (isNull (box parsed.Value)) then
            Some parsed.Value
        else
            None

    let isHttp (uri: Uri) =
        uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps

[<RequireQualifiedAccess>]
type Platform =
    | YouTube
    | Twitch
    | Kick
    | Hls
    | Dash
    | Unknown

[<RequireQualifiedAccess>]
type SegmentState =
    | Pending
    | Downloading
    | Completed
    | Failed

type StreamLink =
    { Url: string
      Title: string option
      Referer: string option
      UserAgent: string option
      Headers: Map<string, string> }

type QualityOption =
    { Id: string
      Label: string
      Width: int
      Height: int
      FrameRate: float
      BitrateKbps: int
      Container: string
      IsAudio: bool
      Kind: string
      AudioUrl: string
      ManifestUrl: string
      FormatId: string
      IsLive: bool }

type ResolveRequest =
    { Url: string
      PreferredQualityId: string }

type AudioTrack =
    { Id: string
      Label: string
      Language: string
      LanguageCode: string
      IsOriginal: bool
      IsRecommended: bool
      BitrateKbps: int
      Container: string
      Url: string }

type Resolution =
    { SessionId: string
      Platform: string
      Title: string
      DurationSeconds: double
      Uploader: string
      ThumbnailUrl: string
      IsLive: bool
      Qualities: QualityOption list
      AudioTracks: AudioTrack list
      RecommendedAudioTrackId: string
      ResolvedAtUtc: DateTimeOffset
      ExpiresAtUtc: DateTimeOffset }

type DownloadRequest =
    { Url: string
      Platform: string
      SessionId: string
      AudioTrackId: string
      QualityId: string
      OutputDirectory: string
      FileName: string
      SegmentConcurrency: int }

type DownloadStatus =
    { Id: string
      Url: string
      Title: string
      Uploader: string
      Platform: string
      QualityId: string
      QualityLabel: string
      State: string
      IsLive: bool
      FilePath: string
      TotalSegments: int
      CompletedSegments: int
      ProgressPercent: double
      BytesDownloaded: int64
      TotalBytes: int64
      BytesPerSecond: double
      EtaSeconds: double
      ErrorMessage: string
      Warning: string
      CreatedAtUtc: DateTimeOffset
      UpdatedAtUtc: DateTimeOffset }

type HistoryEntry =
    { Id: string
      Url: string
      Title: string
      Uploader: string
      Platform: string
      QualityLabel: string
      FilePath: string
      SizeBytes: int64
      DurationSeconds: double
      CompletedAtUtc: DateTimeOffset }

type ProgressEvent =
    { Kind: string
      Status: DownloadStatus
      Entry: HistoryEntry option }

type ToolStatus =
    { Name: string
      Available: bool
      Path: string
      Version: string }

type PlatformInfo =
    { Id: string
      Name: string
      RequiresYtDlp: bool }

type DownloaderConfig =
    { Port: int
      Host: string
      OutputDirectory: string
      SegmentConcurrency: int
      RequestTimeoutSeconds: int
      RequestRetries: int
      SegmentRetries: int
      SegmentRetryDelayMs: int
      ParallelSegmentCeiling: int
      LivePollDivisor: int
      ResolveSessionSeconds: int
      UserAgent: string
      ConnectionLimit: int
      YtDlpPath: string
      FfMpegPath: string
      CookieBrowser: string
      PreferredLanguage: string }

module Platform =

    let label platform =
        match platform with
        | Platform.YouTube -> "YouTube"
        | Platform.Twitch -> "Twitch"
        | Platform.Kick -> "Kick"
        | Platform.Hls -> "HLS"
        | Platform.Dash -> "DASH"
        | Platform.Unknown -> "Bilinmiyor"

    let ofString (value: string) =
        match Guard.trimmed value |> fun safe -> safe.ToLowerInvariant() with
        | "youtube" -> Platform.YouTube
        | "twitch" -> Platform.Twitch
        | "kick" -> Platform.Kick
        | "hls" -> Platform.Hls
        | "dash" -> Platform.Dash
        | _ -> Platform.Unknown

    let ofUrl (url: string) =
        let safe = Guard.trimmed url

        if String.IsNullOrWhiteSpace safe then
            Platform.Unknown
        else
            let lower = safe.ToLowerInvariant()

            let host =
                match Guard.tryUri safe with
                | Some uri -> uri.Host.ToLowerInvariant()
                | None -> ""

            let hostWithoutPrefix =
                if host.StartsWith("www.", StringComparison.Ordinal) then
                    host.Substring(4)
                else if host.StartsWith("m.", StringComparison.Ordinal) then
                    host.Substring(2)
                else
                    host

            let path =
                match Guard.tryUri safe with
                | Some uri -> uri.AbsolutePath.ToLowerInvariant()
                | None -> ""

            if hostWithoutPrefix.EndsWith("youtube.com", StringComparison.Ordinal)
               || hostWithoutPrefix = "youtu.be"
               || hostWithoutPrefix.EndsWith("youtube-nocookie.com", StringComparison.Ordinal) then
                Platform.YouTube
            elif hostWithoutPrefix.EndsWith("twitch.tv", StringComparison.Ordinal) then
                Platform.Twitch
            elif hostWithoutPrefix = "kick.com" || hostWithoutPrefix.EndsWith(".kick.com", StringComparison.Ordinal) then
                Platform.Kick
            elif lower.EndsWith(".m3u8", StringComparison.Ordinal)
                 || lower.EndsWith(".m3u", StringComparison.Ordinal)
                 || lower.Contains("/manifest.m3u8")
                 || lower.Contains("index.m3u8") then
                Platform.Hls
            elif lower.EndsWith(".mpd", StringComparison.Ordinal) || lower.Contains("/manifest.mpd") then
                Platform.Dash
            elif path.Contains("/hls") || path.Contains("/playlist") then
                Platform.Hls
            else
                Platform.Unknown

    let all =
        [ Platform.YouTube
          Platform.Twitch
          Platform.Kick
          Platform.Hls
          Platform.Dash ]

module DownloadState =

    let idle = "idle"
    let resolving = "resolving"
    let queued = "queued"
    let downloading = "downloading"
    let paused = "paused"
    let merging = "merging"
    let muxing = "muxing"
    let completed = "completed"
    let failed = "failed"
    let cancelled = "cancelled"

    let all =
        [ idle
          resolving
          queued
          downloading
          paused
          merging
          muxing
          completed
          failed
          cancelled ]

    let isActive state =
        List.contains state [ resolving; queued; downloading; merging; muxing ]

    let isFinished state =
        List.contains state [ completed; failed; cancelled ]

    let ofString (value: string) =
        match all |> List.tryFind (fun state -> state = value) with
        | Some state -> state
        | None -> idle

module ProgressEventKind =

    let status = "status"
    let progress = "progress"
    let finished = "finished"
    let history = "history"
    let log = "log"

module StreamLink =

    let create (url: string) =
        { Url = Guard.trimmed url
          Title = None
          Referer = None
          UserAgent = None
          Headers = Map.empty }

    let withTitle (title: string) (link: StreamLink) =
        { link with Title = if String.IsNullOrWhiteSpace title then None else Some title }

module QualityOption =

    let unknownQuality =
        { Id = "source"
          Label = "Kaynak"
          Width = 0
          Height = 0
          FrameRate = 0.0
          BitrateKbps = 0
          Container = "ts"
          IsAudio = false
          Kind = "hls"
          AudioUrl = ""
          ManifestUrl = ""
          FormatId = ""
          IsLive = false }

    let heightLabel height =
        match height with
        | value when value >= 2160 -> "2160p"
        | value when value >= 1440 -> "1440p"
        | value when value >= 1080 -> "1080p"
        | value when value >= 720 -> "720p"
        | value when value >= 480 -> "480p"
        | value when value >= 360 -> "360p"
        | value when value >= 240 -> "240p"
        | value when value > 0 -> "source"
        | _ -> "ses"

    let frameRateLabel frameRate =
        if frameRate >= 50.0 then "60fps"
        elif frameRate >= 25.0 then "30fps"
        elif frameRate > 0.0 then String.Format("{0}fps", int frameRate)
        else ""

    let describe quality =
        let parts = ResizeArray<string>()

        if quality.Width > 0 && quality.Height > 0 then
            parts.Add(String.Format("{0}x{1}", quality.Width, quality.Height))

        if quality.FrameRate > 0.0 then
            parts.Add(frameRateLabel quality.FrameRate)

        if quality.BitrateKbps > 0 then
            parts.Add(String.Format("{0} kbps", quality.BitrateKbps))

        if quality.IsAudio then
            parts.Add("yalnızca ses")

        match parts.Count with
        | 0 -> quality.Label
        | _ -> String.Join(" · ", parts)

    let fallback =
        unknownQuality

module Language =

    let names =
        [ "tr", "Türkçe"
          "en", "İngilizce"
          "en-US", "İngilizce (ABD)"
          "en-GB", "İngilizce (Birleşik Krallık)"
          "de", "Almanca"
          "fr", "Fransızca"
          "es", "İspanyolca"
          "pt", "Portekizce"
          "pt-BR", "Portekizce (Brezilya)"
          "it", "İtalyanca"
          "nl", "Felemenkçe"
          "ru", "Rusça"
          "uk", "Ukraynaca"
          "pl", "Lehçe"
          "cs", "Çekçe"
          "ro", "Rumence"
          "hu", "Macarca"
          "el", "Yunanca"
          "ar", "Arapça"
          "he", "İbranice"
          "fa", "Farsça"
          "hi", "Hintçe"
          "ur", "Urduca"
          "bn", "Bengalce"
          "ta", "Tamilce"
          "te", "Teluguca"
          "mr", "Marathi"
          "pa", "Pencapca"
          "gu", "Gujarati"
          "kn", "Kannada"
          "ml", "Malayalamca"
          "id", "Endonezce"
          "ms", "Malayca"
          "th", "Tayca"
          "vi", "Vietnamca"
          "zh-Hans", "Çince (Basitleştirilmiş)"
          "zh-Hant", "Çince (Geleneksel)"
          "ja", "Japonca"
          "ko", "Korece"
          "af", "Afrikaans"
          "sw", "Svahili"
          "da", "Danca"
          "sv", "İsveççe"
          "no", "Norveççe"
          "fi", "Fince"
          "bg", "Bulgarca"
          "sr", "Sırpça"
          "hr", "Hırvatça"
          "sk", "Slovakça"
          "sl", "Slovence"
          "lt", "Litvanca"
          "lv", "Latince"
          "et", "Estonca"
          "tr-TR", "Türkçe (Türkiye)" ]

    let displayName (code: string) =
        let safe = Guard.trimmed code

        if String.IsNullOrWhiteSpace safe then
            "Bilinmeyen dil"
        else
            match names |> List.tryFind (fun (key, _) -> String.Equals(key, safe, StringComparison.OrdinalIgnoreCase)) with
            | Some(_, label) -> label
            | None ->
                let baseCode = safe.Split('-').[0]

                match names |> List.tryFind (fun (key, _) -> String.Equals(key, baseCode, StringComparison.OrdinalIgnoreCase)) with
                | Some(_, label) -> String.Concat(label, " (", safe, ")")
                | None -> safe.ToUpperInvariant()

module Resolution =

    let empty platform title =
        { SessionId = ""
          Platform = platform
          Title = title
          DurationSeconds = 0.0
          Uploader = ""
          ThumbnailUrl = ""
          IsLive = false
          Qualities = []
          AudioTracks = []
          RecommendedAudioTrackId = ""
          ResolvedAtUtc = DateTimeOffset.UtcNow
          ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30.0) }

module DownloadStatus =

    let empty id url =
        { Id = id
          Url = url
          Title = ""
          Uploader = ""
          Platform = Platform.label Platform.Unknown
          QualityId = "source"
          QualityLabel = "Kaynak"
          State = DownloadState.idle
          IsLive = false
          FilePath = ""
          TotalSegments = 0
          CompletedSegments = 0
          ProgressPercent = 0.0
          BytesDownloaded = 0L
          TotalBytes = 0L
          BytesPerSecond = 0.0
          EtaSeconds = 0.0
          ErrorMessage = ""
          Warning = ""
          CreatedAtUtc = DateTimeOffset.UtcNow
          UpdatedAtUtc = DateTimeOffset.UtcNow }

    let updateProgress status percent bytes total speed eta =
        { status with
            ProgressPercent = max 0.0 (min 100.0 percent)
            BytesDownloaded = bytes
            TotalBytes = total
            BytesPerSecond = speed
            EtaSeconds = eta }

module DownloadRequest =

    let create (url: string) (qualityId: string) (outputDirectory: string) (fileName: string) =
        { Url = Guard.trimmed url
          Platform = ""
          SessionId = ""
          AudioTrackId = ""
          QualityId = if String.IsNullOrWhiteSpace qualityId then "source" else qualityId
          OutputDirectory = if String.IsNullOrWhiteSpace outputDirectory then "" else outputDirectory
          FileName = if String.IsNullOrWhiteSpace fileName then "sky-video" else fileName
          SegmentConcurrency = 8 }

    let validate (request: DownloadRequest) =
        let errors = ResizeArray<string>()

        if String.IsNullOrWhiteSpace request.Url then
            errors.Add "Yayın linki boş olamaz."
        else
            match Guard.tryUri request.Url with
            | None -> errors.Add "Yayın linki geçerli bir adres değil."
            | Some uri ->
                if not (Guard.isHttp uri) then
                    errors.Add "Yayın linki http veya https ile başlamalıdır."

        if request.SegmentConcurrency < 1 || request.SegmentConcurrency > 64 then
            errors.Add "Eşzamanlılık değeri 1 ile 64 arasında olmalıdır."

        List.ofSeq errors

module ResolveRequest =

    let create (url: string) =
        { Url = Guard.trimmed url
          PreferredQualityId = "" }

module DownloaderConfig =

    let private readInt name fallback =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" ->
            fallback
        | raw ->
            match Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, parsed -> parsed
            | _ -> fallback

    let private readText name fallback =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> fallback
        | raw -> raw

    let createFromEnvironment () =
        let defaultDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Sky")

        { Port = readInt "SKY_PORT" 47210
          Host = readText "SKY_HOST" "127.0.0.1"
          OutputDirectory = readText "SKY_OUTPUT_DIRECTORY" defaultDirectory
          SegmentConcurrency = readInt "SKY_SEGMENT_CONCURRENCY" 8
          RequestTimeoutSeconds = readInt "SKY_REQUEST_TIMEOUT" 30
          RequestRetries = readInt "SKY_REQUEST_RETRIES" 3
          SegmentRetries = readInt "SKY_SEGMENT_RETRIES" 3
          SegmentRetryDelayMs = readInt "SKY_SEGMENT_RETRY_DELAY" 750
          ParallelSegmentCeiling = readInt "SKY_SEGMENT_CEILING" 16
          LivePollDivisor = readInt "SKY_LIVE_POLL_DIVISOR" 2
          ResolveSessionSeconds = readInt "SKY_RESOLVE_SESSION_SECONDS" 1800
          UserAgent =
            readText
                "SKY_USER_AGENT"
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
          ConnectionLimit = readInt "SKY_CONNECTION_LIMIT" 24
          YtDlpPath = readText "SKY_YTDLP_PATH" ""
          FfMpegPath = readText "SKY_FFMPEG_PATH" ""
          CookieBrowser = readText "SKY_COOKIES_FROM_BROWSER" ""
          PreferredLanguage = readText "SKY_PREFERRED_LANGUAGE" "tr" }

    let defaultConfig () = createFromEnvironment ()

module Segment =

    let fileName index (isInitialization: bool) =
        let suffix = if isInitialization then ".init" else ""
        String.Format("{0:D8}{1}", index, suffix)

module Formatter =

    let byteSize (bytes: int64) =
        let units = [| "B"; "KB"; "MB"; "GB"; "TB" |]
        let mutable size = float bytes
        let mutable index = 0

        while size >= 1024.0 && index < units.Length - 1 do
            size <- size / 1024.0
            index <- index + 1

        if index = 0 then String.Format("{0} {1}", int size, units[index])
        else String.Format("{0:N1} {1}", size, units[index])

    let speed (bytesPerSecond: double) =
        if bytesPerSecond <= 0.0 then "0 B/sn"
        else String.Format("{0}/sn", byteSize (int64 bytesPerSecond))

    let duration (seconds: double) =
        if seconds <= 0.0 then
            "--:--"
        else
            let span = TimeSpan.FromSeconds seconds

            if span.TotalHours >= 1.0 then
                String.Format("{0:00}:{1:00}:{2:00}", int span.TotalHours, span.Minutes, span.Seconds)
            else
                String.Format("{0:00}:{1:00}", span.Minutes, span.Seconds)

    let percent (value: double) =
        String.Format("{0:N1}%", value)
