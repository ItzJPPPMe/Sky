namespace Sky

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

module Twitch =

    let private clientId = "kimne78kx3ncx6brgo4mv6wki5h1ko"
    let private gqlEndpoint = "https://gql.twitch.tv/gql"
    let private usherEndpoint = "https://usher.ttvnw.net"

    let private metadataQuery =
        "query VideoMetadata($videoID: ID!) { video(id: $videoID) { id title lengthSeconds publishedAt viewCount owner { displayName login } } }"

    let private tokenQuery =
        "query VideoAccessToken_Playback($videoID: ID!) { videoPlaybackAccessToken(id: $videoID, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: \"embed\"}) { value signature } }"

    let tryParseVideoId (url: string) =
        match Guard.tryUri url with
        | None ->
            None
        | Some uri ->
            let path = uri.AbsolutePath
            let patterns =
                [ "/videos/(\d+)"
                  "/video/(\d+)"
                  "/(\d+)" ]

            let mutable found: string option = None
            let mutable index = 0

            while found.IsNone && index < patterns.Length do
                let matched = Regex.Match(path, patterns.[index])

                if matched.Success && matched.Groups.Count > 1 then
                    found <- Some(Guard.text matched.Groups.[1].Value)
                else
                    index <- index + 1

            found

    let private queryBody (operationName: string) (query: string) (variables: string) =
        String.Concat(
            [|"{"
              String.Concat("\"operationName\":", JsonSerializer.Serialize operationName)
              ","
              String.Concat("\"query\":", JsonSerializer.Serialize query)
              ","
              String.Concat("\"variables\":", variables)
              "}"|]
        )

    let private headers () =
        [ "Client-ID", clientId
          "Content-Type", "application/json" ]

    let private readString (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Guard.text (value.GetString())
        | _ -> ""

    let private readNumber (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetDouble()
        | _ -> 0.0

    let private errorMessage (payload: string) =
        try
            use document = JsonDocument.Parse payload

            match document.RootElement.TryGetProperty "errors" with
            | true, errors when errors.ValueKind = JsonValueKind.Array && errors.GetArrayLength() > 0 ->
                let first = errors.[0]

                match first.TryGetProperty "message" with
                | true, message when message.ValueKind = JsonValueKind.String -> Guard.text (message.GetString())
                | _ -> "Twitch isteği reddedildi."
            | _ ->
                "Twitch isteği reddedildi."
        with _ ->
            "Twitch isteği reddedildi."

    let private fetchMetadata (videoId: string) (token: CancellationToken) =
        task {
            let variables = String.Concat("{\"videoID\":", JsonSerializer.Serialize(videoId), "}")
            let body = queryBody "VideoMetadata" metadataQuery variables
            let! result = Http.postJson gqlEndpoint (headers ()) body token

            match result with
            | Error message -> return Error message
            | Ok payload ->
                if payload.Contains("\"errors\"", StringComparison.Ordinal) then
                    return Error(errorMessage payload)
                else
                    try
                        use document = JsonDocument.Parse payload

                        match document.RootElement.TryGetProperty "data" with
                        | true, data ->
                            match data.TryGetProperty "video" with
                            | true, video when video.ValueKind = JsonValueKind.Object ->
                                let owner =
                                    match video.TryGetProperty "owner" with
                                    | true, value when value.ValueKind = JsonValueKind.Object -> readString value "displayName"
                                    | _ -> ""

                                return
                                    Ok
                                        (readString video "title",
                                         readNumber video "lengthSeconds",
                                         owner)
                            | _ ->
                                return Error("Yayın tekrarı bulunamadı. Video kimliği: " + videoId)
                        | _ ->
                            return Error "Twitch yanıtı okunamadı."
                    with ex ->
                        return Error(ex.Message)
        }

    let private fetchToken (videoId: string) (token: CancellationToken) =
        task {
            let variables = String.Concat("{\"videoID\":", JsonSerializer.Serialize(videoId), "}")
            let body = queryBody "VideoAccessToken_Playback" tokenQuery variables
            let! result = Http.postJson gqlEndpoint (headers ()) body token

            match result with
            | Error message -> return Error message
            | Ok payload ->
                if payload.Contains("\"errors\"", StringComparison.Ordinal) then
                    return Error(errorMessage payload)
                else
                    try
                        use document = JsonDocument.Parse payload

                        match document.RootElement.TryGetProperty "data" with
                        | true, data ->
                            match data.TryGetProperty "videoPlaybackAccessToken" with
                            | true, access when access.ValueKind = JsonValueKind.Object ->
                                let value = readString access "value"
                                let signature = readString access "signature"

                                if String.IsNullOrWhiteSpace value || String.IsNullOrWhiteSpace signature then
                                    return Error "Twitch oynatma belirteci alınamadı."
                                else
                                    return Ok(value, signature)
                            | _ ->
                                return Error "Twitch oynatma belirteci alınamadı."
                        | _ ->
                            return Error "Twitch yanıtı okunamadı."
                    with ex ->
                        return Error(ex.Message)
        }

    let private playbackUrl (signature: string) (value: string) =
        let random = Random().Next(1, 99999)
        let parameters =
            [ "allow_source", "true"
              "allow_audio_only", "true"
              "fast_bread", "true"
              "p", string random
              "reassignments_supported", "true"
              "player_backend", "mediaplayer"
              "sig", signature
              "token", value ]

        let query =
            parameters
            |> List.map (fun (key, item) -> String.Concat(key, "=", Uri.EscapeDataString(item)))
            |> String.concat "&"

        String.Concat(usherEndpoint, "/api/channel/hls/", signature, ".m3u8?", query)

    let private streamTitle (metadataTitle: string) (videoId: string) =
        if String.IsNullOrWhiteSpace metadataTitle then
            String.Concat("twitch-", videoId)
        else
            let cleaned = Regex.Replace(metadataTitle, "[^\\p{L}\\p{Nd} _.-]", "")
            let safe = cleaned.Trim()

            if String.IsNullOrWhiteSpace safe then
                String.Concat("twitch-", videoId)
            else
                safe

    let private playbackLink (value: StreamLink) =
        { value with
            Referer = Some "https://www.twitch.tv/"
            UserAgent = None
            Headers =
                Map.ofList
                    [ "Client-ID", clientId
                      "Origin", "https://www.twitch.tv" ] }

    let resolve (link: StreamLink) (token: CancellationToken) =
        task {
            match tryParseVideoId link.Url with
            | None -> return Error "Twitch yayın tekrarı kimliği bağlantıdan okunamadı."
            | Some videoId ->
                let! metadata = fetchMetadata videoId token

                match metadata with
                | Error message -> return Error message
                | Ok(rawTitle, duration, owner) ->
                    let! access = fetchToken videoId token

                    match access with
                    | Error message -> return Error message
                    | Ok(value, signature) ->
                        let title = streamTitle rawTitle videoId
                        let manifest = playbackUrl signature value

                        return!
                            Playlists.expand
                                (playbackLink link)
                                Platform.Twitch
                                title
                                owner
                                duration
                                false
                                manifest
                                token
        }
