namespace AITeam.KnowledgeSight.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open AITeam.KnowledgeSight

module ConflictScopeNormalizationTests =

    let private writeFile (filePath: string) (content: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)) |> ignore
        File.WriteAllText(filePath, content.TrimStart(), Encoding.UTF8)

    let private writeConfig (repoRoot: string) (embeddingUrl: string) =
        let config =
            {|
                docDirs = [| "docs" |]
                archiveProcessed = true
                embeddingUrl = embeddingUrl
            |}

        let json = JsonSerializer.Serialize(config, JsonSerializerOptions(WriteIndented = true))
        File.WriteAllText(Path.Combine(repoRoot, "knowledge-sight.json"), json)

    let private findFreePort () =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        port

    let private toDotSlashPath (relativePath: string) =
        "./" + relativePath.Replace("\\", "/").TrimStart('.', '/', '\\')

    let private toDotBackslashPath (relativePath: string) =
        ".\\" + relativePath.Replace("/", "\\").TrimStart('.', '/', '\\')

    let private rewritePersistedIndexPaths (repoRoot: string) (rewriteRelativePath: string -> string) =
        let cfg = Config.load repoRoot
        let indexDir = cfg.IndexDir
        let chunkPath = Path.Combine(indexDir, "chunks.tsv")
        let frontmatterJsonPath = Path.Combine(indexDir, "frontmatters.jsonl")
        let frontmatterTsvPath = Path.Combine(indexDir, "frontmatters.tsv")

        let rewriteStoredPath (storedPath: string) =
            let relativePath =
                if Path.IsPathRooted(storedPath) then Path.GetRelativePath(repoRoot, storedPath)
                else storedPath
            rewriteRelativePath relativePath

        let chunkLines = File.ReadAllLines(chunkPath)
        let updatedChunkLines =
            chunkLines
            |> Array.mapi (fun index line ->
                if index = 0 && line.StartsWith("#fields:", StringComparison.Ordinal) then line
                else
                    let fields = line.Split('\t')
                    if fields.Length >= 10 then
                        fields.[0] <- rewriteStoredPath fields.[0]
                        String.concat "\t" fields
                    else
                        line)
        File.WriteAllLines(chunkPath, updatedChunkLines)

        let updatedJsonLines =
            File.ReadAllLines(frontmatterJsonPath)
            |> Array.map (fun line ->
                use doc = JsonDocument.Parse(line)
                let root = doc.RootElement
                let filePath =
                    match root.TryGetProperty("filePath") with
                    | true, value -> value.GetString()
                    | _ -> ""
                let fields =
                    match root.TryGetProperty("fields") with
                    | true, value -> value
                    | _ -> failwith "frontmatters.jsonl row missing fields"
                JsonSerializer.Serialize({| filePath = rewriteStoredPath filePath; fields = fields |}))
        File.WriteAllLines(frontmatterJsonPath, updatedJsonLines)

        let updatedLegacyLines =
            File.ReadAllLines(frontmatterTsvPath)
            |> Array.map (fun line ->
                let fields = line.Split('\t')
                if fields.Length >= 6 then
                    fields.[0] <- rewriteStoredPath fields.[0]
                    String.concat "\t" fields
                else
                    line)
        File.WriteAllLines(frontmatterTsvPath, updatedLegacyLines)

    let private readEmbeddings (path: string) =
        use fs = File.OpenRead(path)
        use br = new BinaryReader(fs)
        let count = br.ReadInt32()
        if count = 0 then [||]
        else
            let dim = br.ReadInt32()
            Array.init count (fun _ -> Array.init dim (fun _ -> br.ReadSingle()))

    let private writeEmbeddings (path: string) (embeddings: float32[][]) =
        use fs = File.Create(path)
        use bw = new BinaryWriter(fs)
        bw.Write(embeddings.Length)
        if embeddings.Length > 0 then
            bw.Write(embeddings.[0].Length)
            embeddings
            |> Array.iter (fun embedding ->
                embedding |> Array.iter bw.Write)

    let private normalizeStoredRelativePath (repoRoot: string) (storedPath: string) =
        let relativePath =
            if Path.IsPathRooted(storedPath) then Path.GetRelativePath(repoRoot, storedPath)
            else storedPath

        relativePath.Replace("\\", "/").TrimStart('.', '/', '\\')

    let private makeFirstChunkUnusableConflictAnchor (repoRoot: string) (relativePath: string) =
        let cfg = Config.load repoRoot
        let chunkPath = Path.Combine(cfg.IndexDir, "chunks.tsv")
        let embeddingsPath = Path.Combine(cfg.IndexDir, "embeddings.emb")
        let normalizedTarget = relativePath.Replace("\\", "/").TrimStart('.', '/', '\\')
        let allLines = File.ReadAllLines(chunkPath)
        let header, dataLines =
            if allLines.Length > 0 && allLines.[0].StartsWith("#fields:", StringComparison.Ordinal) then
                allLines.[0], allLines.[1..]
            else
                "", allLines

        let embeddings = readEmbeddings embeddingsPath
        Assert.Equal(dataLines.Length, embeddings.Length)

        let rows =
            dataLines
            |> Array.mapi (fun index line ->
                let fields = line.Split('\t')
                let normalizedPath = normalizeStoredRelativePath repoRoot fields.[0]
                let level = int fields.[3]
                let startLine = int fields.[4]
                index, line, embeddings.[index], normalizedPath, level, startLine)

        let targetRows =
            rows
            |> Array.filter (fun (_, _, _, normalizedPath, _, _) ->
                String.Equals(normalizedPath, normalizedTarget, StringComparison.OrdinalIgnoreCase))

        Assert.True(targetRows.Length >= 2, sprintf "Expected multiple persisted chunks for %s" relativePath)

        let targetPrimaryIndex =
            targetRows
            |> Array.minBy (fun (_, _, _, _, level, startLine) -> level, startLine)
            |> fun (index, _, _, _, _, _) -> index

        let reordered =
            rows
            |> Array.sortBy (fun (index, _, _, _, _, _) ->
                if index = targetPrimaryIndex then Int32.MaxValue else index)

        let reorderedLines = reordered |> Array.map (fun (_, line, _, _, _, _) -> line)
        let reorderedEmbeddings = reordered |> Array.map (fun (_, _, embedding, _, _, _) -> embedding)
        let truncatedEmbeddings = reorderedEmbeddings.[0 .. reorderedEmbeddings.Length - 2]

        let outputLines =
            if String.IsNullOrEmpty(header) then reorderedLines
            else Array.append [| header |] reorderedLines

        File.WriteAllLines(chunkPath, outputLines)
        writeEmbeddings embeddingsPath truncatedEmbeddings

    let private zeroPersistedConflictAnchors (repoRoot: string) =
        let cfg = Config.load repoRoot
        let embeddingsPath = Path.Combine(cfg.IndexDir, "embeddings.emb")
        let embeddings = readEmbeddings embeddingsPath
        embeddings
        |> Array.map (fun _ -> [||])
        |> writeEmbeddings embeddingsPath

    type private EmbeddingServer private (listener: HttpListener, loopTask: Task, port: int) =
        member _.EmbeddingUrl = sprintf "http://127.0.0.1:%d/v1/embeddings" port

        interface IDisposable with
            member _.Dispose() =
                try
                    if listener.IsListening then
                        listener.Stop()
                    listener.Close()
                with _ ->
                    ()

                try
                    loopTask.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
                with _ ->
                    ()

        static member Start() =
            let port = findFreePort ()
            let listener = new HttpListener()
            listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
            listener.Start()

            let loopTask =
                Task.Run(fun () ->
                    task {
                        try
                            while listener.IsListening do
                                let! context = listener.GetContextAsync()
                                use reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding)
                                let! body = reader.ReadToEndAsync()

                                try
                                    if String.Equals(context.Request.RawUrl, "/v1/embeddings", StringComparison.Ordinal) then
                                        use doc = JsonDocument.Parse(body)
                                        let input =
                                            match doc.RootElement.TryGetProperty("input") with
                                            | true, value -> value
                                            | _ -> failwith "embedding request missing input"

                                        let embeddings =
                                            input.EnumerateArray()
                                            |> Seq.map (fun _ -> {| embedding = [| 1.0f; 0.0f; 0.0f |] |})
                                            |> Seq.toArray

                                        let payload = JsonSerializer.Serialize({| data = embeddings |})
                                        let bytes = Encoding.UTF8.GetBytes(payload)
                                        context.Response.StatusCode <- 200
                                        context.Response.ContentType <- "application/json"
                                        context.Response.ContentLength64 <- int64 bytes.Length
                                        do! context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                                    else
                                        context.Response.StatusCode <- 404
                                finally
                                    context.Response.Close()
                        with
                        | :? HttpListenerException
                        | :? ObjectDisposedException -> ()
                    } :> Task)

            new EmbeddingServer(listener, loopTask, port)

    type private ConflictScopeHarness private (repoRoot: string, server: EmbeddingServer, engine: Jint.Engine) =
        member _.EvalJson (query: string) = QueryEngine.evalJson engine query

        interface IDisposable with
            member _.Dispose() =
                (server :> IDisposable).Dispose()
                try
                    Directory.Delete(repoRoot, true)
                with _ ->
                    ()

        static member Create(rewriteRelativePath: string -> string, ?reshapePersistedIndex: string -> unit) =
            let server = EmbeddingServer.Start()
            let repoRoot = Path.Combine(Path.GetTempPath(), sprintf "ks-conflicts-scope-tests-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(repoRoot) |> ignore
            writeConfig repoRoot server.EmbeddingUrl

            writeFile
                (Path.Combine(repoRoot, "docs", "canon", "alpha.md"))
                """
---
title: "Alpha"
status: "active"
source: "canon"
---
Alpha intro.

## Details

Scope normalization candidate alpha.
"""

            writeFile
                (Path.Combine(repoRoot, "docs", "canon", "beta.md"))
                """
---
title: "Beta"
status: "active"
source: "canon"
---
Beta intro.

## Details

Scope normalization candidate beta.
"""

            let cfg = Config.load repoRoot

            match IndexingWorkflow.rebuild cfg with
            | Error message ->
                (server :> IDisposable).Dispose()
                failwithf "Index build failed in test harness: %s" message
            | Ok _ ->
                rewritePersistedIndexPaths repoRoot rewriteRelativePath
                reshapePersistedIndex |> Option.iter (fun reshape -> reshape repoRoot)
                match IndexStore.load cfg.IndexDir with
                | None ->
                    (server :> IDisposable).Dispose()
                    failwith "Persisted index reload failed in test harness."
                | Some reloadedIndex ->
                    let engine = QueryEngine.create cfg reloadedIndex None
                    new ConflictScopeHarness(repoRoot, server, engine)

    let private getRequiredProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> value
        | _ -> failwithf "Missing property '%s'" name

    let private getSingleError (element: JsonElement) =
        Assert.Equal(JsonValueKind.Array, element.ValueKind)
        Assert.Equal(1, element.GetArrayLength())
        element[0]

    [<Fact>]
    let ``conflicts exact scope accepts repo relative selector when stored path keeps leading dot slash`` () =
        use harness = ConflictScopeHarness.Create(toDotSlashPath)

        use contextDoc = JsonDocument.Parse(harness.EvalJson("context('docs/canon/alpha.md')"))
        Assert.Equal("Alpha", (getRequiredProperty "title" contextDoc.RootElement).GetString())

        use conflictsDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/alpha.md', pairs:true, threshold:0.10})"))

        let results = conflictsDoc.RootElement
        Assert.Equal(JsonValueKind.Array, results.ValueKind)
        Assert.Equal(0, results.GetArrayLength())

    [<Fact>]
    let ``conflicts exact scope accepts repo relative selector when stored path keeps leading dot backslash`` () =
        use harness = ConflictScopeHarness.Create(toDotBackslashPath)

        use contextDoc = JsonDocument.Parse(harness.EvalJson("context('docs/canon/alpha.md')"))
        Assert.Equal("Alpha", (getRequiredProperty "title" contextDoc.RootElement).GetString())

        use conflictsDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/alpha.md', pairs:true, threshold:0.10})"))

        let results = conflictsDoc.RootElement
        Assert.Equal(JsonValueKind.Array, results.ValueKind)
        Assert.Equal(0, results.GetArrayLength())

    [<Fact>]
    let ``conflicts dir and glob scope still work when stored path keeps leading dot backslash`` () =
        use harness = ConflictScopeHarness.Create(toDotBackslashPath)

        use dirDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon', pairs:true, threshold:0.10})"))

        let dirResults = dirDoc.RootElement
        Assert.Equal(JsonValueKind.Array, dirResults.ValueKind)
        Assert.True(dirResults.GetArrayLength() > 0, dirResults.GetRawText())

        use globDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/*', pairs:true, threshold:0.10})"))

        let globResults = globDoc.RootElement
        Assert.Equal(JsonValueKind.Array, globResults.ValueKind)
        Assert.True(globResults.GetArrayLength() > 0, globResults.GetRawText())

    [<Fact>]
    let ``conflicts exact scope still resolves when first chunk remains the usable anchor`` () =
        use harness = ConflictScopeHarness.Create(toDotBackslashPath)

        use conflictsDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/alpha.md', pairs:true, threshold:0.10})"))

        let results = conflictsDoc.RootElement
        Assert.Equal(JsonValueKind.Array, results.ValueKind)
        Assert.Equal(0, results.GetArrayLength())

    [<Fact>]
    let ``conflicts exact scope resolves persisted doc when first chunk is not a usable anchor`` () =
        use harness =
            ConflictScopeHarness.Create(
                toDotBackslashPath,
                reshapePersistedIndex = (fun repoRoot -> makeFirstChunkUnusableConflictAnchor repoRoot "docs/canon/alpha.md"))

        use contextDoc = JsonDocument.Parse(harness.EvalJson("context('docs/canon/alpha.md')"))
        Assert.Equal("Alpha", (getRequiredProperty "title" contextDoc.RootElement).GetString())

        use conflictsDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/alpha.md', pairs:true, threshold:0.10})"))

        let results = conflictsDoc.RootElement
        Assert.Equal(JsonValueKind.Array, results.ValueKind)
        Assert.Equal(0, results.GetArrayLength())

    [<Fact>]
    let ``conflicts dir and glob scope resolve persisted docs when first chunk is not a usable anchor`` () =
        use harness =
            ConflictScopeHarness.Create(
                toDotBackslashPath,
                reshapePersistedIndex = (fun repoRoot -> makeFirstChunkUnusableConflictAnchor repoRoot "docs/canon/alpha.md"))

        use dirDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon', pairs:true, threshold:0.10})"))

        let dirResults = dirDoc.RootElement
        Assert.Equal(JsonValueKind.Array, dirResults.ValueKind)
        Assert.True(dirResults.GetArrayLength() > 0, dirResults.GetRawText())

        use globDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/*', pairs:true, threshold:0.10})"))

        let globResults = globDoc.RootElement
        Assert.Equal(JsonValueKind.Array, globResults.ValueKind)
        Assert.True(globResults.GetArrayLength() > 0, globResults.GetRawText())

    [<Fact>]
    let ``conflicts exact scope reports semantic unavailability when matched docs have zero persisted anchors`` () =
        use harness =
            ConflictScopeHarness.Create(
                toDotBackslashPath,
                reshapePersistedIndex = zeroPersistedConflictAnchors)

        use contextDoc = JsonDocument.Parse(harness.EvalJson("context('docs/canon/alpha.md')"))
        Assert.Equal("Alpha", (getRequiredProperty "title" contextDoc.RootElement).GetString())

        use conflictsDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/alpha.md', pairs:true, threshold:0.10})"))

        let error = getSingleError conflictsDoc.RootElement
        Assert.Equal("docs/canon/alpha.md", (getRequiredProperty "selector" error).GetString())
        Assert.Equal(
            "conflicts() scope selector 'docs/canon/alpha.md' matched indexed/supported docs but none have usable persisted semantic anchors in this wave",
            (getRequiredProperty "error" error).GetString())

    [<Fact>]
    let ``conflicts dir and glob scope report semantic unavailability when matched docs have zero persisted anchors`` () =
        use harness =
            ConflictScopeHarness.Create(
                toDotBackslashPath,
                reshapePersistedIndex = zeroPersistedConflictAnchors)

        use dirDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon', pairs:true, threshold:0.10})"))

        let dirError = getSingleError dirDoc.RootElement
        Assert.Equal("docs/canon", (getRequiredProperty "selector" dirError).GetString())
        Assert.Equal(
            "conflicts() scope selector 'docs/canon' matched indexed/supported docs but none have usable persisted semantic anchors in this wave",
            (getRequiredProperty "error" dirError).GetString())

        use globDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({scope:'docs/canon/*', pairs:true, threshold:0.10})"))

        let globError = getSingleError globDoc.RootElement
        Assert.Equal("docs/canon/*", (getRequiredProperty "selector" globError).GetString())
        Assert.Equal(
            "conflicts() scope selector 'docs/canon/*' matched indexed/supported docs but none have usable persisted semantic anchors in this wave",
            (getRequiredProperty "error" globError).GetString())

    [<Fact>]
    let ``conflicts without scope reports semantic unavailability when the indexed candidate surface has zero persisted anchors`` () =
        use harness =
            ConflictScopeHarness.Create(
                toDotBackslashPath,
                reshapePersistedIndex = zeroPersistedConflictAnchors)

        use conflictsDoc =
            JsonDocument.Parse(
                harness.EvalJson("conflicts({pairs:true, threshold:0.10})"))

        let error = getSingleError conflictsDoc.RootElement
        Assert.Equal(
            "conflicts() requires usable persisted semantic anchors in this wave; the indexed candidate surface has none",
            (getRequiredProperty "error" error).GetString())
