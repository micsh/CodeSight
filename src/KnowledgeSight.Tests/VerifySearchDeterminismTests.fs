namespace AITeam.KnowledgeSight.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open AITeam.KnowledgeSight

module VerifySearchDeterminismTests =

    let private writeFile (filePath: string) (content: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)) |> ignore
        File.WriteAllText(filePath, content.TrimStart(), Encoding.UTF8)

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

    let private jsStringLiteral (value: string) = JsonSerializer.Serialize(value)

    type private EmbeddingServer private (listener: HttpListener, loopTask: Task, port: int, requestCount: int ref) =
        member _.EmbeddingUrl = sprintf "http://127.0.0.1:%d/v1/embeddings" port
        member _.RequestCount = requestCount.Value
        member _.Stop() =
            if listener.IsListening then
                listener.Stop()

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

            let requestCount = ref 0

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
                                        requestCount.Value <- requestCount.Value + 1

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

            new EmbeddingServer(listener, loopTask, port, requestCount)

    type private VerifyHarness private (repoRoot: string, server: EmbeddingServer, engine: Jint.Engine) =
        member _.EvalJson (query: string) = QueryEngine.evalJson engine query
        member _.EmbeddingRequestCount = server.RequestCount
        member _.StopEmbeddingServer() = server.Stop()
        member _.FileExists (relativePath: string) =
            File.Exists(Path.Combine(repoRoot, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString())))
        member _.ReadFile (relativePath: string) =
            File.ReadAllText(Path.Combine(repoRoot, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString())))

        interface IDisposable with
            member _.Dispose() =
                (server :> IDisposable).Dispose()
                try
                    Directory.Delete(repoRoot, true)
                with _ ->
                    ()

        static member Create(seedRepo: string -> unit) =
            let server = EmbeddingServer.Start()
            let repoRoot = Path.Combine(Path.GetTempPath(), sprintf "ks-verify-search-tests-%s" (Guid.NewGuid().ToString("N")))
            Directory.CreateDirectory(repoRoot) |> ignore
            writeConfig repoRoot server.EmbeddingUrl
            seedRepo repoRoot

            let cfg = Config.load repoRoot

            match IndexingWorkflow.rebuild cfg with
            | Error message ->
                (server :> IDisposable).Dispose()
                failwithf "Index build failed in test harness: %s" message
            | Ok (index, chunks) ->
                let engine = QueryEngine.create cfg index chunks
                new VerifyHarness(repoRoot, server, engine)

    let private getRequiredProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> value
        | _ -> failwithf "Missing property '%s'" name

    [<Fact>]
    let ``supersede persists deterministic verify search cache and reverify reuses it without live embeddings`` () =
        let seedRepo repoRoot =
            writeFile
                (Path.Combine(repoRoot, "docs", "canon", "verify-alpha.md"))
                """
---
title: "Verify alpha"
status: "active"
source: "canon"
---
Deterministic verify search coverage for active canonical docs.
"""

        use harness = VerifyHarness.Create(seedRepo)

        let searchQuery = "search('deterministic verify search', {limit: 1, file: 'docs/canon/verify-alpha.md', status:['active']})"
        use searchDoc = JsonDocument.Parse(harness.EvalJson(searchQuery))

        let searchResults = searchDoc.RootElement
        Assert.Equal(JsonValueKind.Array, searchResults.ValueKind)
        Assert.Equal(1, searchResults.GetArrayLength())

        let refId = (getRequiredProperty "id" searchResults[0]).GetString()
        let newPath = "docs/canon/verify-alpha-v2.md"
        let verifyExpr = sprintf "search('deterministic verify search', {limit: 1, file: '%s', status:['active']}).length === 1" newPath
        let supersedeQuery =
            sprintf "supersede(%s, %s, {reason:'refresh deterministic verify search', by:'ops', verify:%s})"
                (jsStringLiteral refId)
                (jsStringLiteral "Deterministic verify search coverage for the replacement canonical doc.\n")
                (jsStringLiteral verifyExpr)
        use supersedeDoc = JsonDocument.Parse(harness.EvalJson(supersedeQuery))

        let supersedeRoot = supersedeDoc.RootElement
        Assert.Equal("supersede", (getRequiredProperty "action" supersedeRoot).GetString())
        Assert.Equal(newPath, (getRequiredProperty "newPath" supersedeRoot).GetString())

        let contextQuery = sprintf "context(%s)" (jsStringLiteral newPath)
        use contextDoc = JsonDocument.Parse(harness.EvalJson(contextQuery))
        let frontmatter = getRequiredProperty "frontmatter" contextDoc.RootElement
        let persistedCache = (getRequiredProperty "verify_search_cache" frontmatter).GetString()
        Assert.False(String.IsNullOrWhiteSpace(persistedCache))

        let requestsBeforeReverify = harness.EmbeddingRequestCount
        let reverifyQuery = sprintf "reverify({scope:[%s]})" (jsStringLiteral newPath)
        use reverifyDoc = JsonDocument.Parse(harness.EvalJson(reverifyQuery))

        Assert.Equal(requestsBeforeReverify, harness.EmbeddingRequestCount)

        let reverifyResults = reverifyDoc.RootElement
        Assert.Equal(JsonValueKind.Array, reverifyResults.ValueKind)
        Assert.Equal(1, reverifyResults.GetArrayLength())
        let outcome = (getRequiredProperty "outcome" reverifyResults[0]).GetString()
        Assert.True(String.Equals(outcome, "ok", StringComparison.Ordinal), reverifyResults.GetRawText())

    [<Fact>]
    let ``reverify errors when search verify lacks persisted deterministic cache and never falls back live`` () =
        let seedRepo repoRoot =
            writeFile
                (Path.Combine(repoRoot, "docs", "canon", "legacy-search.md"))
                """
---
title: "Legacy search verify"
status: "active"
source: "canon"
verify: "search('legacy deterministic verify', {file:'docs/canon/legacy-search.md', status:['active']}).length === 1"
verify_snapshot: "legacy-snapshot"
---
Legacy deterministic verify search doc.
"""

        use harness = VerifyHarness.Create(seedRepo)

        let requestsBeforeReverify = harness.EmbeddingRequestCount
        let reverifyQuery = "reverify({scope:['docs/canon/legacy-search.md']})"
        use reverifyDoc = JsonDocument.Parse(harness.EvalJson(reverifyQuery))

        Assert.Equal(requestsBeforeReverify, harness.EmbeddingRequestCount)

        let result = reverifyDoc.RootElement[0]
        Assert.Equal("error", (getRequiredProperty "outcome" result).GetString())
        Assert.Contains(
            "persisted deterministic query embeddings",
            (getRequiredProperty "error" result).GetString(),
            StringComparison.OrdinalIgnoreCase)

    [<Fact>]
    let ``reverify keeps novelty outside the verify sandbox`` () =
        let seedRepo repoRoot =
            writeFile
                (Path.Combine(repoRoot, "docs", "canon", "novelty-verify.md"))
                """
---
title: "Novelty verify"
status: "active"
source: "canon"
verify: "novelty('verify novelty', {status:['active']}).length === 0"
verify_snapshot: "legacy-snapshot"
---
Novelty should remain outside the deterministic verify sandbox.
"""

        use harness = VerifyHarness.Create(seedRepo)

        let requestsBeforeReverify = harness.EmbeddingRequestCount
        let reverifyQuery = "reverify({scope:['docs/canon/novelty-verify.md']})"
        use reverifyDoc = JsonDocument.Parse(harness.EvalJson(reverifyQuery))

        Assert.Equal(requestsBeforeReverify, harness.EmbeddingRequestCount)

        let result = reverifyDoc.RootElement[0]
        Assert.Equal("error", (getRequiredProperty "outcome" result).GetString())
        Assert.Contains("novelty", (getRequiredProperty "error" result).GetString(), StringComparison.OrdinalIgnoreCase)

    [<Fact>]
    let ``promote fails and leaves inbox pending when deterministic search capture is unavailable`` () =
        let inboxPath = "inbox/ops/2026-05-08T00-00-00Z-promote-search.md"
        let targetPath = "docs/canon/promoted-search.md"

        let seedRepo repoRoot =
            writeFile
                (Path.Combine(repoRoot, inboxPath.Replace("/", Path.DirectorySeparatorChar.ToString())))
                """
---
title: "Promote search"
status: "pending"
source: "ops"
cycle: "2026-05-08T00-00-00Z"
---
Promote deterministic search capture should fail closed.
"""

        use harness = VerifyHarness.Create(seedRepo)
        use triageDoc = JsonDocument.Parse(harness.EvalJson("triage({team:'ops'})"))
        let inboxRef = (getRequiredProperty "id" triageDoc.RootElement[0]).GetString()

        harness.StopEmbeddingServer()

        let verifyExpr = "search('promote deterministic search', {file:'docs/canon/promoted-search.md', status:['active']}).length === 1"
        let disposeQuery =
            sprintf "dispose(%s, {action:'promote', target:%s, verify:%s})"
                (jsStringLiteral inboxRef)
                (jsStringLiteral targetPath)
                (jsStringLiteral verifyExpr)
        use disposeDoc = JsonDocument.Parse(harness.EvalJson(disposeQuery))

        let error = (getRequiredProperty "error" disposeDoc.RootElement).GetString()
        Assert.Contains("cannot capture deterministic query embeddings", error, StringComparison.OrdinalIgnoreCase)
        Assert.False(harness.FileExists(targetPath))
        Assert.True(harness.FileExists(inboxPath))

    [<Fact>]
    let ``merge fails and leaves canonical target unchanged when deterministic search capture is unavailable`` () =
        let inboxPath = "inbox/ops/2026-05-08T00-00-00Z-merge-search.md"
        let targetPath = "docs/canon/merge-target.md"

        let seedRepo repoRoot =
            writeFile
                (Path.Combine(repoRoot, targetPath.Replace("/", Path.DirectorySeparatorChar.ToString())))
                """
---
title: "Merge target"
status: "active"
source: "canon"
verify: "search('merge deterministic search', {file:'docs/canon/merge-target.md', status:['active']}).length === 1"
verify_snapshot: "legacy-snapshot"
---
Canonical merge target body.
"""

            writeFile
                (Path.Combine(repoRoot, inboxPath.Replace("/", Path.DirectorySeparatorChar.ToString())))
                """
---
title: "Merge search"
status: "pending"
source: "ops"
cycle: "2026-05-08T00-00-00Z"
---
Merged corroboration should not land when capture fails.
"""

        use harness = VerifyHarness.Create(seedRepo)
        let baselineTarget = harness.ReadFile(targetPath)
        use triageDoc = JsonDocument.Parse(harness.EvalJson("triage({team:'ops'})"))
        let inboxRef = (getRequiredProperty "id" triageDoc.RootElement[0]).GetString()

        harness.StopEmbeddingServer()

        let disposeQuery =
            sprintf "dispose(%s, {action:'merge', target:%s})"
                (jsStringLiteral inboxRef)
                (jsStringLiteral targetPath)
        use disposeDoc = JsonDocument.Parse(harness.EvalJson(disposeQuery))

        let error = (getRequiredProperty "error" disposeDoc.RootElement).GetString()
        Assert.Contains("cannot capture deterministic query embeddings", error, StringComparison.OrdinalIgnoreCase)
        Assert.Equal(baselineTarget, harness.ReadFile(targetPath))
        Assert.True(harness.FileExists(inboxPath))

    [<Fact>]
    let ``supersede fails and leaves original active doc in place when deterministic search capture is unavailable`` () =
        let oldPath = "docs/canon/supersede-search.md"
        let newPath = "docs/canon/supersede-search-v2.md"

        let seedRepo repoRoot =
            writeFile
                (Path.Combine(repoRoot, oldPath.Replace("/", Path.DirectorySeparatorChar.ToString())))
                """
---
title: "Supersede search"
status: "active"
source: "canon"
---
Supersede deterministic search capture should fail closed.
"""

        use harness = VerifyHarness.Create(seedRepo)
        let searchQuery = "search('supersede deterministic search', {limit: 1, file: 'docs/canon/supersede-search.md', status:['active']})"
        use searchDoc = JsonDocument.Parse(harness.EvalJson(searchQuery))
        let refId = (getRequiredProperty "id" searchDoc.RootElement[0]).GetString()

        harness.StopEmbeddingServer()

        let verifyExpr = "search('supersede deterministic search', {file:'docs/canon/supersede-search-v2.md', status:['active']}).length === 1"
        let supersedeQuery =
            sprintf "supersede(%s, %s, {reason:'bounded correction', by:'ops', verify:%s})"
                (jsStringLiteral refId)
                (jsStringLiteral "Replacement content that should not land.\n")
                (jsStringLiteral verifyExpr)
        use supersedeDoc = JsonDocument.Parse(harness.EvalJson(supersedeQuery))

        let error = (getRequiredProperty "error" supersedeDoc.RootElement).GetString()
        Assert.Contains("cannot capture deterministic query embeddings", error, StringComparison.OrdinalIgnoreCase)
        Assert.False(harness.FileExists(newPath))
        Assert.True(harness.FileExists(oldPath))

        let contextQuery = sprintf "context(%s)" (jsStringLiteral oldPath)
        use contextDoc = JsonDocument.Parse(harness.EvalJson(contextQuery))
        Assert.Equal("active", (getRequiredProperty "status" contextDoc.RootElement).GetString())
