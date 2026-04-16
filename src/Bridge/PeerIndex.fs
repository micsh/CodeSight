namespace AITeam.CodeSight.Bridge

open System
open System.IO
open System.Text.Json

/// Lightweight types for the KnowledgeSight peer index. No dependency on KS project.
[<AutoOpen>]
module PeerTypes =
    type KsChunkEntry = {
        FilePath: string
        Heading: string
        HeadingPath: string
        Summary: string
        Tags: string
        StartLine: int
        EndLine: int
    }

    type KsSourceChunk = {
        FilePath: string
        Heading: string
        Content: string
    }

    type KsPeerIndex = {
        Chunks: KsChunkEntry[]
        SourceChunks: KsSourceChunk[]
    }

/// Reads KnowledgeSight index files from disk. Metadata-only — no embeddings.
module PeerIndex =

    /// Discover the KS index directory. Checks override path first, then sibling of repo root.
    let discover (repoRoot: string) (peerOverride: string option) =
        match peerOverride with
        | Some p ->
            let dir = if Directory.Exists p && File.Exists(Path.Combine(p, "chunks.tsv")) then Some p
                      // If they gave us a repo root, check for .knowledge-sight/ under it
                      else
                          let ksDir = Path.Combine(p, ".knowledge-sight")
                          if Directory.Exists ksDir && File.Exists(Path.Combine(ksDir, "chunks.tsv")) then Some ksDir
                          else None
            dir
        | None ->
            let ksDir = Path.Combine(repoRoot, ".knowledge-sight")
            if Directory.Exists ksDir && File.Exists(Path.Combine(ksDir, "chunks.tsv")) then
                Some ksDir
            else None

    /// Load KS chunks.tsv (metadata only).
    let loadChunks (ksDir: string) =
        let path = Path.Combine(ksDir, "chunks.tsv")
        if not (File.Exists path) then [||]
        else
            let allLines = File.ReadAllLines(path)
            let dataLines =
                if allLines.Length > 0 && allLines.[0].StartsWith("#fields:") then allLines.[1..]
                else allLines
            dataLines |> Array.choose (fun line ->
                let p = line.Split('\t')
                if p.Length >= 10 then
                    Some { FilePath = p.[0]; Heading = p.[1]; HeadingPath = p.[2]
                           Summary = p.[6]; Tags = p.[7]
                           StartLine = int p.[4]; EndLine = int p.[5] }
                else None)

    /// Load KS source-chunks.jsonl (content for lexical matching).
    let loadSourceChunks (ksDir: string) =
        let path = Path.Combine(ksDir, "source-chunks.jsonl")
        if not (File.Exists path) then [||]
        else
            File.ReadAllLines(path) |> Array.choose (fun line ->
                try
                    let doc = JsonDocument.Parse(line)
                    let r = doc.RootElement
                    let str (p: string) = match r.TryGetProperty(p) with true, v -> v.GetString() | _ -> ""
                    Some { FilePath = str "filePath"; Heading = str "heading"; Content = str "content" }
                with _ -> None)

    /// Load the full peer index (lazy, called once).
    let load (ksDir: string) =
        { Chunks = loadChunks ksDir
          SourceChunks = loadSourceChunks ksDir }
