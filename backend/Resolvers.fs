namespace Sky

open System
open System.Threading
open System.Threading.Tasks

module Resolvers =

    let private error message = Error message

    let private titleFromUrl (url: string) =
        Playlists.titleFromUrl url

    let isSupported (platform: Platform) =
        match platform with
        | Platform.YouTube
        | Platform.Twitch
        | Platform.Kick
        | Platform.Hls -> true
        | _ -> false

    let platforms () =
        Platform.all
        |> List.map (fun platform ->
            { Id = (Platform.label platform).ToLowerInvariant()
              Name = Platform.label platform
              RequiresYtDlp = platform = Platform.YouTube })

    let private resolveDirect (link: StreamLink) (platform: Platform) (token: CancellationToken) =
        task {
            let! text = Http.getText link.Url (Some link) token

            return
                match text with
                | Error message -> error message
                | Ok content ->
                    let playlist = M3U8.parse link.Url content

                    if playlist.IsMaster then
                        let qualities =
                            playlist.Variants
                            |> List.filter (fun variant -> not (String.IsNullOrWhiteSpace variant.Uri))
                            |> List.mapi M3U8.variantToQuality
                            |> List.sortByDescending (fun quality ->
                                quality.Height * 100000 + quality.BitrateKbps)

                        if List.isEmpty qualities then
                            error "Oynatma listesi varyant içermiyor."
                        else
                            Ok
                                { Resolution.empty (Platform.label platform) (titleFromUrl link.Url) with
                                    DurationSeconds = playlist.TotalDurationSeconds
                                    IsLive = playlist.IsEndless
                                    Qualities = qualities }
                    else
                        let quality =
                            { QualityOption.unknownQuality with
                                Id = "source"
                                Label = "Kaynak"
                                Kind = "hls"
                                AudioUrl = ""
                                ManifestUrl = link.Url
                                FormatId = "source"
                                IsLive = playlist.IsEndless
                                Container =
                                    if content.Contains(".m4s", StringComparison.OrdinalIgnoreCase) then "mp4"
                                    else "ts" }

                        Ok
                            { Resolution.empty (Platform.label platform) (titleFromUrl link.Url) with
                                DurationSeconds = playlist.TotalDurationSeconds
                                IsLive = playlist.IsEndless
                                Qualities = [ quality ] }
        }

    let resolve (url: string) (token: CancellationToken) =
        task {
            let safe = Guard.trimmed url

            if String.IsNullOrWhiteSpace safe then
                return error "Çözümlenecek bağlantı boş olamaz."
            else
                match Guard.tryUri safe with
                | None ->
                    return error "Bağlantı geçerli bir adres değil."
                | Some uri when not (Guard.isHttp uri) ->
                    return error "Yalnızca http ve https adresleri desteklenir."
                | Some _ ->
                    let platform = Platform.ofUrl safe
                    let link = StreamLink.create safe

                    match platform with
                    | Platform.YouTube ->
                        return! YouTube.resolve link token
                    | Platform.Twitch ->
                        return! Twitch.resolve link token
                    | Platform.Kick ->
                        return! Kick.resolve link token
                    | Platform.Hls
                    | Platform.Dash ->
                        return! resolveDirect link Platform.Hls token
                    | Platform.Unknown ->
                        return error "Bağlantı tanınmadı. YouTube, Twitch, Kick veya doğrudan HLS adresi girin."
        }

    let resolveQuality (resolution: Resolution) (qualityId: string) =
        let wanted = Guard.trimmed qualityId

        match resolution.Qualities |> List.tryFind (fun quality -> quality.Id = wanted) with
        | Some quality -> Some quality
        | None -> resolution.Qualities |> List.tryHead

    let toolStatuses () = Tools.toolStatuses ()
