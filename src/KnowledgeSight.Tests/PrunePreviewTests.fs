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

module PrunePreviewTests =

    let private writeFile (filePath: string) (content: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)) |> ignore
        File.WriteAllText(filePath, content.TrimStart(), Encoding.UTF8)

    let private repoFile (repoRoot: string) (relativePath: string) =
        Path.Combine(repoRoot, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()))

    let private writeConfig (repoRoot: string) (embeddingUrl: string) =
        let config =
            {|
                docDirs = [| "docs"; "inbox" |]
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

    type private PruneHarness private (repoRoot: string, server: EmbeddingServer, engine: Jint.Engine) =
        member _.EvalJson (query: string) = QueryEngine.evalJson engine query
        member _.ReadFile (relativePath: string) = File.ReadAllText(repoFile repoRoot relativePath)
        member _.FileExists (relativePath: string) = File.Exists(repoFile repoRoot relativePath)
        member _.LockFile (relativePath: string) = new FileStream(repoFile repoRoot relativePath, FileMode.Open, FileAccess.Read, FileShare.None)

        interface IDisposable with
            member _.Dispose() =
                (server :> IDisposable).Dispose()
                try
                    Directory.Delete(repoRoot, true)
                with _ ->
                    ()

        static member Create() =
            let server = EmbeddingServer.Start()
            let repoRoot = Path.Combine(Path.GetTempPath(), sprintf "ks-prune-preview-tests-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(repoRoot) |> ignore
            writeConfig repoRoot server.EmbeddingUrl

            writeFile
                (repoFile repoRoot "docs/canon/stale-prunable.md")
                """
---
title: "Stale prunable"
status: "stale"
source: "canon"
---
Historical stale document with no incoming links.
"""

            writeFile
                (repoFile repoRoot "docs/canon/stale-linked.md")
                """
---
title: "Stale linked"
status: "stale"
source: "canon"
---
Historical stale document that is still linked from an active owner.
"""

            writeFile
                (repoFile repoRoot "docs/canon/history/superseded-prunable.md")
                """
---
title: "Superseded prunable"
status: "superseded"
source: "canon"
---
Superseded historical document that is old enough to preview as a candidate.
"""

            writeFile
                (repoFile repoRoot "docs/canon/history/deprecated-recent.md")
                """
---
title: "Deprecated recent"
status: "deprecated"
source: "canon"
---
Deprecated historical document that is still too recent for prune preview.
"""

            writeFile
                (repoFile repoRoot "docs/canon/history/stale-cascade-source.md")
                """
---
title: "Stale cascade source"
status: "stale"
source: "canon"
---
This stale source links to [cascade target](stale-cascade-target.md).
"""

            writeFile
                (repoFile repoRoot "docs/canon/history/stale-cascade-target.md")
                """
---
title: "Stale cascade target"
status: "stale"
source: "canon"
---
This stale target should stay blocked in the first apply pass.
"""

            writeFile
                (repoFile repoRoot "docs/canon/active-owner.md")
                """
---
title: "Active owner"
status: "active"
source: "canon"
---
This active owner still links to [stale linked](stale-linked.md).
"""

            writeFile
                (repoFile repoRoot "inbox/ops/pending-note.md")
                """
---
title: "Pending note"
status: "pending"
source: "inbox"
---
Pending inbox note that should never appear in prune preview.
"""

            let oldTime = DateTime.UtcNow.AddDays(-60.0)
            let recentTime = DateTime.UtcNow.AddDays(-5.0)

            for relativePath in [
                "docs/canon/stale-prunable.md"
                "docs/canon/stale-linked.md"
                "docs/canon/history/superseded-prunable.md"
                "docs/canon/history/stale-cascade-source.md"
                "docs/canon/history/stale-cascade-target.md"
                "docs/canon/active-owner.md"
                "inbox/ops/pending-note.md"
            ] do
                File.SetLastWriteTimeUtc(repoFile repoRoot relativePath, oldTime)

            File.SetLastWriteTimeUtc(repoFile repoRoot "docs/canon/history/deprecated-recent.md", recentTime)

            let cfg = Config.load repoRoot

            match IndexingWorkflow.rebuild cfg with
            | Error message ->
                (server :> IDisposable).Dispose()
                failwithf "Index build failed in prune preview harness: %s" message
            | Ok (index, chunks) ->
                let engine = QueryEngine.create cfg index chunks
                new PruneHarness(repoRoot, server, engine)

    let private getRequiredProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> value
        | _ -> failwithf "Missing property '%s'" name

    let private getStringArray (name: string) (element: JsonElement) =
        getRequiredProperty name element
        |> fun value -> value.EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Seq.toArray

    let private getPaths (root: JsonElement) =
        root.EnumerateArray()
        |> Seq.choose (fun item ->
            match item.TryGetProperty("path") with
            | true, value when value.ValueKind = JsonValueKind.String -> Some (value.GetString())
            | _ -> None)
        |> Seq.toArray

    let private findByPath (path: string) (root: JsonElement) =
        root.EnumerateArray()
        |> Seq.find (fun item ->
            match item.TryGetProperty("path") with
            | true, value -> String.Equals(value.GetString(), path, StringComparison.Ordinal)
            | _ -> false)

    [<Fact>]
    let ``prune preview stays read only and reports candidates plus blockers for non live canonical docs only`` () =
        use harness = PruneHarness.Create()
        let stalePrunableBefore = harness.ReadFile("docs/canon/stale-prunable.md")
        let staleLinkedBefore = harness.ReadFile("docs/canon/stale-linked.md")
        let supersededBefore = harness.ReadFile("docs/canon/history/superseded-prunable.md")

        use pruneDoc = JsonDocument.Parse(harness.EvalJson("prune({olderThanDays:30})"))

        let results = pruneDoc.RootElement
        Assert.Equal(JsonValueKind.Array, results.ValueKind)

        let paths = getPaths results |> Array.sort
        Assert.Equal<string[]>(
            [|
                "docs/canon/history/deprecated-recent.md"
                "docs/canon/history/stale-cascade-source.md"
                "docs/canon/history/stale-cascade-target.md"
                "docs/canon/history/superseded-prunable.md"
                "docs/canon/stale-linked.md"
                "docs/canon/stale-prunable.md"
            |],
            paths)

        Assert.DoesNotContain("docs/canon/active-owner.md", paths)
        Assert.DoesNotContain("inbox/ops/pending-note.md", paths)

        let stalePrunable = findByPath "docs/canon/stale-prunable.md" results
        Assert.Equal("candidate", (getRequiredProperty "outcome" stalePrunable).GetString())
        Assert.True((getRequiredProperty "eligible" stalePrunable).GetBoolean())
        Assert.True((getRequiredProperty "ageGuardPassed" stalePrunable).GetBoolean())
        Assert.True((getRequiredProperty "backlinkGuardPassed" stalePrunable).GetBoolean())
        Assert.Equal(0, (getRequiredProperty "backlinkCount" stalePrunable).GetInt32())

        let staleLinked = findByPath "docs/canon/stale-linked.md" results
        Assert.Equal("blocked", (getRequiredProperty "outcome" staleLinked).GetString())
        Assert.False((getRequiredProperty "eligible" staleLinked).GetBoolean())
        Assert.True((getRequiredProperty "ageGuardPassed" staleLinked).GetBoolean())
        Assert.False((getRequiredProperty "backlinkGuardPassed" staleLinked).GetBoolean())
        Assert.Equal(1, (getRequiredProperty "backlinkCount" staleLinked).GetInt32())
        Assert.Equal<string[]>([| "docs/canon/active-owner.md" |], getStringArray "backlinks" staleLinked)

        let cascadeTarget = findByPath "docs/canon/history/stale-cascade-target.md" results
        Assert.Equal("blocked", (getRequiredProperty "outcome" cascadeTarget).GetString())
        Assert.False((getRequiredProperty "eligible" cascadeTarget).GetBoolean())
        Assert.Equal<string[]>([| "docs/canon/history/stale-cascade-source.md" |], getStringArray "backlinks" cascadeTarget)

        let deprecatedRecent = findByPath "docs/canon/history/deprecated-recent.md" results
        Assert.Equal("blocked", (getRequiredProperty "outcome" deprecatedRecent).GetString())
        Assert.False((getRequiredProperty "eligible" deprecatedRecent).GetBoolean())
        Assert.False((getRequiredProperty "ageGuardPassed" deprecatedRecent).GetBoolean())
        Assert.True((getRequiredProperty "backlinkGuardPassed" deprecatedRecent).GetBoolean())

        let supersededPrunable = findByPath "docs/canon/history/superseded-prunable.md" results
        Assert.Equal("candidate", (getRequiredProperty "outcome" supersededPrunable).GetString())
        Assert.True((getRequiredProperty "eligible" supersededPrunable).GetBoolean())

        Assert.True(harness.FileExists("docs/canon/stale-prunable.md"))
        Assert.True(harness.FileExists("docs/canon/stale-linked.md"))
        Assert.True(harness.FileExists("docs/canon/history/superseded-prunable.md"))
        Assert.Equal(stalePrunableBefore, harness.ReadFile("docs/canon/stale-prunable.md"))
        Assert.Equal(staleLinkedBefore, harness.ReadFile("docs/canon/stale-linked.md"))
        Assert.Equal(supersededBefore, harness.ReadFile("docs/canon/history/superseded-prunable.md"))

    [<Fact>]
    let ``prune apply false preserves shipped preview output`` () =
        use harness = PruneHarness.Create()

        let preview = harness.EvalJson("prune({olderThanDays:30})")
        let explicitPreview = harness.EvalJson("prune({olderThanDays:30, apply:false})")

        Assert.Equal(preview, explicitPreview)

    [<Fact>]
    let ``prune apply deletes only initially eligible docs and refreshes state without cascade`` () =
        use harness = PruneHarness.Create()

        use applyDoc = JsonDocument.Parse(harness.EvalJson("prune({olderThanDays:30, apply:true})"))
        let applied = applyDoc.RootElement

        let stalePrunable = findByPath "docs/canon/stale-prunable.md" applied
        Assert.Equal("deleted", (getRequiredProperty "outcome" stalePrunable).GetString())
        Assert.Equal("candidate", (getRequiredProperty "previewOutcome" stalePrunable).GetString())

        let superseded = findByPath "docs/canon/history/superseded-prunable.md" applied
        Assert.Equal("deleted", (getRequiredProperty "outcome" superseded).GetString())

        let cascadeSource = findByPath "docs/canon/history/stale-cascade-source.md" applied
        Assert.Equal("deleted", (getRequiredProperty "outcome" cascadeSource).GetString())

        let cascadeTarget = findByPath "docs/canon/history/stale-cascade-target.md" applied
        Assert.Equal("blocked", (getRequiredProperty "outcome" cascadeTarget).GetString())
        Assert.Equal("blocked", (getRequiredProperty "previewOutcome" cascadeTarget).GetString())

        let staleLinked = findByPath "docs/canon/stale-linked.md" applied
        Assert.Equal("blocked", (getRequiredProperty "outcome" staleLinked).GetString())

        let deprecatedRecent = findByPath "docs/canon/history/deprecated-recent.md" applied
        Assert.Equal("blocked", (getRequiredProperty "outcome" deprecatedRecent).GetString())

        Assert.False(harness.FileExists("docs/canon/stale-prunable.md"))
        Assert.False(harness.FileExists("docs/canon/history/superseded-prunable.md"))
        Assert.False(harness.FileExists("docs/canon/history/stale-cascade-source.md"))
        Assert.True(harness.FileExists("docs/canon/history/stale-cascade-target.md"))
        Assert.True(harness.FileExists("docs/canon/stale-linked.md"))
        Assert.True(harness.FileExists("docs/canon/active-owner.md"))
        Assert.True(harness.FileExists("inbox/ops/pending-note.md"))

        use previewAfterDoc = JsonDocument.Parse(harness.EvalJson("prune({olderThanDays:30})"))
        let previewAfter = previewAfterDoc.RootElement
        let remainingPaths = getPaths previewAfter |> Array.sort
        Assert.Equal<string[]>(
            [|
                "docs/canon/history/deprecated-recent.md"
                "docs/canon/history/stale-cascade-target.md"
                "docs/canon/stale-linked.md"
            |],
            remainingPaths)

        let refreshedCascadeTarget = findByPath "docs/canon/history/stale-cascade-target.md" previewAfter
        Assert.Equal("candidate", (getRequiredProperty "outcome" refreshedCascadeTarget).GetString())

    [<Fact>]
    let ``prune apply exact scope reports explicit delete failure when target cannot be deleted`` () =
        use harness = PruneHarness.Create()
        use lockStream = harness.LockFile("docs/canon/stale-prunable.md")

        use applyDoc =
            JsonDocument.Parse(
                harness.EvalJson("prune({scope:'docs/canon/stale-prunable.md', olderThanDays:30, apply:true})"))

        let results = applyDoc.RootElement
        let row = findByPath "docs/canon/stale-prunable.md" results
        Assert.Equal("error", (getRequiredProperty "outcome" row).GetString())
        Assert.Equal("candidate", (getRequiredProperty "previewOutcome" row).GetString())
        Assert.Contains("could not delete target", (getRequiredProperty "error" row).GetString())
        Assert.True(harness.FileExists("docs/canon/stale-prunable.md"))

    [<Fact>]
    let ``prune apply dir and glob scope reuse canonical selector behavior`` () =
        let assertHistoryApply (query: string) =
            use harness = PruneHarness.Create()
            use applyDoc = JsonDocument.Parse(harness.EvalJson(query))
            let results = applyDoc.RootElement
            let paths = getPaths results |> Array.sort
            Assert.Equal<string[]>(
                [|
                    "docs/canon/history/deprecated-recent.md"
                    "docs/canon/history/stale-cascade-source.md"
                    "docs/canon/history/stale-cascade-target.md"
                    "docs/canon/history/superseded-prunable.md"
                |],
                paths)
            Assert.Equal("deleted", (getRequiredProperty "outcome" (findByPath "docs/canon/history/stale-cascade-source.md" results)).GetString())
            Assert.Equal("deleted", (getRequiredProperty "outcome" (findByPath "docs/canon/history/superseded-prunable.md" results)).GetString())
            Assert.Equal("blocked", (getRequiredProperty "outcome" (findByPath "docs/canon/history/stale-cascade-target.md" results)).GetString())
            Assert.Equal("blocked", (getRequiredProperty "outcome" (findByPath "docs/canon/history/deprecated-recent.md" results)).GetString())
            Assert.True(harness.FileExists("docs/canon/stale-prunable.md"))

        assertHistoryApply("prune({scope:'docs/canon/history', olderThanDays:30, apply:true})")
        assertHistoryApply("prune({scope:'docs/canon/history/*.md', olderThanDays:30, apply:true})")
