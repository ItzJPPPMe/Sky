namespace Sky

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

module Downloader =

    type private JobRuntime =
        { Status: DownloadStatus
          Request: DownloadRequest
          Resolution: Resolution
          Quality: QualityOption
          SegmentDirectory: string
          OutputPath: string
          Cancel: CancellationTokenSource }

    type private ProgressTracker(limiter: RateLimiter) =
        let mutable bytesDownloaded = 0L
        let mutable segmentsCompleted = 0L
        let mutable segmentsTotal = 0L
        let stopwatch = Stopwatch.StartNew()

        member _.AddBytes(count: int64) =
            Interlocked.Add(&bytesDownloaded, count) |> ignore

        member _.ConsumeAsync(count: int64, token: CancellationToken) =
            limiter.ConsumeAsync(count, token)

        member _.AddSegment() =
            Interlocked.Increment(&segmentsCompleted) |> ignore

        member _.SetSegmentsTotal(total: int) =
            Interlocked.Exchange(&segmentsTotal, int64 total) |> ignore

        member _.Snapshot() =
            let bytes = Interlocked.Read(&bytesDownloaded)
            let completed = Interlocked.Read(&segmentsCompleted)
            let total = Interlocked.Read(&segmentsTotal)
            let seconds = stopwatch.Elapsed.TotalSeconds
            let speed = if seconds <= 0.0 then 0.0 else float bytes / seconds

            let percent =
                if total > 0L then
                    100.0 * float completed / float total
                else
                    0.0

            let eta =
                if speed <= 0.0 || percent <= 0.0 then
                    0.0
                else
                    max 0.0 ((100.0 - percent) / percent * float bytes / speed)

            bytes, int completed, int total, speed, eta, percent

    let private config = DownloaderConfig.defaultConfig ()

    let private settings () = AppSettings.load ()
    let private runtimes = ConcurrentDictionary<string, JobRuntime>()
    let private pausedJobs = ConcurrentDictionary<string, DownloadRequest>()
    let private historyEntries = ResizeArray<HistoryEntry>()
    let private historyLock = obj ()
    let private subscribers = ResizeArray<Action<ProgressEvent>>()
    let private queue = ConcurrentQueue<string>()

    let subscribe (handler: Action<ProgressEvent>) =
        lock subscribers (fun () -> subscribers.Add handler)

        { new IDisposable with
            member _.Dispose() =
                lock subscribers (fun () -> subscribers.Remove handler |> ignore) }

    let private publish (event: ProgressEvent) =
        let handlers = lock subscribers (fun () -> subscribers.ToArray())

        handlers
        |> Array.iter (fun handler ->
            try
                handler.Invoke event
            with _ ->
                ())

    let private publishStatus kind (status: DownloadStatus) =
        publish
            { Kind = kind
              Status = status
              Entry = None }

    let private store (runtime: JobRuntime) =
        runtimes.[runtime.Status.Id] <- runtime
        runtime

    let private patch (id: string) (mapper: JobRuntime -> JobRuntime) =
        match runtimes.TryGetValue id with
        | true, runtime -> Some(store (mapper runtime))
        | _ -> None

    let private patchStatus (id: string) (mapper: DownloadStatus -> DownloadStatus) =
        match patch id (fun runtime -> { runtime with Status = mapper runtime.Status }) with
        | Some runtime ->
            publishStatus ProgressEventKind.status runtime.Status
            Some runtime.Status
        | None ->
            None

    let tryGetJob id =
        match runtimes.TryGetValue id with
        | true, runtime -> Some runtime.Status
        | _ -> None

    let listJobs () =
        runtimes.Values |> Seq.map (fun runtime -> runtime.Status) |> List.ofSeq

    let listHistory () =
        lock historyLock (fun () -> historyEntries |> List.ofSeq)

    let private fileSize (path: string) =
        try
            let info = FileInfo path

            if info.Exists then
                info.Length
            else
                0L
        with _ ->
            0L

    let private delay (milliseconds: int) (token: CancellationToken) =
        task {
            try
                do! Task.Delay(milliseconds, token)
            with _ ->
                ()
        }

    let private fetchOnce (url: string) (range: (int64 * int64) option) (token: CancellationToken) =
        async {
            try
                let! bytes = Http.getBytes url [] range token |> Async.AwaitTask
                return bytes
            with
            | :? OperationCanceledException ->
                return Error "İptal edildi."
            | ex ->
                return Error ex.Message
        }

    let private fetchWithRetry (url: string) (range: (int64 * int64) option) (token: CancellationToken) =
        let rec attempt (remaining: int) =
            task {
                if token.IsCancellationRequested then
                    token.ThrowIfCancellationRequested()

                let! result = fetchOnce url range token |> Async.StartAsTask

                match result with
                | Ok payload -> return Ok payload
                | Error message ->
                    if remaining <= 0 then
                        return Error message
                    else
                        let current = settings ()
                        let step = current.SegmentRetries - remaining + 1
                        do! delay (config.SegmentRetryDelayMs * step) token
                        return! attempt (remaining - 1)
            }

        attempt (settings ()).SegmentRetries

    let private downloadOnce (url: string) (path: string) (range: (int64 * int64) option) (token: CancellationToken) =
        task {
            let! result = fetchWithRetry url range token

            match result with
            | Error message -> return Error message
            | Ok payload ->
                File.WriteAllBytes(path, payload)
                return Ok payload.LongLength
        }

    let private downloadWithRetry (url: string) (path: string) (range: (int64 * int64) option) (token: CancellationToken) =
        downloadOnce url path range token

    let private ivForSegment (segmentIndex: int) =
        let vector = Array.zeroCreate<byte> 16
        let mutable value = uint64 segmentIndex
        let mutable position = 8

        while position >= 0 do
            vector.[position] <- byte (value &&& 0xFFUL)
            value <- value >>> 8
            position <- position - 1

        vector

    let private decryptSegment (payload: byte[]) (key: M3U8.KeyInfo) (segmentIndex: int) (token: CancellationToken) =
        task {
            if not (String.Equals(key.Method, "AES-128", StringComparison.OrdinalIgnoreCase)) then
                return payload
            elif String.IsNullOrWhiteSpace key.Uri then
                return payload
            else
                let! material = Http.getBytes key.Uri [] None token

                return
                    match material with
                    | Ok keyBytes when keyBytes.Length >= 16 ->
                        let initializationVector =
                            match key.Iv with
                            | Some vector when vector.Length = 16 -> vector
                            | _ -> ivForSegment segmentIndex

                        use aes = Aes.Create()
                        aes.KeySize <- 128
                        aes.Mode <- CipherMode.CBC
                        aes.Padding <- PaddingMode.None
                        aes.Key <- keyBytes.[0..15]
                        aes.IV <- initializationVector
                        use decryptor = aes.CreateDecryptor()
                        decryptor.TransformFinalBlock(payload, 0, payload.Length)
                    | _ ->
                        payload
        }

    let private startProgressTicker (runtime: JobRuntime) (tracker: ProgressTracker) (token: CancellationToken) =
        task {
            try
                while not token.IsCancellationRequested do
                    do! Task.Delay(400, token)

                    let bytes, completed, total, speed, eta, percent = tracker.Snapshot()

                    patchStatus runtime.Status.Id (fun status ->
                        let updated = DownloadStatus.updateProgress status percent bytes bytes speed eta

                        { updated with
                            CompletedSegments = completed
                            TotalSegments = total })
                    |> ignore
            with _ ->
                ()
        }

    let private fetchPlaylist (runtime: JobRuntime) (token: CancellationToken) =
        task {
            let! text = Http.getText runtime.Quality.ManifestUrl None token

            match text with
            | Error message -> return Error message
            | Ok content -> return Ok(M3U8.parse runtime.Quality.ManifestUrl content)
        }

    let private segmentPath (runtime: JobRuntime) (segment: M3U8.Segment) =
        Path.Combine(runtime.SegmentDirectory, Segment.fileName segment.Index segment.IsInitialization)

    let private downloadLargeFile
        (url: string)
        (path: string)
        (concurrency: int)
        (tracker: ProgressTracker)
        (token: CancellationToken)
        =
        task {
            let! total = Http.probeSize url [] token

            if total <= 1L then
                let! single = downloadWithRetry url path None token

                match single with
                | Ok count ->
                    do! tracker.ConsumeAsync(count, token)
                    tracker.AddBytes count
                    tracker.SetSegmentsTotal 1
                    return Ok count
                | Error message ->
                    return Error message
            else
                let chunkSize = 4L * 1024L * 1024L
                let chunkCount = int ((total + chunkSize - 1L) / chunkSize)
                let writeLock = obj ()
                let failures = ResizeArray<string>()

                use gate = new SemaphoreSlim(max 1 concurrency)
                use stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 <<< 20, true)
                stream.SetLength total

                let handleChunk (index: int) =
                    task {
                        try
                            try
                                do! gate.WaitAsync token

                                let offset = int64 index * chunkSize
                                let length = min chunkSize (total - offset)

                                let! downloaded = fetchWithRetry url (Some(offset, length)) token

                                match downloaded with
                                | Ok chunk ->
                                    lock writeLock (fun () ->
                                        stream.Seek(offset, SeekOrigin.Begin) |> ignore
                                        stream.Write(chunk, 0, chunk.Length))

                                    let chunkSizeInBytes = int64 chunk.LongLength
                                    do! tracker.ConsumeAsync(chunkSizeInBytes, token)
                                    tracker.AddBytes chunkSizeInBytes
                                    tracker.AddSegment()
                                | Error message ->
                                    failures.Add(String.Format("Parça {0}: {1}", index + 1, message))
                            with
                            | :? OperationCanceledException ->
                                token.ThrowIfCancellationRequested()
                        finally
                            gate.Release() |> ignore
                    }

                let tasks =
                    [ 0 .. chunkCount - 1 ]
                    |> List.map (fun index -> handleChunk index :> Task)
                    |> List.toArray

                tracker.SetSegmentsTotal chunkCount
                do! Task.WhenAll tasks
                stream.Flush(true)

                if failures.Count > 0 then
                    return Error(String.Join("; ", failures |> Seq.truncate 3))
                else
                    return Ok total
        }

    let private downloadDirect (runtime: JobRuntime) (tracker: ProgressTracker) (token: CancellationToken) =
        task {
            let! response =
                downloadLargeFile
                    runtime.Quality.ManifestUrl
                    runtime.OutputPath
                    runtime.Request.SegmentConcurrency
                    tracker
                    token

            match response with
            | Error message -> return Error message
            | Ok count ->
                tracker.SetSegmentsTotal 1
                return Ok count
        }

    let private downloadSegments
        (runtime: JobRuntime)
        (tracker: ProgressTracker)
        (segments: M3U8.Segment list)
        (sharedFile: string option)
        (token: CancellationToken)
        =
        task {
            use gate = new SemaphoreSlim(runtime.Request.SegmentConcurrency)
            let failures = ResizeArray<string>()

            let handleSegment (segment: M3U8.Segment) =
                task {
                    try
                        try
                            do! gate.WaitAsync token

                            let path =
                                match sharedFile with
                                | Some file -> file
                                | None -> segmentPath runtime segment

                            let range =
                                match segment.ByteRangeLength with
                                | Some length -> Some(defaultArg segment.ByteRangeOffset 0L, length)
                                | None -> None

                            let cached =
                                match sharedFile with
                                | Some _ -> false
                                | None -> fileSize path > 0L

                            if not cached then
                                let! downloaded = downloadWithRetry segment.Uri path range token

                                match downloaded with
                                | Ok count ->
                                    let mutable size = count

                                    match segment.Key with
                                    | Some key when String.Equals(key.Method, "AES-128", StringComparison.OrdinalIgnoreCase) ->
                                        let payload = File.ReadAllBytes path
                                        let! decrypted = decryptSegment payload key segment.Index token
                                        File.WriteAllBytes(path, decrypted)
                                        size <- int64 decrypted.LongLength
                                    | _ ->
                                        ()

                                    do! tracker.ConsumeAsync(size, token)
                                    tracker.AddBytes size
                                    tracker.AddSegment()
                                | Error message ->
                                    failures.Add(String.Format("Segment {0}: {1}", segment.Index, message))
                        with
                        | :? OperationCanceledException ->
                            token.ThrowIfCancellationRequested()
                    finally
                        gate.Release() |> ignore
                }

            let tasks =
                segments
                |> List.map (fun segment -> handleSegment segment :> Task)
                |> List.toArray

            do! Task.WhenAll tasks

            if failures.Count > 0 then
                return Error(String.Join("; ", failures |> Seq.truncate 5))
            else
                return Ok()
        }

    let private writeShared (path: string) (offset: int64) (payload: byte[]) =
        use stream =
            new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 <<< 20, true)

        stream.Seek(offset, SeekOrigin.Begin) |> ignore
        stream.Write(payload, 0, payload.Length)

    let private mergeSegments (runtime: JobRuntime) (sharedFile: string option) (segments: M3U8.Segment list) (token: CancellationToken) =
        task {
            match sharedFile with
            | Some file ->
                let bytes = fileSize file

                if bytes > 0L then
                    File.Copy(file, runtime.OutputPath, true)
                    return Ok bytes
                else
                    return Error "Birleştirilecek veri bulunamadı."
            | None ->
                let files =
                    segments
                    |> List.filter (fun segment -> File.Exists(segmentPath runtime segment))
                    |> List.map (fun segment -> segmentPath runtime segment)

                if List.isEmpty files then
                    return Error "Birleştirilecek segment bulunamadı."
                else
                    use target =
                        new FileStream(runtime.OutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 <<< 20, true)

                    for path in files do
                        token.ThrowIfCancellationRequested()

                        use source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 <<< 20, true)
                        do! source.CopyToAsync(target, 1 <<< 20, token)

                    target.Flush(true)
                    return Ok(fileSize runtime.OutputPath)
        }

    let private collectSegments (runtime: JobRuntime) (tracker: ProgressTracker) (token: CancellationToken) =
        task {
            let seen = HashSet<string>(StringComparer.Ordinal)
            let ordered = ResizeArray<M3U8.Segment>()
            let mutable sharedFile: string option = None
            let mutable finished = false
            let mutable failure: string option = None

            while not finished && token.IsCancellationRequested |> not do
                let! playlist = fetchPlaylist runtime token

                match playlist with
                | Error message -> failure <- Some message
                | Ok value ->
                    let freshInit = ResizeArray<M3U8.Segment>()

                    for segment in value.InitializationSegments do
                        if seen.Add segment.Uri then
                            ordered.Add segment
                            freshInit.Add segment

                    let fresh = value.Segments |> List.filter (fun segment -> seen.Add segment.Uri)

                    let byterange =
                        match value.Segments with
                        | head :: _ -> head.ByteRangeLength.IsSome
                        | [] -> false

                    if byterange && Option.isNone sharedFile then
                        let file = Path.Combine(runtime.SegmentDirectory, "media.bin")

                        if not (File.Exists file) then
                            File.WriteAllBytes(file, Array.empty)

                        sharedFile <- Some file

                    for segment in fresh do
                        ordered.Add segment

                    let batch = freshInit |> List.ofSeq |> List.append fresh

                    if not (List.isEmpty batch) then
                        tracker.SetSegmentsTotal ordered.Count

                        let! downloaded = downloadSegments runtime tracker batch sharedFile token

                        match downloaded with
                        | Error message -> failure <- Some message
                        | Ok() -> ()

                    if Option.isSome failure || not value.IsEndless then
                        finished <- true
                    else
                        let wait = max 1.0 (value.TargetDurationSeconds / float (settings ()).LivePollDivisor)
                        do! delay (int (wait * 1000.0)) token

            match failure with
            | Some message -> return Error message
            | None ->
                let all = ordered |> List.ofSeq
                tracker.SetSegmentsTotal all.Length
                return Ok(all, sharedFile)
        }

    let private finishJob (runtime: JobRuntime) (warning: string) =
        let size = fileSize runtime.OutputPath

        match patchStatus runtime.Status.Id (fun status ->
            let updated = DownloadStatus.updateProgress status 100.0 size size 0.0 0.0
            let completed = max status.CompletedSegments status.TotalSegments

            { updated with
                State = DownloadState.completed
                FilePath = runtime.OutputPath
                Warning = Guard.text warning
                CompletedSegments = completed
                TotalSegments = completed }) with
        | Some status ->
            let entry =
                { Id = status.Id
                  Url = status.Url
                  Title = status.Title
                  Uploader = status.Uploader
                  Platform = status.Platform
                  QualityLabel = status.QualityLabel
                  FilePath = runtime.OutputPath
                  SizeBytes = size
                  DurationSeconds = runtime.Resolution.DurationSeconds
                  CompletedAtUtc = DateTimeOffset.UtcNow }

            lock historyLock (fun () ->
                historyEntries.Insert(0, entry)

                let limit = (settings ()).HistoryLimit

                while historyEntries.Count > limit do
                    historyEntries.RemoveAt(historyEntries.Count - 1))

            publish
                { Kind = ProgressEventKind.finished
                  Status = { status with State = DownloadState.completed }
                  Entry = Some entry }

            try
                if Directory.Exists runtime.SegmentDirectory then
                    Directory.Delete(runtime.SegmentDirectory, true)
            with _ ->
                ()
        | None ->
            ()

    let private completeJob (runtime: JobRuntime) =
        finishJob runtime String.Empty

    let private completeJobWithWarning (runtime: JobRuntime) (warning: string) =
        finishJob runtime warning

    let private failJob id message =
        match patchStatus id (fun status ->
            { status with
                State = DownloadState.failed
                ErrorMessage = message }) with
        | Some status -> publishStatus ProgressEventKind.finished status
        | None -> ()

    let private cancelJobInternal id =
        let state =
            if pausedJobs.ContainsKey id then
                DownloadState.paused
            else
                DownloadState.cancelled

        match patchStatus id (fun status -> { status with State = state }) with
        | Some status -> publishStatus ProgressEventKind.finished status
        | None -> ()

    let private containerFromSegments (segments: M3U8.Segment list) =
        segments
        |> List.filter (fun segment -> not segment.IsInitialization)
        |> List.map (fun segment -> segment.Uri.ToLowerInvariant())
        |> List.tryFind (fun uri -> uri.EndsWith(".ts") || uri.EndsWith(".m4s") || uri.EndsWith(".mp4"))
        |> Option.map (fun uri ->
            if uri.EndsWith(".ts") then
                "ts"
            else
                "mp4")
        |> Option.defaultValue "ts"

    let private retargetOutput (runtime: JobRuntime) (segments: M3U8.Segment list) =
        let container = containerFromSegments segments
        let current = runtime.OutputPath
        let directory = Guard.text (Path.GetDirectoryName current)
        let baseName = Guard.text (Path.GetFileNameWithoutExtension current)
        let candidate = Path.Combine(directory, String.Concat(baseName, ".", container))

        match patch runtime.Status.Id (fun current' -> { current' with OutputPath = candidate }) with
        | Some updated -> updated
        | None -> runtime

    let private remuxToMp4 (inputPath: string) (audioPath: string option) (token: CancellationToken) =
        task {
            let! tool = Tools.ffmpeg()

            match tool with
            | None ->
                return Error "MP4 oluşturmak için ffmpeg gerekli ancak bulunamadı."
            | Some(invocation, _) ->
                let directory = Guard.text (Path.GetDirectoryName inputPath)
                let baseName = Path.GetFileNameWithoutExtension inputPath
                let outputPath = Path.Combine(directory, String.Concat(baseName, ".mp4"))
                let size = fileSize inputPath
                let timeout = int (max 240000.0 (float size / 1200.0))

                let common =
                    [ "-hide_banner"
                      "-loglevel"
                      "error"
                      "-nostdin"
                      "-y"
                      "-i"
                      inputPath ]

                let tail =
                    match audioPath with
                    | Some audio ->
                        [ "-i"
                          audio
                          "-map"
                          "0:v:0"
                          "-map"
                          "1:a:0" ]
                    | None ->
                        [ "-map"
                          "0" ]

                let arguments =
                    common
                    @ tail
                    @ [ "-c"
                        "copy"
                        "-movflags"
                        "+faststart"
                        outputPath ]

                let! result = Tools.capture invocation arguments timeout

                match result with
                | Ok _ when fileSize outputPath > 0L ->
                    try
                        File.Delete inputPath
                    with _ ->
                        ()

                    return Ok outputPath
                | Ok _ ->
                    return Error "ffmpeg çıktı üretmedi."
                | Error message ->
                    let detail =
                        if String.IsNullOrWhiteSpace message then
                            "bilinmeyen hata"
                        else
                            message.Trim()

                    return Error(String.Format("ffmpeg: {0}", detail))
        }

    let private downloadAudioTrack (runtime: JobRuntime) (tracker: ProgressTracker) (token: CancellationToken) =
        task {
            let url = Guard.trimmed runtime.Quality.AudioUrl

            if String.IsNullOrWhiteSpace url then
                return Ok None
            else
                let path = Path.Combine(runtime.SegmentDirectory, "audio.m4a")
                let! downloaded = downloadLargeFile url path runtime.Request.SegmentConcurrency tracker token

                match downloaded with
                | Ok _ -> return Ok(Some path)
                | Error message -> return Error(String.Format("Ses indirilemedi: {0}", message))
        }

    let private ensureMp4 (runtime: JobRuntime) (tracker: ProgressTracker) =
        task {
            let token = runtime.Cancel.Token
            let current = runtime.OutputPath
            let isTransportStream = String.Equals(Path.GetExtension current, ".ts", StringComparison.OrdinalIgnoreCase)
            let remuxEnabled = (settings ()).AutoRemuxToMp4

            if not isTransportStream || not remuxEnabled then
                return Ok(runtime, String.Empty)
            else
                patchStatus runtime.Status.Id (fun status ->
                    { status with State = DownloadState.muxing })
                |> ignore

                let! audio = downloadAudioTrack runtime tracker token

                match audio with
                | Error message -> return Error message
                | Ok audioPath ->
                    let! muxed = remuxToMp4 current audioPath token

                    match muxed with
                    | Ok path ->
                        match patch runtime.Status.Id (fun current' -> { current' with OutputPath = path }) with
                        | Some updated ->
                            patchStatus updated.Status.Id (fun status ->
                                { status with FilePath = path })
                            |> ignore

                            return Ok(updated, String.Empty)
                        | None ->
                            return Error "İş kaydı bulunamadı."
                    | Error message ->
                        return Ok(runtime, message)
        }

    let private executeJob (runtime: JobRuntime) =
        task {
            let token = runtime.Cancel.Token
            let tracker = ProgressTracker(RateLimiter(Sky.AppSettings.speedLimitBytes ()))
            use tickerSource = new CancellationTokenSource()

            patchStatus runtime.Status.Id (fun status ->
                { status with State = DownloadState.downloading })
            |> ignore

            startProgressTicker runtime tracker tickerSource.Token |> ignore

            try
                if runtime.Quality.Kind = "direct" then
                    let! direct = downloadDirect runtime tracker token

                    match direct with
                    | Error message -> failJob runtime.Status.Id message
                    | Ok _ -> completeJob runtime
                else
                    let! collected = collectSegments runtime tracker token

                    match collected with
                    | Error message -> failJob runtime.Status.Id message
                    | Ok(segments, sharedFile) ->
                        if List.isEmpty segments then
                            failJob runtime.Status.Id "İndirilecek segment bulunamadı."
                        else
                            let runtime = retargetOutput runtime segments

                            patchStatus runtime.Status.Id (fun status ->
                                { status with
                                    State = DownloadState.merging
                                    FilePath = runtime.OutputPath
                                    TotalSegments = segments.Length
                                    CompletedSegments = segments.Length })
                            |> ignore

                            let! merged = mergeSegments runtime sharedFile segments token

                            match merged with
                            | Error message -> failJob runtime.Status.Id message
                            | Ok _ ->
                                let hasOutput = fileSize runtime.OutputPath > 0L

                                if not hasOutput then
                                    failJob runtime.Status.Id "Birleştirilen dosya oluşturulamadı."
                                else
                                    let! ensured = ensureMp4 runtime tracker

                                    match ensured with
                                    | Ok(final, warning) ->
                                        if String.IsNullOrWhiteSpace warning then
                                            completeJob final
                                        else
                                            completeJobWithWarning final warning
                                    | Error message ->
                                        failJob runtime.Status.Id message
            finally
                tickerSource.Cancel()
        }

    let private activeJobCount () =
        let running = [ DownloadState.resolving; DownloadState.downloading; DownloadState.merging; DownloadState.muxing ]

        runtimes.Values
        |> Seq.filter (fun runtime -> running |> List.contains runtime.Status.State)
        |> Seq.length

    let rec private pumpQueue () =
        let limit = (settings ()).MaxParallelJobs
        let mutable running = activeJobCount ()
        let mutable progressed = true

        while running < limit && progressed do
            progressed <- false
            let mutable dequeuedId = String.Empty

            if queue.TryDequeue(&dequeuedId) then
                progressed <- true

                match runtimes.TryGetValue dequeuedId with
                | true, runtime when runtime.Status.State = DownloadState.queued ->
                    let fresh =
                        { runtime with
                            Status =
                                { runtime.Status with
                                    State = DownloadState.downloading
                                    ErrorMessage = ""
                                    Warning = ""
                                    BytesPerSecond = 0.0
                                    EtaSeconds = 0.0
                                    BytesDownloaded = 0L }
                            Cancel = new CancellationTokenSource() }

                    store fresh |> ignore
                    publishStatus ProgressEventKind.status fresh.Status
                    startRuntime fresh
                    running <- running + 1
                | _ ->
                    ()

    and private startRuntime (runtime: JobRuntime) =
        Task.Run(Func<Task>(fun () -> background runtime)) |> ignore

    and private background (runtime: JobRuntime) =
        task {
            try
                do! executeJob runtime
            with
            | :? OperationCanceledException ->
                cancelJobInternal runtime.Status.Id
            | ex ->
                failJob runtime.Status.Id ex.Message

            pumpQueue ()
        }

    let private sanitizeFileName (value: string) =
        let invalid = Path.GetInvalidFileNameChars()
        let builder = StringBuilder()

        for character in Guard.text value do
            builder.Append(if invalid |> Array.contains character then '-' else character) |> ignore

        let cleaned = builder.ToString().Trim()

        if String.IsNullOrWhiteSpace cleaned then
            "sky-video"
        else
            cleaned

    let private containerOf (quality: QualityOption) =
        if String.IsNullOrWhiteSpace quality.Container then
            "ts"
        else
            quality.Container

    let private uniqueOutputPath (directory: string) (baseName: string) (extension: string) =
        let mutable candidate = Path.Combine(directory, String.Concat(baseName, ".", extension))
        let mutable index = 1

        while File.Exists candidate && index < 1000 do
            let value = String.Concat(baseName, " (", string index, ")")
            candidate <- Path.Combine(directory, String.Concat(value, ".", extension))
            index <- index + 1

        candidate

    let private applyAudioTrack (resolution: Resolution) (request: DownloadRequest) (quality: QualityOption) =
        let requested = Guard.trimmed request.AudioTrackId

        let selected =
            if String.IsNullOrWhiteSpace requested then
                None
            else
                resolution.AudioTracks
                |> List.tryFind (fun track -> String.Equals(track.Id, requested, StringComparison.Ordinal))

        match selected with
        | Some track ->
            { quality with AudioUrl = track.Url }
        | None ->
            if String.IsNullOrWhiteSpace quality.AudioUrl then
                match resolution.AudioTracks |> List.tryFind (fun track -> track.IsRecommended) with
                | Some track -> { quality with AudioUrl = track.Url }
                | None -> quality
            else
                quality

    let startJob (request: DownloadRequest) : Task<Result<DownloadStatus, string>> =
        task {
            match DownloadRequest.validate request with
            | error :: _ ->
                return Error error
            | [] ->
                let stored = Sessions.tryGet request.SessionId
                let fromSession =
                    match stored with
                    | Some resolution when not (List.isEmpty resolution.Qualities) -> Some resolution
                    | _ -> None

                let! resolutionResult =
                    match fromSession with
                    | Some resolution -> Task.FromResult(Ok resolution)
                    | None -> Resolvers.resolve request.Url CancellationToken.None

                match resolutionResult with
                | Error message -> return Error message
                | Ok resolution ->
                    match Resolvers.resolveQuality resolution request.QualityId with
                    | None -> return Error "Seçilen kalite bulunamadı."
                    | Some quality ->
                        let quality = applyAudioTrack resolution request quality
                        let id = Guid.NewGuid().ToString("N")

                        let outputDirectory =
                            if String.IsNullOrWhiteSpace request.OutputDirectory then
                                (settings ()).OutputDirectory
                            else
                                request.OutputDirectory

                        Directory.CreateDirectory outputDirectory |> ignore

                        let requested = Guard.trimmed request.FileName

                        let baseName =
                            if String.IsNullOrWhiteSpace requested then
                                resolution.Title
                            else
                                requested

                        let template = (settings ()).FileNameTemplate
                        let withTemplate = AppSettings.fileNameFor template baseName resolution.Platform (quality.Label) (containerOf quality)
                        let fileName = if String.IsNullOrWhiteSpace requested then withTemplate else sanitizeFileName baseName

                        let outputPath = uniqueOutputPath outputDirectory fileName (containerOf quality)
                        let segmentDirectory = Path.Combine(outputDirectory, String.Concat(".sky-", id))
                        Directory.CreateDirectory segmentDirectory |> ignore

                        let status =
                            { DownloadStatus.empty id request.Url with
                                Title = resolution.Title
                                Uploader = resolution.Uploader
                                Platform = resolution.Platform
                                QualityId = quality.Id
                                QualityLabel = QualityOption.describe quality
                                State = DownloadState.queued
                                IsLive = resolution.IsLive
                                FilePath = outputPath }

                        let runtime =
                            { Status = status
                              Request =
                                { request with
                                    OutputDirectory = outputDirectory
                                    QualityId = quality.Id
                                    SegmentConcurrency =
                                        if request.SegmentConcurrency < 1 then
                                            (settings ()).SegmentConcurrency
                                        else
                                            request.SegmentConcurrency }
                              Resolution = resolution
                              Quality = quality
                              SegmentDirectory = segmentDirectory
                              OutputPath = outputPath
                              Cancel = new CancellationTokenSource() }

                        store runtime |> ignore
                        publishStatus ProgressEventKind.status status

                        queue.Enqueue id
                        pumpQueue ()

                        return Ok status
        }

    let private removePaused id =
        let mutable removed = Unchecked.defaultof<DownloadRequest>
        pausedJobs.TryRemove(id, &removed) |> ignore
        removed

    let private removeRuntime id =
        let mutable removed = Unchecked.defaultof<JobRuntime>
        runtimes.TryRemove(id, &removed) |> ignore
        removed

    let cancelJob id =
        match runtimes.TryGetValue id with
        | true, runtime ->
            removePaused id |> ignore
            runtime.Cancel.Cancel()
            true
        | _ ->
            false

    let pauseJob id =
        match runtimes.TryGetValue id with
        | true, runtime when DownloadState.isActive runtime.Status.State ->
            pausedJobs[id] <- runtime.Request
            runtime.Cancel.Cancel()
            true
        | _ ->
            false

    let resumeJob id =
        let request = removePaused id

        if isNull (box request) then
            Task.FromResult false
        else
            removeRuntime id |> ignore

            task {
                let! result = startJob request

                match result with
                | Ok _ -> return true
                | Error _ -> return false
            }

    let removeHistory id =
        lock historyLock (fun () -> historyEntries.RemoveAll(fun entry -> entry.Id = id) |> ignore)

    let shutdown () =
        runtimes.Values |> Seq.iter (fun runtime -> runtime.Cancel.Cancel())

        lock subscribers (fun () -> subscribers.Clear())
