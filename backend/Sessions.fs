namespace Sky

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

module Sessions =

    let private storage = new ConcurrentDictionary<string, Resolution>()
    let private semaphore = new SemaphoreSlim(1, 1)

    let save (resolution: Resolution) =
        let seconds = float (DownloaderConfig.defaultConfig()).ResolveSessionSeconds

        let stored =
            { resolution with
                SessionId = Guid.NewGuid().ToString("N")
                ResolvedAtUtc = DateTimeOffset.UtcNow
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds) }

        storage.[stored.SessionId] <- stored
        stored

    let tryGet id =
        let safe = Guard.trimmed id

        if String.IsNullOrWhiteSpace safe then
            None
        else
            match storage.TryGetValue safe with
            | true, resolution when resolution.ExpiresAtUtc > DateTimeOffset.UtcNow -> Some resolution
            | true, _ ->
                storage.TryRemove safe |> ignore
                None
            | _ ->
                None

    let purge () =
        task {
            let! entered = semaphore.WaitAsync(0)

            if entered then
                try
                    let now = DateTimeOffset.UtcNow
                    let expired = ResizeArray<string>()

                    for KeyValue(key, resolution) in storage do
                        if resolution.ExpiresAtUtc <= now then
                            expired.Add key

                    for key in expired do
                        storage.TryRemove key |> ignore
                finally
                    semaphore.Release() |> ignore

            return ()
        }

    let count () = storage.Count

module Playlists =

    let private platformLabel (platform: Platform) =
        Platform.label platform

    let private isFragmented (content: string) =
        content.Contains(".m4s", StringComparison.OrdinalIgnoreCase)
        || content.Contains(".mp4", StringComparison.OrdinalIgnoreCase)

    let expand
        (link: StreamLink)
        (platform: Platform)
        (title: string)
        (uploader: string)
        (durationSeconds: double)
        (isLive: bool)
        (manifestUrl: string)
        (token: CancellationToken)
        =
        task {
            let! text = Http.getText manifestUrl (Some link) token

            match text with
            | Error message -> return Error message
            | Ok content ->
                let playlist = M3U8.parse manifestUrl content
                let template =
                    { Resolution.empty (platformLabel platform) title with
                        DurationSeconds = durationSeconds
                        Uploader = uploader
                        IsLive = isLive || playlist.IsEndless }

                if playlist.IsMaster then
                    let variants =
                        playlist.Variants
                        |> List.filter (fun variant -> not (String.IsNullOrWhiteSpace variant.Uri))

                    if List.isEmpty variants then
                        return Error "Oynatma listesi varyant içermiyor."
                    else
                        let qualities =
                            variants
                            |> List.mapi M3U8.variantToQuality
                            |> List.sortByDescending (fun quality ->
                                (if quality.IsAudio then 1 else 0),
                                quality.Height,
                                quality.BitrateKbps)

                        return Ok { template with Qualities = qualities }
                elif not (List.isEmpty playlist.Segments) || not (List.isEmpty playlist.InitializationSegments) then
                    let quality =
                        { QualityOption.unknownQuality with
                            Id = "source"
                            Label = "Kaynak"
                            Kind = "hls"
                            AudioUrl = ""
                            ManifestUrl = manifestUrl
                            FormatId = "source"
                            IsLive = template.IsLive
                            Container = if isFragmented content then "mp4" else "ts" }

                    return
                        Ok
                            { template with
                                DurationSeconds =
                                    if template.DurationSeconds > 0.0 then
                                        template.DurationSeconds
                                    else
                                        playlist.TotalDurationSeconds
                                Qualities = [ quality ] }
                else
                    return Error "Oynatma listesi çözümlenemedi."
        }

    let titleFromUrl (url: string) =
        match Guard.tryUri url with
        | None ->
            "video"
        | Some uri ->
            let name = System.IO.Path.GetFileNameWithoutExtension(uri.AbsolutePath)

            match Uri.UnescapeDataString (Guard.text name) with
            | value when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> uri.Host
