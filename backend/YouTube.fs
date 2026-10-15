namespace Sky

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module YouTube =

    type private AudioCandidate =
        { Candidate: QualityOption
          Language: string
          LanguageName: string
          IsOriginal: bool
          Preference: double }

    let private config = DownloaderConfig.defaultConfig ()

    let private errorMessage (payload: string) =
        let lines =
            payload
                .Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun line -> line.Trim())
                |> Array.filter (fun line -> line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                |> Array.truncate 3

        match lines with
        | [||] -> payload.Trim()
        | values -> String.concat " " values

    let private argumentsFor (url: string) =
        let arguments =
            [ "-J"
              "--no-warnings"
              "--no-playlist"
              "--no-progress"
              url ]

        if String.IsNullOrWhiteSpace config.CookieBrowser then
            arguments
        else
            arguments @ [ "--cookies-from-browser"; config.CookieBrowser ]

    let private readString (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Guard.text (value.GetString())
        | _ -> ""

    let private readDouble (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            try
                value.GetDouble()
            with _ ->
                0.0
        | _ ->
            0.0

    let private readInt (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            try
                value.GetInt32()
            with _ ->
                0
        | _ ->
            0

    let private frameRateOf (element: JsonElement) =
        let raw = readString element "fps"

        match Double.TryParse(raw.Replace(',', '.'), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, value -> value
        | _ -> 0.0

    let private tryFormat (element: JsonElement) =
        let url = readString element "url"

        if String.IsNullOrWhiteSpace url then
            None
        else
            let formatId = readString element "format_id"
            let height = readInt element "height"
            let width = readInt element "width"
            let frameRate = frameRateOf element
            let bitrate = readDouble element "tbr"
            let container = readString element "ext"
            let protocol = readString element "protocol"
            let videoCodec = readString element "vcodec"
            let audioCodec = readString element "acodec"

            if String.IsNullOrWhiteSpace formatId then
                None
            elif protocol = "m3u8_native" && height > 0 && videoCodec <> "none" then
                Some
                    { Id = formatId
                      Label = QualityOption.heightLabel height
                      Width = width
                      Height = height
                      FrameRate = frameRate
                      BitrateKbps = int bitrate
                      Container = if String.IsNullOrWhiteSpace container then "ts" else container
                      IsAudio = false
                      Kind = "hls"
                      AudioUrl = ""
                      ManifestUrl = url
                      FormatId = formatId
                      IsLive = false }
            elif (protocol = "https" || protocol = "http")
                 && videoCodec <> "none"
                 && audioCodec <> "none" then
                Some
                    { Id = formatId
                      Label = String.Concat(QualityOption.heightLabel height, " (mp4)")
                      Width = width
                      Height = height
                      FrameRate = frameRate
                      BitrateKbps = int bitrate
                      Container = "mp4"
                      IsAudio = false
                      Kind = "direct"
                      AudioUrl = ""
                      ManifestUrl = url
                      FormatId = formatId
                      IsLive = false }
            else
                None

    let private tryAudio (element: JsonElement) =
        let url = readString element "url"
        let container = readString element "ext"
        let protocol = readString element "protocol"
        let audioCodec = readString element "acodec"
        let videoCodec = readString element "vcodec"
        let bitrate = readDouble element "abr"
        let formatId = readString element "format_id"
        let isDynamicRange = formatId.EndsWith("-drc", StringComparison.OrdinalIgnoreCase)

        if String.IsNullOrWhiteSpace url
           || String.IsNullOrWhiteSpace formatId
           || (protocol <> "https" && protocol <> "http")
           || audioCodec = "none"
           || audioCodec = ""
           || videoCodec <> "none"
           || String.IsNullOrWhiteSpace container
           || isDynamicRange then
            None
        else
            Some
                { Id = String.Concat("audio-", formatId)
                  Label = String.Concat("ses ", formatId)
                  Width = 0
                  Height = 0
                  FrameRate = 0.0
                  BitrateKbps = int bitrate
                  Container = container
                  IsAudio = true
                  Kind = "direct"
                  AudioUrl = ""
                  ManifestUrl = url
                  FormatId = formatId
                  IsLive = false }

    let private readLanguage (element: JsonElement) =
        readString element "language"

    let private readLanguagePreference (element: JsonElement) =
        match element.TryGetProperty "language_preference" with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            try
                value.GetDouble()
            with _ ->
                -1.0
        | _ ->
            -1.0

    let private audioCandidates (formats: JsonElement) (originalLanguage: string) =
        [ for index in 0 .. formats.GetArrayLength() - 1 do
              let element = formats.[index]
              match tryAudio element with
              | Some candidate ->
                  let language = readLanguage element
                  let preference = readLanguagePreference element
                  let isOriginal =
                      (not (String.IsNullOrWhiteSpace originalLanguage))
                      && String.Equals(language, originalLanguage, StringComparison.OrdinalIgnoreCase)

                  yield
                      { Candidate = candidate
                        Language = language
                        LanguageName = Language.displayName language
                        IsOriginal = isOriginal || (String.IsNullOrWhiteSpace language)
                        Preference = preference }
              | None -> () ]

    let private isPreferredContainer (container: string) =
        container = "m4a" || container = "mp4"

    let private audioSortKey (candidate: AudioCandidate) =
        let containerBonus =
            if isPreferredContainer candidate.Candidate.Container then
                1
            else
                0

        containerBonus, candidate.Candidate.BitrateKbps

    let private bestPerLanguage (candidates: AudioCandidate list) =
        candidates
        |> List.groupBy (fun candidate -> candidate.Language)
        |> List.collect (fun (language, items) ->
            match items |> List.sortByDescending audioSortKey |> List.tryHead with
            | Some best -> [ best ]
            | None -> [])

    let private buildAudioTracks (preferredLanguage: string) (candidates: AudioCandidate list) =
        let tracks =
            candidates
            |> bestPerLanguage
            |> List.filter (fun candidate -> not (String.IsNullOrWhiteSpace candidate.Language))
            |> List.map (fun candidate ->
                let track: AudioTrack =
                    { Id = candidate.Candidate.FormatId
                      Label = candidate.LanguageName
                      Language = candidate.LanguageName
                      LanguageCode = candidate.Language
                      IsOriginal = candidate.IsOriginal
                      IsRecommended = false
                      BitrateKbps = candidate.Candidate.BitrateKbps
                      Container = candidate.Candidate.Container
                      Url = candidate.Candidate.ManifestUrl }

                track)

        let safe = Guard.trimmed preferredLanguage

        let preferred =
            tracks
            |> List.tryFind (fun track ->
                String.Equals(track.LanguageCode, safe, StringComparison.OrdinalIgnoreCase)
                || String.Equals(track.LanguageCode.Split('-').[0], safe, StringComparison.OrdinalIgnoreCase))

        let chosen =
            match preferred with
            | Some track -> Some track
            | None ->
                match tracks |> List.tryFind (fun track -> track.IsOriginal) with
                | Some track -> Some track
                | None -> List.tryHead tracks

        let recommendedId =
            chosen
            |> Option.map (fun track -> track.Id)
            |> Option.defaultValue ""

        let marked =
            tracks
            |> List.map (fun track ->
                { track with IsRecommended = String.Equals(track.Id, recommendedId, StringComparison.Ordinal) })

        marked, recommendedId

    let private parsePayload (payload: string) =
        try
            use document = JsonDocument.Parse payload
            let root = document.RootElement
            let title = readString root "title"
            let uploader =
                match readString root "channel" with
                | value when not (String.IsNullOrWhiteSpace value) -> value
                | _ -> readString root "uploader"

            let duration = readDouble root "duration"
            let isLive =
                match root.TryGetProperty "is_live" with
                | true, value when value.ValueKind = JsonValueKind.True -> true
                | _ -> false

            let formatsOrEmpty =
                match root.TryGetProperty "formats" with
                | true, formats when formats.ValueKind = JsonValueKind.Array -> formats
                | _ -> Unchecked.defaultof<JsonElement>

            let originalLanguage = readString root "language"

            let qualities =
                if formatsOrEmpty.ValueKind = JsonValueKind.Array then
                    [ for index in 0 .. formatsOrEmpty.GetArrayLength() - 1 do
                          match tryFormat formatsOrEmpty.[index] with
                          | Some quality -> yield quality
                          | None -> () ]
                else
                    []

            let audioTracks, recommendedId =
                if formatsOrEmpty.ValueKind = JsonValueKind.Array then
                    buildAudioTracks config.PreferredLanguage (audioCandidates formatsOrEmpty originalLanguage)
                else
                    [], ""

            let audioUrl =
                audioTracks
                |> List.tryFind (fun track -> String.Equals(track.Id, recommendedId, StringComparison.Ordinal))
                |> Option.map (fun track -> track.Url)
                |> Option.defaultValue ""

            let distinct =
                qualities
                |> List.sortByDescending (fun quality ->
                    (if quality.Kind = "hls" then 1 else 0),
                    quality.Height,
                    quality.BitrateKbps)
                |> List.fold (fun accumulated quality ->
                    match List.tryFind (fun item -> item.Height = quality.Height && item.Kind = quality.Kind) accumulated with
                    | Some _ -> accumulated
                    | None -> accumulated @ [ quality ]) []

            let withAudio =
                if String.IsNullOrWhiteSpace audioUrl then
                    distinct
                else
                    distinct
                    |> List.map (fun quality ->
                        if quality.Kind = "hls" then
                            { quality with AudioUrl = audioUrl }
                        else
                            quality)

            if List.isEmpty withAudio then
                Error "YouTube için uygun format bulunamadı. yt-dlp sürümünü güncelleyin."
            else
                Ok
                    { Resolution.empty (Platform.label Platform.YouTube) (if String.IsNullOrWhiteSpace title then "youtube" else title) with
                        DurationSeconds = duration
                        Uploader = uploader
                        IsLive = isLive
                        Qualities = withAudio
                        AudioTracks = audioTracks
                        RecommendedAudioTrackId = recommendedId }
        with ex ->
            Error(String.Format("YouTube verisi okunamadı: {0}", ex.Message))

    let private videoIdOf (url: string) =
        match Guard.tryUri url with
        | None ->
            ""
        | Some uri ->
            let host = uri.Host.ToLowerInvariant()
            let path = uri.AbsolutePath

            if host.EndsWith("youtu.be", StringComparison.Ordinal) then
                path.Trim('/')
            else
                let fromQuery =
                    match System.Web.HttpUtility.ParseQueryString(uri.Query)["v"] with
                    | null -> ""
                    | value -> Guard.trimmed value

                if fromQuery.Length > 0 then
                    fromQuery
                else
                    let segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)

                    match segments with
                    | [| "shorts"; id |] -> id
                    | [| "embed"; id |] -> id
                    | [| "live"; id |] -> id
                    | [| "v"; id |] -> id
                    | _ -> ""

    let private thumbnailFor (videoId: string) =
        if String.IsNullOrWhiteSpace videoId then
            ""
        else
            String.Concat("https://i.ytimg.com/vi/", videoId, "/hqdefault.jpg")

    let toolStatus () =
        task {
            let! statuses = Tools.toolStatuses()
            return statuses |> List.tryFind (fun status -> status.Name = "yt-dlp")
        }

    let resolve (link: StreamLink) (token: CancellationToken) =
        task {
            let! tool = Tools.ytdlp()

            match tool with
            | None ->
                return Error(Tools.toolRequiredMessage "yt-dlp")
            | Some(invocation, _) ->
                let! output = Tools.capture invocation (argumentsFor link.Url) 120000

                match output with
                | Error message -> return Error(errorMessage message)
                | Ok payload ->
                    if not (payload.TrimStart().StartsWith("{", StringComparison.Ordinal)) then
                        return Error(errorMessage payload)
                    else
                        let parsed = parsePayload payload

                        match parsed with
                        | Error message ->
                            return Error message
                        | Ok resolution ->
                            let thumbnail = thumbnailFor (videoIdOf link.Url)

                            return Ok { resolution with ThumbnailUrl = thumbnail }
        }
