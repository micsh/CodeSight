namespace AITeam.CodeSight

open System
open System.Collections.Generic
open System.IO
open Jint

/// Jint-based query engine. Wires all 12 primitives, evaluates JS, formats results.
module QueryEngine =

    /// Create a Jint engine with all primitives wired.
    let create (index: CodeIndex) (chunks: CodeChunk[] option) (embeddingUrl: string) =
        let session = QuerySession()
        let engine = Engine()

        // search
        engine.SetValue("search", Func<string, obj, obj>(fun query opts ->
            let limit, kind, file =
                match opts with
                | :? Jint.Native.JsObject as o ->
                    let l = match o.Get("limit") with v when not (v.IsUndefined()) -> int (v.AsNumber()) | _ -> 5
                    let k = match o.Get("kind") with v when not (v.IsUndefined()) && not (v.IsNull()) -> v.AsString() | _ -> ""
                    let f = match o.Get("file") with v when not (v.IsUndefined()) && not (v.IsNull()) -> v.AsString() | _ -> ""
                    l, k, f
                | _ -> 5, "", ""
            box (Primitives.search index session chunks embeddingUrl query limit kind file))) |> ignore

        // context
        engine.SetValue("context", Func<string, obj>(fun f -> box (Primitives.context index session f))) |> ignore

        // impact
        engine.SetValue("impact", Func<string, obj>(fun t -> box (Primitives.impact index session t))) |> ignore

        // imports
        engine.SetValue("imports", Func<string, obj>(fun f -> box (Primitives.imports index f))) |> ignore

        // deps
        engine.SetValue("deps", Func<string, obj>(fun p -> box (Primitives.deps index p))) |> ignore

        // expand
        engine.SetValue("expand", Func<string, obj>(fun id -> box (Primitives.expand index session chunks id))) |> ignore

        // neighborhood
        engine.SetValue("neighborhood", Func<string, obj, obj>(fun id opts ->
            let before, after =
                match opts with
                | :? Jint.Native.JsObject as o ->
                    let b = match o.Get("before") with v when not (v.IsUndefined()) -> int (v.AsNumber()) | _ -> 3
                    let a = match o.Get("after") with v when not (v.IsUndefined()) -> int (v.AsNumber()) | _ -> 3
                    b, a
                | _ -> 3, 3
            box (Primitives.neighborhood index session chunks id before after))) |> ignore

        // similar
        engine.SetValue("similar", Func<string, obj, obj>(fun id opts ->
            let limit =
                match opts with
                | :? Jint.Native.JsObject as o ->
                    match o.Get("limit") with v when not (v.IsUndefined()) -> int (v.AsNumber()) | _ -> 5
                | _ -> 5
            box (Primitives.similar index session id limit))) |> ignore

        // grep
        engine.SetValue("grep", Func<string, obj, obj>(fun pattern opts ->
            let limit, kind, file =
                match opts with
                | :? Jint.Native.JsObject as o ->
                    let l = match o.Get("limit") with v when not (v.IsUndefined()) -> int (v.AsNumber()) | _ -> 10
                    let k = match o.Get("kind") with v when not (v.IsUndefined()) && not (v.IsNull()) -> v.AsString() | _ -> ""
                    let f = match o.Get("file") with v when not (v.IsUndefined()) && not (v.IsNull()) -> v.AsString() | _ -> ""
                    l, k, f
                | _ -> 10, "", ""
            box (Primitives.grep index session chunks pattern limit kind file))) |> ignore

        // files
        engine.SetValue("files", Func<string, obj>(fun p -> box (Primitives.files index (if isNull p then "" else p)))) |> ignore

        // modules
        engine.SetValue("modules", Func<obj>(fun () -> box (Primitives.modules index))) |> ignore

        // refs
        engine.SetValue("refs", Func<string, obj, obj>(fun name opts ->
            let limit =
                match opts with
                | :? Jint.Native.JsObject as o ->
                    match o.Get("limit") with v when not (v.IsUndefined()) -> int (v.AsNumber()) | _ -> 20
                | _ -> 20
            box (Primitives.refs index session chunks name limit))) |> ignore

        engine

    /// Evaluate JS with IIFE wrapping (avoids let-redeclaration across calls).
    let eval (engine: Engine) (js: string) : string =
        try
            let lines = js.Trim().Split('\n') |> Array.map (fun s -> s.Trim()) |> Array.filter (fun s -> s <> "")
            let body =
                if lines.Length <= 1 then sprintf "return %s" lines.[0]
                else
                    let init = lines.[..lines.Length-2] |> String.concat "\n  "
                    sprintf "%s\n  return %s" init (lines.[lines.Length-1])
            let wrapped = sprintf "(function() {\n  %s\n})()" body
            let result = engine.Evaluate(wrapped)
            let native = result.ToObject()
            Format.formatValue native
        with ex -> sprintf "Error: %s" ex.Message


