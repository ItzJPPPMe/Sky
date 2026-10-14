namespace Sky

open System
open System.Collections.Generic
open System.Globalization

module M3U8 =

    type KeyInfo =
        { Method: string
          Uri: string
          Iv: byte[] option }

    type Segment =
        { Index: int
          Uri: string
          DurationSeconds: float
          Title: string
          IsInitialization: bool
          ByteRangeOffset: int64 option
          ByteRangeLength: int64 option
          Key: KeyInfo option }

    type Variant =
        { Uri: string
          Bandwidth: int64
          AverageBandwidth: int64
          Width: int
          Height: int
          FrameRate: float
          Codecs: string
          AudioGroupId: string
          IsAudio: bool }

    type Playlist =
        { Version: int
          TargetDurationSeconds: float
          MediaSequence: int
          IsMaster: bool
          IsEndless: bool
          Variants: Variant list
          InitializationSegments: Segment list
          Segments: Segment list
          TotalDurationSeconds: float
          PlaylistType: string }

    let private parseAttributes (value: string) =
        let attributes = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let mutable index = 0

        while index < value.Length do
            let equals = value.IndexOf('=', index)

            if equals < 0 then
                index <- value.Length
            else
                let key = value.Substring(index, equals - index).Trim()
                let mutable cursor = equals + 1
                let mutable attributeValue = String.Empty

                if cursor < value.Length && value.[cursor] = '"' then
                    cursor <- cursor + 1
                    let start = cursor
                    let mutable finished = false

                    while cursor < value.Length && not finished do
                        if value.[cursor] = '"' then
                            finished <- true
                        else
                            cursor <- cursor + 1

                    if start < cursor then
                        attributeValue <- value.Substring(start, cursor - start)

                    cursor <- min (cursor + 1) value.Length
                else
                    let start = cursor
                    let mutable finished = false

                    while cursor < value.Length && not finished do
                        if value.[cursor] = ',' then
                            finished <- true
                        else
                            cursor <- cursor + 1

                    attributeValue <- value.Substring(start, cursor - start).Trim()

                if key.Length > 0 then
                    attributes.[key] <- attributeValue

                index <- min (cursor + 1) value.Length

        attributes

    let private attributeValue (attributes: Dictionary<string, string>) (key: string) =
        match attributes.TryGetValue key with
        | true, value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> ""

    let private parseInt (value: string) fallback =
        match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> fallback

    let private parseInt64 (value: string) fallback =
        match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> fallback

    let private parseDouble (value: string) fallback =
        match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, parsed -> parsed
        | _ -> fallback

    let private parseResolution (value: string) =
        let parts = (Guard.text value).Split('x', StringSplitOptions.RemoveEmptyEntries)

        if parts.Length = 2 then
            parseInt parts[0] 0, parseInt parts[1] 0
        else
            0, 0

    let private parseIv (value: string) =
        let trimmed = value.Trim()

        if String.IsNullOrWhiteSpace trimmed || String.Equals(trimmed, "0x", StringComparison.OrdinalIgnoreCase) then
            None
        else
            let hex = trimmed.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("0X", "")

            if hex.Length = 32 then
                try
                    Some(Convert.FromHexString(hex))
                with _ ->
                    None
            else
                None

    let private isAudioVariant (attributes: Dictionary<string, string>) =
        let codecs = attributeValue attributes "CODECS" |> fun value -> value.ToLowerInvariant()

        if codecs.Contains("avc1") || codecs.Contains("hvc1") || codecs.Contains("hev1") || codecs.Contains("av01") || codecs.Contains("vp9") || codecs.Contains("vp09") then
            false
        else
            let name = attributeValue attributes "NAME" |> fun value -> value.ToLowerInvariant()
            name.Contains("audio") || codecs.Contains("mp4a") || codecs.Contains("ac-3") || codecs.Contains("ec-3") || codecs.Contains("opus")

    let absoluteUrl (baseUrl: string) (reference: string) =
        let candidate = Guard.trimmed reference

        if candidate.Length = 0 then
            ""
        else
            match Guard.tryUri candidate with
            | Some uri -> uri.AbsoluteUri
            | None ->
                match Guard.tryUri baseUrl with
                | Some uri ->
                    try
                        Uri(uri, candidate).AbsoluteUri
                    with _ ->
                        candidate
                | None ->
                    candidate

    let parse (playlistUrl: string) (content: string) : Playlist =
        let lines =
            (Guard.text content)
                .Split('\n')
                |> Array.map (fun line -> line.Trim().TrimStart('\uFEFF'))
                |> Array.filter (fun line -> line.Length > 0)

        let version = ref 0
        let targetDuration = ref 0.0
        let mediaSequence = ref 0
        let isMaster = ref false
        let isEndless = ref true
        let playlistType = ref ""
        let variants = ResizeArray<Variant>()
        let initializationSegments = ResizeArray<Segment>()
        let segments = ResizeArray<Segment>()
        let mutable currentKey: KeyInfo option = None
        let mutable currentTitle = ""
        let mutable currentDuration = 0.0
        let mutable currentByteRange: (int64 * int64) option = None
        let mutable currentIsInitialization = false
        let mutable nextByteRangeOffset = 0L
        let mutable segmentIndex = 0

        let addSegment (uri: string) =
            if not (String.IsNullOrWhiteSpace uri) then
                let range = currentByteRange
                let offset = defaultArg (range |> Option.map fst) 0L
                let length = defaultArg (range |> Option.map snd) 0L

                let segment =
                    { Index = segmentIndex
                      Uri = uri
                      DurationSeconds = currentDuration
                      Title = currentTitle
                      IsInitialization = currentIsInitialization
                      ByteRangeOffset = if length > 0L then Some offset else None
                      ByteRangeLength = if length > 0L then Some length else None
                      Key = currentKey }

                if currentIsInitialization then
                    initializationSegments.Add segment
                else
                    segments.Add segment

                segmentIndex <- segmentIndex + 1

        let mutable index = 0

        while index < lines.Length do
            let line = lines[index]

            if line.StartsWith("#EXT", StringComparison.Ordinal) then
                if line.StartsWith("#EXTM3U", StringComparison.Ordinal) then
                    isMaster.Value <- false

                if line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal) then
                    isMaster.Value <- true
                    isEndless.Value <- false

                    let attributes = parseAttributes (line.Substring(line.IndexOf(':') + 1))

                    if index + 1 < lines.Length then
                        let reference = lines[index + 1].Trim()

                        if not (reference.StartsWith("#", StringComparison.Ordinal)) then
                            let width, height = parseResolution (attributeValue attributes "RESOLUTION")
                            let audioOnly = isAudioVariant attributes

                            variants.Add
                                { Uri = absoluteUrl playlistUrl reference
                                  Bandwidth = parseInt64 (attributeValue attributes "BANDWIDTH") 0L
                                  AverageBandwidth = parseInt64 (attributeValue attributes "AVERAGE-BANDWIDTH") 0L
                                  Width = if audioOnly then 0 else width
                                  Height = if audioOnly then 0 else height
                                  FrameRate = parseDouble (attributeValue attributes "FRAME-RATE") 0.0
                                  Codecs = attributeValue attributes "CODECS"
                                  AudioGroupId = attributeValue attributes "AUDIO"
                                  IsAudio = audioOnly }

                            index <- index + 1
                elif line.StartsWith("#EXT-X-VERSION", StringComparison.Ordinal) then
                    version.Value <- parseInt (line.Substring(line.IndexOf(':') + 1)) 0
                elif line.StartsWith("#EXT-X-TARGETDURATION", StringComparison.Ordinal) then
                    targetDuration.Value <- parseDouble (line.Substring(line.IndexOf(':') + 1)) 0.0
                elif line.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal) then
                    mediaSequence.Value <- parseInt (line.Substring(line.IndexOf(':') + 1)) 0
                elif line.StartsWith("#EXT-X-PLAYLIST-TYPE", StringComparison.Ordinal) then
                    playlistType.Value <- (line.Substring(line.IndexOf(':') + 1)).Trim()
                elif line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal) then
                    isEndless.Value <- false
                elif line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal) then
                    let attributes = parseAttributes (line.Substring(line.IndexOf(':') + 1))
                    let method = attributeValue attributes "METHOD"

                    if String.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase) then
                        currentKey <- None
                    elif method.Length > 0 then
                        let keyInfo: KeyInfo =
                            { Method = method.ToUpperInvariant()
                              Uri = absoluteUrl playlistUrl (attributeValue attributes "URI")
                              Iv = parseIv (attributeValue attributes "IV") }

                        currentKey <- Some keyInfo
                elif line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal) then
                    let attributes = parseAttributes (line.Substring(line.IndexOf(':') + 1))
                    currentIsInitialization <- true
                    addSegment (absoluteUrl playlistUrl (attributeValue attributes "URI"))
                    currentIsInitialization <- false
                elif line.StartsWith("#EXTINF", StringComparison.Ordinal) then
                    let value = line.Substring(line.IndexOf(':') + 1)
                    let durationPart = value.Split(',')[0]
                    currentDuration <- parseDouble durationPart 0.0

                    if value.Contains(",") then
                        currentTitle <- value.Substring(value.IndexOf(',') + 1).Trim()
                elif line.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) then
                    let value = (line.Substring(line.IndexOf(':') + 1)).Trim()
                    let parts = value.Split('@')

                    match parts.Length with
                    | 2 ->
                        let length = parseInt64 parts[0] 0L
                        let offset = parseInt64 parts[1] 0L
                        currentByteRange <- Some(length, offset)
                        nextByteRangeOffset <- offset + length
                    | 1 ->
                        let length = parseInt64 parts[0] 0L
                        currentByteRange <- Some(length, nextByteRangeOffset)
                        nextByteRangeOffset <- nextByteRangeOffset + length
                    | _ ->
                        currentByteRange <- None
            else
                currentTitle <- ""

                if currentByteRange.IsSome then
                    let range = currentByteRange
                    let length = defaultArg (range |> Option.map fst) 0L
                    let offset = defaultArg (range |> Option.map snd) 0L
                    currentByteRange <- Some(length, offset)

                addSegment (absoluteUrl playlistUrl line)
                currentDuration <- 0.0
                currentByteRange <- None

            index <- index + 1

        let totalDuration =
            segments
            |> Seq.sumBy (fun segment -> segment.DurationSeconds)

        { Version = version.Value
          TargetDurationSeconds = targetDuration.Value
          MediaSequence = mediaSequence.Value
          IsMaster = isMaster.Value
          IsEndless = isEndless.Value
          Variants = variants |> List.ofSeq
          InitializationSegments = initializationSegments |> List.ofSeq
          Segments = segments |> List.ofSeq
          TotalDurationSeconds = totalDuration
          PlaylistType = playlistType.Value }

    let variantToQuality (index: int) (variant: Variant) =
        { Id = String.Format("v{0}", index)
          Label = QualityOption.heightLabel variant.Height
          Width = variant.Width
          Height = variant.Height
          FrameRate = variant.FrameRate
          BitrateKbps = int (variant.Bandwidth / 1000L)
          Container = "ts"
          IsAudio = variant.IsAudio
          Kind = "hls"
          AudioUrl = ""
          ManifestUrl = variant.Uri
          FormatId = String.Format("v{0}", index)
          IsLive = false }
