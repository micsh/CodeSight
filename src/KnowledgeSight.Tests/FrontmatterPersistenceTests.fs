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

module FrontmatterPersistenceTests =

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

    type private FrontmatterHarness private (repoRoot: string, relativeDocPath: string, server: EmbeddingServer, engine: Jint.Engine) =
        member _.EvalJson (query: string) = QueryEngine.evalJson engine query
        member _.DeleteSourceMarkdown() = File.Delete(Path.Combine(repoRoot, relativeDocPath.Replace("/", Path.DirectorySeparatorChar.ToString())))
        member _.RelativeDocPath = relativeDocPath

        interface IDisposable with
            member _.Dispose() =
                (server :> IDisposable).Dispose()
                try
                    Directory.Delete(repoRoot, true)
                with _ ->
                    ()

        static member Create() =
            let server = EmbeddingServer.Start()
            let repoRoot = Path.Combine(Path.GetTempPath(), sprintf "ks-frontmatter-tests-%s" (Guid.NewGuid().ToString("N")))
            let relativeDocPath = "docs/roadmap/frontmatter-queue.md"
            Directory.CreateDirectory(repoRoot) |> ignore
            writeConfig repoRoot server.EmbeddingUrl

            writeFile
                (Path.Combine(repoRoot, "docs", "roadmap", "frontmatter-queue.md"))
                """
---
title: "Frontmatter queue"
status: "pending"
tags:
  - epistemic
  - v0
related:
  - src/Engine/Loop.fs
  - src/Engine/Queue.fs
source: "ops"
cycle: "2026-05-08T00-00-00Z"
concept: "frontmatter survival"
observable:
  - context(file)
  - explain(refId)
forbids:
  - markdown reparse
suggested_target: "docs/canon/frontmatter-queue.md"
---
Epistemic prerequisite queue state for indexed frontmatter survival.
"""

            let cfg = Config.load repoRoot

            match IndexingWorkflow.rebuild cfg with
            | Error message ->
                (server :> IDisposable).Dispose()
                failwithf "Index build failed in test harness: %s" message
            | Ok _ ->
                match IndexStore.load cfg.IndexDir with
                | None ->
                    (server :> IDisposable).Dispose()
                    failwith "Persisted index reload failed in test harness."
                | Some reloadedIndex ->
                    let engine = QueryEngine.create cfg reloadedIndex None
                    new FrontmatterHarness(repoRoot, relativeDocPath, server, engine)

    let private getRequiredProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> value
        | _ -> failwithf "Missing property '%s'" name

    let private getStringArray (name: string) (element: JsonElement) =
        getRequiredProperty name element
        |> fun value ->
            value.EnumerateArray()
            |> Seq.map (fun item -> item.GetString())
            |> Seq.toArray

    let private assertExpectedFrontmatterMap (root: JsonElement) =
        Assert.Equal("index", (getRequiredProperty "frontmatterSource" root).GetString())
        let frontmatter = getRequiredProperty "frontmatter" root

        Assert.Equal("Frontmatter queue", (getRequiredProperty "title" frontmatter).GetString())
        Assert.Equal("pending", (getRequiredProperty "status" frontmatter).GetString())
        Assert.Equal("ops", (getRequiredProperty "source" frontmatter).GetString())
        Assert.Equal("2026-05-08T00-00-00Z", (getRequiredProperty "cycle" frontmatter).GetString())
        Assert.Equal("frontmatter survival", (getRequiredProperty "concept" frontmatter).GetString())
        Assert.Equal("docs/canon/frontmatter-queue.md", (getRequiredProperty "suggested_target" frontmatter).GetString())
        Assert.Equal<string[]>([| "epistemic"; "v0" |], getStringArray "tags" frontmatter)
        Assert.Equal<string[]>([| "src/Engine/Loop.fs"; "src/Engine/Queue.fs" |], getStringArray "related" frontmatter)
        Assert.Equal<string[]>([| "context(file)"; "explain(refId)" |], getStringArray "observable" frontmatter)
        Assert.Equal<string[]>([| "markdown reparse" |], getStringArray "forbids" frontmatter)

    [<Fact>]
    let ``context returns indexed arbitrary frontmatter after reload without reopening markdown`` () =
        use harness = FrontmatterHarness.Create()
        harness.DeleteSourceMarkdown()

        use doc = JsonDocument.Parse(harness.EvalJson(sprintf "context('%s')" harness.RelativeDocPath))
        let root = doc.RootElement

        Assert.Equal("Frontmatter queue", (getRequiredProperty "title" root).GetString())
        Assert.Equal("pending", (getRequiredProperty "status" root).GetString())
        Assert.Equal("src/Engine/Loop.fs, src/Engine/Queue.fs", (getRequiredProperty "related" root).GetString())
        assertExpectedFrontmatterMap root
        Assert.True((getRequiredProperty "sections" root).GetArrayLength() > 0)

    [<Fact>]
    let ``explain returns indexed arbitrary frontmatter after reload without source chunks`` () =
        use harness = FrontmatterHarness.Create()
        harness.DeleteSourceMarkdown()

        use searchDoc = JsonDocument.Parse(harness.EvalJson(sprintf "search('frontmatter survival', {limit: 1, file: '%s', status:['pending']})" harness.RelativeDocPath))
        let searchResults = searchDoc.RootElement
        Assert.Equal(JsonValueKind.Array, searchResults.ValueKind)
        Assert.Equal(1, searchResults.GetArrayLength())

        let refId = (getRequiredProperty "id" searchResults[0]).GetString()

        use explainDoc = JsonDocument.Parse(harness.EvalJson(sprintf "explain('%s')" refId))
        let root = explainDoc.RootElement

        assertExpectedFrontmatterMap root
        Assert.Equal("source chunks not loaded", (getRequiredProperty "sourceMatch" root).GetString())
        Assert.EndsWith(harness.RelativeDocPath, (getRequiredProperty "filePath" root).GetString().Replace("\\", "/"), StringComparison.Ordinal)
