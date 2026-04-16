namespace AITeam.CodeSight.Bridge

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open AITeam.CodeSight

/// L1 bridge primitives: drift() and coverage().
module BridgePrimitives =

    let private mdict (pairs: (string * obj) list) =
        let d = Dictionary<string, obj>()
        for (k, v) in pairs do d.[k] <- v
        d

    /// Normalize a file path to repo-relative with forward slashes.
    let private normalizePath (repoRoot: string) (filePath: string) =
        let full = if Path.IsPathRooted filePath then filePath else Path.Combine(repoRoot, filePath)
        let rel =
            if full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase) then
                full.Substring(repoRoot.Length).TrimStart('\\', '/')
            else filePath
        rel.Replace('\\', '/')

    /// Build a set of all CS symbol names for matching against KS content.
    /// Returns (symbolName → list of ChunkEntry) for resolution checking.
    let private buildSymbolIndex (index: CodeIndex) =
        let symbols = Dictionary<string, ResizeArray<ChunkEntry>>(StringComparer.OrdinalIgnoreCase)
        let add key entry =
            match symbols.TryGetValue(key) with
            | true, list -> list.Add(entry)
            | _ ->
                let list = ResizeArray()
                list.Add(entry)
                symbols.[key] <- list
        for c in index.Chunks do
            // Add by name (e.g., "parseAll", "Orchestrator")
            if c.Name.Length >= 3 then add c.Name c
            // Add by Module.Name (e.g., "Parser.parseAll")
            if c.Module <> "" && c.Name <> "" then add (sprintf "%s.%s" c.Module c.Name) c
            // Add by file basename (e.g., "Orchestrator.fs")
            let baseName = Path.GetFileName(c.FilePath)
            if baseName.Length >= 3 then add baseName c
            // Add by file stem (e.g., "Orchestrator")
            let stem = Path.GetFileNameWithoutExtension(c.FilePath)
            if stem.Length >= 3 then add stem c
        symbols

    /// Check which CS symbols are mentioned in a KS section's content.
    let private findMentions (symbolIndex: Dictionary<string, ResizeArray<ChunkEntry>>) (content: string) =
        let found = ResizeArray<string>()
        for kv in symbolIndex do
            let symbol = kv.Key
            // Skip very short symbols to avoid noise (e.g., "fs", "Id")
            if symbol.Length >= 3 then
                // Word-boundary check: symbol must appear as a distinct token
                let pattern = sprintf @"\b%s\b" (Regex.Escape(symbol))
                if Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase) then
                    found.Add(symbol)
        found |> Seq.toArray

    /// Check which CS symbols are mentioned but no longer resolve in the CS index.
    /// This extracts symbol-like tokens from KS content and checks them against the CS index.
    let private findDriftingSymbols (index: CodeIndex) (content: string) =
        let csFileNames =
            let names = index.Chunks |> Array.map (fun c -> Path.GetFileName(c.FilePath)) |> Array.distinct
            HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
        let csNames =
            let names = index.Chunks |> Array.map (fun c -> c.Name) |> Array.filter (fun n -> n.Length >= 3) |> Array.distinct
            HashSet<string>(names, StringComparer.OrdinalIgnoreCase)

        let missing = ResizeArray<string>()

        // Check file references: word.ext patterns
        let fileRefs = Regex.Matches(content, @"\b(\w[\w.-]*\.(?:fs|fsx|cs|ts|tsx|js|jsx|py|go|rs|java|kt))\b")
        for m in fileRefs do
            let name = m.Groups.[1].Value
            if not (csFileNames.Contains(name)) then
                missing.Add(name)

        // Check qualified names: Module.Member (both PascalCase)
        let qualRefs = Regex.Matches(content, @"\b([A-Z]\w+\.\w{3,})\b")
        for m in qualRefs do
            let name = m.Groups.[1].Value
            let parts = name.Split('.')
            if parts.Length = 2 then
                let modulePart = parts.[0]
                let memberPart = parts.[1]
                let moduleExists = csNames.Contains(modulePart) || csFileNames.Contains(modulePart + ".fs") || csFileNames.Contains(modulePart + ".cs")
                let memberExists = csNames.Contains(memberPart)
                if not moduleExists && not memberExists then
                    missing.Add(name)

        missing |> Seq.distinct |> Seq.toArray

    // ── drift() ──

    /// Find KS sections that reference CS symbols which no longer exist in the codebase.
    let drift (index: CodeIndex) (peer: KsPeerIndex) =
        let results = ResizeArray<Dictionary<string, obj>>()
        for sc in peer.SourceChunks do
            let missing = findDriftingSymbols index sc.Content
            if missing.Length > 0 then
                results.Add(mdict [
                    "file", box (sc.FilePath.Replace('\\', '/'))
                    "heading", box sc.Heading
                    "missingSymbols", box (missing |> String.concat ", ")
                    "count", box missing.Length
                ])
        results.ToArray()

    // ── coverage() ──

    /// Find important CS files (by fanIn) that have no documentation in KS.
    let coverage (index: CodeIndex) (peer: KsPeerIndex) (minFanIn: int) =
        // Build a set of all text content from KS for matching
        let allKsContent =
            peer.SourceChunks
            |> Array.map (fun sc -> sc.Content)
            |> String.concat "\n"

        // Build the symbol index from CS
        let symbolIndex = buildSymbolIndex index

        // Determine which CS files are "documented" (any symbol from that file appears in KS)
        let documentedFiles = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let fileSymbols = Dictionary<string, ResizeArray<string>>(StringComparer.OrdinalIgnoreCase)
        for kv in symbolIndex do
            for entry in kv.Value do
                let file = Path.GetFileName(entry.FilePath)
                if not (fileSymbols.ContainsKey(file)) then
                    fileSymbols.[file] <- ResizeArray()
                fileSymbols.[file].Add(kv.Key)

        for kv in fileSymbols do
            let file = kv.Key
            // A file is documented if its name, stem, or any prominent symbol appears in KS content
            let stem = Path.GetFileNameWithoutExtension(file)
            let isDocumented =
                (stem.Length >= 3 && Regex.IsMatch(allKsContent, sprintf @"\b%s\b" (Regex.Escape(stem)), RegexOptions.IgnoreCase))
                || Regex.IsMatch(allKsContent, sprintf @"\b%s\b" (Regex.Escape(file)), RegexOptions.IgnoreCase)
            if isDocumented then
                documentedFiles.Add(file) |> ignore

        // Compute fanIn per file (reuse hotspots approach: resolve imports to file names via stem matching)
        let allFiles = index.Chunks |> Array.map (fun c -> Path.GetFileName c.FilePath) |> Array.distinct
        let fileStems = allFiles |> Array.map (fun f -> f, Path.GetFileNameWithoutExtension(f).ToLowerInvariant()) |> dict
        let fanInMap = Dictionary<string, int>()
        for (filePath, importedModule) in index.Imports do
            let mLower = importedModule.ToLowerInvariant()
            let src = Path.GetFileName filePath
            for kv in fileStems do
                if kv.Key <> src && mLower.Contains(kv.Value) && kv.Value.Length >= 3 then
                    fanInMap.[kv.Key] <- (match fanInMap.TryGetValue(kv.Key) with true, v -> v + 1 | _ -> 0) + 1

        // Report files with fanIn >= threshold that are NOT documented
        let results = ResizeArray<Dictionary<string, obj>>()
        for kv in fanInMap do
            let file = kv.Key
            let fanIn = kv.Value
            if fanIn >= minFanIn && not (documentedFiles.Contains(file)) then
                let chunks = index.Chunks |> Array.filter (fun c -> Path.GetFileName(c.FilePath) = file)
                let kinds = chunks |> Array.map (fun c -> c.Kind) |> Array.distinct |> String.concat ","
                let topNames = chunks |> Array.map (fun c -> c.Name) |> Array.distinct |> Array.truncate 5 |> String.concat ", "
                let loc = chunks |> Array.sumBy (fun c -> c.EndLine - c.StartLine + 1)
                results.Add(mdict [
                    "file", box file
                    "path", box (chunks.[0].FilePath.Replace('\\', '/'))
                    "fanIn", box fanIn
                    "chunks", box chunks.Length
                    "loc", box loc
                    "kinds", box kinds
                    "topSymbols", box topNames
                ])
        results.ToArray() |> Array.sortByDescending (fun d -> d.["fanIn"] :?> int)
