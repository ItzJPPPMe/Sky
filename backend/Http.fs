namespace Sky

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks

module Http =

    let private config = DownloaderConfig.defaultConfig ()
    let private handler = new HttpClientHandler()
    let private client = new HttpClient(handler)

    do
        handler.AllowAutoRedirect <- true
        handler.AutomaticDecompression <- DecompressionMethods.All
        handler.MaxConnectionsPerServer <- config.ConnectionLimit
        client.Timeout <- TimeSpan.FromSeconds(config.RequestTimeoutSeconds)
        client.DefaultRequestHeaders.UserAgent.ParseAdd config.UserAgent

    let private createRequest (method: HttpMethod) (url: string) =
        let request = new HttpRequestMessage(method, url)
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9") |> ignore
        request

    let private applyLink (link: StreamLink option) (request: HttpRequestMessage) =
        match link with
        | Some value ->
            match value.Referer with
            | Some referer when not (String.IsNullOrWhiteSpace referer) ->
                request.Headers.Referrer <- Uri referer
            | _ ->
                ()

            match value.UserAgent with
            | Some agent when not (String.IsNullOrWhiteSpace agent) ->
                request.Headers.Remove("User-Agent") |> ignore
                request.Headers.TryAddWithoutValidation("User-Agent", agent) |> ignore
            | _ ->
                ()

            value.Headers
            |> Map.iter (fun key header ->
                request.Headers.TryAddWithoutValidation(key, header) |> ignore)
        | None ->
            ()

    let private applyHeaders (headers: (string * string) list) (request: HttpRequestMessage) =
        for (key, header) in headers do
            request.Headers.TryAddWithoutValidation(key, header) |> ignore

    let private send (request: HttpRequestMessage) (token: CancellationToken) =
        task {
            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            let! body = response.Content.ReadAsStringAsync(token)

            if response.IsSuccessStatusCode then
                return Ok body
            else
                let preview = if body.Length > 400 then body.Substring(0, 400) else body
                return Error(String.Format("HTTP {0} {1}", int response.StatusCode, preview))
        }

    let getText (url: string) (link: StreamLink option) (token: CancellationToken) =
        task {
            use request = createRequest HttpMethod.Get url
            applyLink link request
            return! send request token
        }

    let getTextWithHeaders (url: string) (headers: (string * string) list) (token: CancellationToken) =
        task {
            use request = createRequest HttpMethod.Get url
            applyHeaders headers request
            return! send request token
        }

    let postJson (url: string) (headers: (string * string) list) (body: string) (token: CancellationToken) =
        task {
            use request = createRequest HttpMethod.Post url
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            applyHeaders headers request
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            return! send request token
        }

    let getBytes (url: string) (headers: (string * string) list) (range: (int64 * int64) option) (token: CancellationToken) =
        task {
            use request = createRequest HttpMethod.Get url
            applyHeaders headers request

            match range with
            | Some(offset, length) ->
                request.Headers.TryAddWithoutValidation(
                    "Range",
                    String.Format("bytes={0}-{1}", offset, offset + length - 1L)
                )
                |> ignore
            | None ->
                ()

            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)

            if response.IsSuccessStatusCode then
                use! stream = response.Content.ReadAsStreamAsync(token)
                use memory = new MemoryStream()
                do! stream.CopyToAsync(memory, 81920, token)
                return Ok(memory.ToArray()
                )
            else
                return Error(String.Format("HTTP {0}", int response.StatusCode))
        }

    let headLength (url: string) (headers: (string * string) list) (token: CancellationToken) =
        task {
            use request = createRequest HttpMethod.Head url
            applyHeaders headers request

            try
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                let contentLength = response.Content.Headers.ContentLength

                if contentLength.HasValue then
                    return contentLength.Value
                else
                    return 0L
            with _ ->
                return 0L
        }

    let probeSize (url: string) (headers: (string * string) list) (token: CancellationToken) =
        task {
            try
                use request = createRequest HttpMethod.Get url
                applyHeaders headers request
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-0") |> ignore

                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)

                match (box response.Content.Headers.ContentRange) with
                | (:? ContentRangeHeaderValue as header) when header.Length.HasValue ->
                    return header.Length.Value
                | _ ->
                    let contentLength = response.Content.Headers.ContentLength

                    if contentLength.HasValue then
                        return contentLength.Value
                    else
                        return 0L
            with _ ->
                return 0L
        }
