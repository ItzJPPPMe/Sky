namespace Sky

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

module Kick =

    let private graphqlEndpoint = "https://api.kick.com/graphql"
    let private browserHeaders =
        [ "Accept", "application/json, text/plain, */*"
          "Accept-Language", "en-US,en;q=0.9"
          "Origin", "https://kick.com"
          "Referer", "https://kick.com/" ]

    let private videoQuery =
        "query playback_access_token($slug: ID!) { video(id: $slug) { id playback_url video_status livestream { id slug } __typename } }"

    let private livestreamQuery =
        "query playback_access_token($slug: ID!) { livestream(id: $slug) { id playback_url video_status __typename } }"

    let tryParseVideoSlug (url: string) =
        match Guard.tryUri url with
        | None ->
            None
        | Some uri ->
            let path = uri.AbsolutePath.Trim('/')
            let parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            let uuidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"

            let mutable result: string option = None
            let mutable index = 0

            while result.IsNone && index < parts.Length do
                let part = parts.[index]

                if Regex.IsMatch(part, uuidPattern) then
                    result <- Some(Guard.text part)
                elif index > 0 && parts.[index - 1] = "videos" && Regex.IsMatch(part, "^[0-9a-fA-F]{16,}$") then
                    result <- Some(Guard.text part)

                index <- index + 1

            result

    let tryParseChannelSlug (url: string) =
        match Guard.tryUri url with
        | None ->
            None
        | Some uri ->
            let parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)

            match parts with
            | [| channel |] when not (String.IsNullOrWhiteSpace channel) -> Some channel
            | _ -> None

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

    let private readString (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Guard.text (value.GetString())
        | _ -> ""

    let private errorMessage (payload: string) =
        try
            use document = JsonDocument.Parse payload

            match document.RootElement.TryGetProperty "errors" with
            | true, errors when errors.ValueKind = JsonValueKind.Array && errors.GetArrayLength() > 0 ->
                let first = errors.[0]

                match first.TryGetProperty "message" with
                | true, message when message.ValueKind = JsonValueKind.String -> Guard.text (message.GetString())
                | _ -> "Kick isteği reddedildi."
            | _ ->
                "Kick isteği reddedildi."
        with _ ->
            "Kick isteği reddedildi."

    let private extractPlaybackUrl (payload: string) =
        let matched = Regex.Match(payload, "\\\"playback_url\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"")
        let value = if matched.Success then matched.Groups.[1].Value else ""

        match value.Replace("\\/", "/").Replace("\\u0026", "&") with
        | result when not (String.IsNullOrWhiteSpace result) -> Some result
        | _ -> None

    let private fromGraphql (payload: string) =
        if payload.Contains("\"errors\"", StringComparison.Ordinal) then
            Error(errorMessage payload)
        else
            try
                use document = JsonDocument.Parse payload
                let mutable node = Unchecked.defaultof<JsonElement>

                match document.RootElement.TryGetProperty "data" with
                | true, data when data.ValueKind = JsonValueKind.Object ->
                    let mutable found = false

                    for property in data.EnumerateObject() do
                        if not found && property.Value.ValueKind = JsonValueKind.Object then
                            node <- property.Value
                            found <- true

                    if found then
                        match readString node "playback_url" with
                        | url when not (String.IsNullOrWhiteSpace url) -> Ok url
                        | _ -> Error "Kick oynatma adresi bulunamadı."
                    else
                        Error "Kick yanıtı okunamadı."
                | _ ->
                    Error "Kick yanıtı okunamadı."
            with ex ->
                Error ex.Message

    let private fetchViaGraphql (query: string) (slug: string) (token: CancellationToken) =
        task {
            let variables = String.Concat("{\"slug\":", JsonSerializer.Serialize(slug), "}")
            let body = queryBody "playback_access_token" query variables
            let! result = Http.postJson graphqlEndpoint browserHeaders body token

            match result with
            | Error message -> return Error message
            | Ok payload ->
                return
                    match fromGraphql payload with
                    | Error message ->
                        match extractPlaybackUrl payload with
                        | Some url -> Ok url
                        | None -> Error message
                    | Ok url -> Ok url
        }

    let private fetchViaPage (slug: string) (token: CancellationToken) =
        task {
            let! page = Http.getTextWithHeaders (String.Concat("https://kick.com/videos/", slug)) browserHeaders token

            match page with
            | Error message -> return Error message
            | Ok content ->
                return
                    match extractPlaybackUrl content with
                    | Some url -> Ok url
                    | None -> Error "Kick sayfasından oynatma adresi alınamadı."
        }

    let resolveVideo (link: StreamLink) (slug: string) (token: CancellationToken) =
        task {
            let! primary = fetchViaGraphql videoQuery slug token

            let playback =
                match primary with
                | Ok url -> Task.FromResult(Ok url)
                | Error _ -> fetchViaPage slug token

            let! resolved = playback

            match resolved with
            | Error message -> return Error message
            | Ok url ->
                return!
                    Playlists.expand
                        link
                        Platform.Kick
                        (Playlists.titleFromUrl link.Url)
                        "Kick"
                        0.0
                        false
                        url
                        token
        }

    let resolveLivestream (link: StreamLink) (slug: string) (token: CancellationToken) =
        task {
            let! playback = fetchViaGraphql livestreamQuery slug token

            match playback with
            | Error message -> return Error message
            | Ok url ->
                return!
                    Playlists.expand
                        link
                        Platform.Kick
                        slug
                        slug
                        0.0
                        true
                        url
                        token
        }

    let resolve (link: StreamLink) (token: CancellationToken) =
        task {
            match tryParseVideoSlug link.Url with
            | Some slug -> return! resolveVideo link slug token
            | None ->
                match tryParseChannelSlug link.Url with
                | Some slug -> return! resolveLivestream link slug token
                | None -> return Error "Kick video veya kanal adresi çözümlenemedi."
        }

