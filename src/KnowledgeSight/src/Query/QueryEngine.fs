namespace AITeam.KnowledgeSight

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open Jint
open AITeam.Sight.Core

/// Jint-based query engine. Wires all primitives, evaluates JS, formats results.
module QueryEngine =

    let private sourceKey = "__ks_source__"

    /// Extract a string property from a JS options object, handling JsObject / ObjectInstance / JsValue.
    let private jsStr (opts: obj) (key: string) (def: string) =
        match opts with
        | :? Jint.Native.JsValue as v when v.IsObject() ->
            let o = v.AsObject()
            let prop = o.Get(key)
            if prop.IsUndefined() || prop.IsNull() then def else prop.AsString()
        | :? System.Dynamic.ExpandoObject as eo ->
            let dict = eo :> IDictionary<string, obj>
            match dict.TryGetValue(key) with
            | true, v when not (isNull v) -> string v
            | _ -> def
        | _ -> def

    /// Extract a string property only when the key is present; null/undefined count as explicit blank input.
    let private jsStrOption (opts: obj) (key: string) =
        match opts with
        | :? Jint.Native.JsValue as v when v.IsObject() ->
            let o = v.AsObject()
            if o.HasProperty(key) then
                let prop = o.Get(key)
                if prop.IsUndefined() || prop.IsNull() then Some "" else Some(prop.AsString())
            else
                None
        | :? System.Dynamic.ExpandoObject as eo ->
            let dict = eo :> IDictionary<string, obj>
            match dict.TryGetValue(key) with
            | true, v when isNull v -> Some ""
            | true, v -> Some (string v)
            | _ -> None
        | _ -> None

    /// Extract a raw property only when the key is present; preserves null presence for validation.
    let private jsPropOption (opts: obj) (key: string) =
        match opts with
        | :? Jint.Native.JsValue as v when v.IsObject() ->
            let o = v.AsObject()
            if o.HasProperty(key) then
                let prop = o.Get(key)
                if prop.IsUndefined() || prop.IsNull() then Some null else Some(prop.ToObject())
            else
                None
        | :? System.Dynamic.ExpandoObject as eo ->
            let dict = eo :> IDictionary<string, obj>
            match dict.TryGetValue(key) with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    /// Extract an int property from a JS options object.
    let private jsInt (opts: obj) (key: string) (def: int) =
        match opts with
        | :? Jint.Native.JsValue as v when v.IsObject() ->
            let o = v.AsObject()
            let prop = o.Get(key)
            if prop.IsUndefined() || prop.IsNull() then def else int (prop.AsNumber())
        | :? System.Dynamic.ExpandoObject as eo ->
            let dict = eo :> IDictionary<string, obj>
            match dict.TryGetValue(key) with
            | true, v when not (isNull v) -> int (System.Convert.ToDouble(v))
            | _ -> def
        | _ -> def

    /// Extract a float property from a JS options object.
    let private jsFloat (opts: obj) (key: string) (def: float) =
        match opts with
        | :? Jint.Native.JsValue as v when v.IsObject() ->
            let o = v.AsObject()
            let prop = o.Get(key)
            if prop.IsUndefined() || prop.IsNull() then def else prop.AsNumber()
        | :? System.Dynamic.ExpandoObject as eo ->
            let dict = eo :> IDictionary<string, obj>
            match dict.TryGetValue(key) with
            | true, v when not (isNull v) -> System.Convert.ToDouble(v)
            | _ -> def
        | _ -> def

    /// Extract a bool property from a JS options object.
    let private jsBool (opts: obj) (key: string) (def: bool) =
        match opts with
        | :? Jint.Native.JsValue as v when v.IsObject() ->
            let o = v.AsObject()
            let prop = o.Get(key)
            if prop.IsUndefined() || prop.IsNull() then def else prop.AsBoolean()
        | :? System.Dynamic.ExpandoObject as eo ->
            let dict = eo :> IDictionary<string, obj>
            match dict.TryGetValue(key) with
            | true, v when not (isNull v) -> System.Convert.ToBoolean(v)
            | _ -> def
        | _ -> def

    /// Extract a string-array property from a JS options object.
    let private jsStrArray (opts: obj) (key: string) (def: string[]) =
        let coerce (value: obj) =
            match value with
            | null -> def
            | :? string as s -> [| s |]
            | :? (obj array) as arr -> arr |> Array.choose (fun item -> if isNull item then None else Some (string item))
            | :? System.Collections.IEnumerable as items ->
                items |> Seq.cast<obj> |> Seq.choose (fun item -> if isNull item then None else Some (string item)) |> Seq.toArray
            | _ -> [| string value |]

        match opts with
        | :? Jint.Native.JsValue as v when v.IsObject() ->
            let o = v.AsObject()
            let prop = o.Get(key)
            if prop.IsUndefined() || prop.IsNull() then def else coerce (prop.ToObject())
        | :? System.Dynamic.ExpandoObject as eo ->
            let dict = eo :> IDictionary<string, obj>
            match dict.TryGetValue(key) with
            | true, v when not (isNull v) -> coerce v
            | _ -> def
        | _ -> def

    /// Stamp each result item with its source primitive name for format disambiguation.
    let private stamp (source: string) (results: Dictionary<string, obj>[]) =
        for d in results do d.[sourceKey] <- box source
        results

    let private stamp1 (source: string) (result: Dictionary<string, obj>) =
        result.[sourceKey] <- box source
        result

    let create (cfg: KnowledgeSightConfig) (index: DocIndex) (chunks: DocChunk[] option) =
        let session = QuerySession(cfg.IndexDir)
        let engine = new Engine()
        let mutable currentIndex = index
        let mutable currentChunks = chunks

        let refreshState (updatedIndex: DocIndex) (updatedChunks: DocChunk[] option) =
            currentIndex <- updatedIndex
            currentChunks <- updatedChunks

        // catalog
        engine.SetValue("catalog", Func<obj, obj>(fun opts ->
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "catalog" (Primitives.catalog cfg currentIndex statuses)))) |> ignore

        // search
        engine.SetValue("search", Func<string, obj, obj>(fun query opts ->
            let limit = jsInt opts "limit" 5
            let tag = jsStr opts "tag" ""
            let file = jsStr opts "file" ""
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "search" (Primitives.search cfg currentIndex session currentChunks cfg.EmbeddingUrl query limit tag file statuses)))) |> ignore

        // context
        engine.SetValue("context", Func<string, obj>(fun f -> box (stamp1 "context" (Primitives.context currentIndex session f)))) |> ignore

        // expand
        engine.SetValue("expand", Func<string, obj>(fun id -> box (stamp1 "expand" (Primitives.expand currentIndex session currentChunks id)))) |> ignore

        // neighborhood
        engine.SetValue("neighborhood", Func<string, obj, obj>(fun id opts ->
            let before = jsInt opts "before" 3
            let after = jsInt opts "after" 3
            box (stamp1 "neighborhood" (Primitives.neighborhood currentIndex session currentChunks id before after)))) |> ignore

        // similar
        engine.SetValue("similar", Func<string, obj, obj>(fun id opts ->
            let limit = jsInt opts "limit" 5
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "similar" (Primitives.similar cfg currentIndex session id limit statuses)))) |> ignore

        // grep
        engine.SetValue("grep", Func<string, obj, obj>(fun pattern opts ->
            let limit = jsInt opts "limit" 10
            let file = jsStr opts "file" ""
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "grep" (Primitives.grep cfg currentIndex session currentChunks pattern limit file statuses)))) |> ignore

        // mentions
        engine.SetValue("mentions", Func<string, obj, obj>(fun term opts ->
            let limit = jsInt opts "limit" 20
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "mentions" (Primitives.mentions cfg currentIndex session currentChunks term limit statuses)))) |> ignore

        // files
        engine.SetValue("files", Func<string, obj>(fun p -> box (stamp "files" (Primitives.files currentIndex (if isNull p then "" else p))))) |> ignore

        // backlinks
        engine.SetValue("backlinks", Func<string, obj, obj>(fun f opts ->
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "backlinks" (Primitives.backlinks cfg currentIndex session f statuses)))) |> ignore

        // links
        engine.SetValue("links", Func<string, obj, obj>(fun f opts ->
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "links" (Primitives.links cfg currentIndex f statuses)))) |> ignore

        // pinned
        engine.SetValue("pinned", Func<obj, obj>(fun opts ->
            let tier = jsStr opts "tier" "grammar"
            box (stamp "pinned" (Primitives.pinned currentIndex session tier)))) |> ignore

        // orphans
        engine.SetValue("orphans", Func<obj, obj>(fun opts ->
            let statuses = jsStrArray opts "status" Primitives.orphansDefaultStatuses
            box (stamp "orphans" (Primitives.orphans cfg currentIndex statuses)))) |> ignore

        // broken
        engine.SetValue("broken", Func<obj, obj>(fun opts ->
            let statuses = jsStrArray opts "status" Primitives.brokenDefaultStatuses
            box (stamp "broken" (Primitives.broken cfg currentIndex statuses)))) |> ignore

        // placement
        engine.SetValue("placement", Func<string, obj, obj>(fun content opts ->
            let limit = jsInt opts "limit" 3
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "placement" (Primitives.placement cfg currentIndex cfg.EmbeddingUrl content limit statuses)))) |> ignore

        // walk
        engine.SetValue("walk", Func<string, obj, obj>(fun file opts ->
            let depth = jsInt opts "depth" 2
            let direction = jsStr opts "direction" "out"
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "walk" (Primitives.walk cfg currentIndex session file depth direction statuses)))) |> ignore

        // novelty
        engine.SetValue("novelty", Func<string, obj, obj>(fun text opts ->
            let threshold = jsFloat opts "threshold" 0.75
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "novelty" (Primitives.novelty cfg currentIndex cfg.EmbeddingUrl text threshold statuses)))) |> ignore

        // cluster
        engine.SetValue("cluster", Func<string, obj, obj>(fun dir opts ->
            let threshold = jsFloat opts "threshold" 0.7
            let statuses = jsStrArray opts "status" Primitives.retrievalDefaultStatuses
            box (stamp "cluster" (Primitives.cluster cfg currentIndex dir threshold statuses)))) |> ignore

        // hygiene
        engine.SetValue("hygiene", Func<Jint.Native.JsValue, obj>(fun opts ->
            let profile, limit =
                if isNull (box opts) || opts.IsUndefined() || opts.IsNull() then "", 10
                else jsStr opts "profile" "", jsInt opts "limit" 10
            box (stamp "hygiene" (Primitives.hygiene currentIndex currentChunks cfg.RepoRoot profile limit)))) |> ignore

        // gaps — use JsValue to avoid Jint's ToObject() conversion
        engine.SetValue("gaps", Func<Jint.Native.JsValue, obj>(fun opts ->
            let scope, minDocs, signal =
                if isNull (box opts) || opts.IsUndefined() || opts.IsNull() then "", 1, ""
                elif opts.IsString() then opts.AsString(), 1, ""
                elif opts.IsObject() then
                    let o = opts.AsObject()
                    let s = match o.Get("scope") with v when not (v.IsUndefined()) && not (v.IsNull()) -> v.AsString() | _ -> ""
                    let m = match o.Get("min_docs") with v when not (v.IsUndefined()) -> int (v.AsNumber()) | _ -> 1
                    let sig' = match o.Get("signal") with v when not (v.IsUndefined()) && not (v.IsNull()) -> v.AsString() | _ -> ""
                    s, m, sig'
                else "", 1, ""
            box (stamp "gaps" (Primitives.gaps currentIndex currentChunks scope minDocs signal)))) |> ignore

        // changed
        engine.SetValue("changed", Func<string, obj>(fun gitRef ->
            box (stamp "changed" (Primitives.changed currentIndex session cfg.RepoRoot gitRef)))) |> ignore

        // explain
        engine.SetValue("explain", Func<string, obj>(fun refId ->
            box (stamp1 "explain" (Primitives.explain currentIndex session currentChunks refId)))) |> ignore

        // propose
        engine.SetValue("propose", Func<string, obj, obj>(fun text opts ->
            let team = jsStr opts "team" ""
            let cycle = jsStr opts "cycle" ""
            let concept = jsStr opts "concept" ""
            let confidence = jsStr opts "confidence" ""
            let verify = jsStr opts "verify" ""
            let observable = jsStr opts "observable" ""
            let forbids = jsStr opts "forbids" ""
            let threshold = jsFloat opts "threshold" 0.75
            let dryRun = jsBool opts "dryRun" false
            box (stamp "propose" (Primitives.propose currentIndex session currentChunks cfg refreshState text team cycle concept confidence verify observable forbids threshold dryRun)))) |> ignore

        // triage
        engine.SetValue("triage", Func<obj, obj>(fun opts ->
            let team = jsStr opts "team" ""
            let before = jsStr opts "before" ""
            let limit = jsInt opts "limit" 20
            box (stamp "triage" (Primitives.triage currentIndex session cfg team before limit)))) |> ignore

        // dispose
        engine.SetValue("dispose", Func<string, obj, obj>(fun inboxRef opts ->
            let action = jsStr opts "action" ""
            let target = jsStr opts "target" ""
            let verify = jsStr opts "verify" ""
            let concept = jsStr opts "concept" ""
            let observable = jsStr opts "observable" ""
            let forbids = jsStr opts "forbids" ""
            let reason = jsStr opts "reason" ""
            let archive = jsBool opts "archive" cfg.ArchiveProcessed
            box (stamp1 "dispose" (Primitives.dispose currentIndex session cfg refreshState inboxRef action target verify concept observable forbids reason archive)))) |> ignore

        // supersede
        engine.SetValue("supersede", Func<string, string, obj, obj>(fun oldRef newContent opts ->
            let reason = jsStr opts "reason" ""
            let by = jsStr opts "by" ""
            let verify = jsStr opts "verify" ""
            box (stamp1 "supersede" (Primitives.supersede currentIndex session cfg refreshState oldRef newContent reason by verify)))) |> ignore

        // reverify
        engine.SetValue("reverify", Func<obj, obj>(fun opts ->
            let scope = jsStrArray opts "scope" [||]
            let apply = jsBool opts "apply" false
            box (stamp "reverify" (Primitives.reverify cfg session refreshState currentIndex currentChunks scope apply)))) |> ignore

        // conflicts
        engine.SetValue("conflicts", Func<Jint.Native.JsValue, obj>(fun opts ->
            let threshold, scope, judge, pairs, verdicts, rawVerdicts, rollup, profile, profiles, duplicatesOnly, hasConflict, mixedVerdicts, compatibleOnly, conflictOnly, noConflict =
                if isNull (box opts) || opts.IsUndefined() || opts.IsNull() then 0.82, [||], false, false, [||], None, false, None, None, false, false, false, false, false, false
                elif opts.IsNumber() then opts.AsNumber(), [||], false, false, [||], None, false, None, None, false, false, false, false, false, false
                elif opts.IsString() then 0.82, [| opts.AsString() |], false, false, [||], None, false, None, None, false, false, false, false, false, false
                elif opts.IsObject() then
                    let threshold = jsFloat opts "threshold" 0.82
                    let scope = jsStrArray opts "scope" [||]
                    let judge = jsBool opts "judge" false
                    let pairs = jsBool opts "pairs" false
                    let verdicts = jsStrArray opts "verdicts" [||]
                    let rawVerdicts = jsPropOption opts "verdicts"
                    let rollup = jsBool opts "rollup" false
                    let profile = jsStrOption opts "profile"
                    let profiles = jsPropOption opts "profiles"
                    let duplicatesOnly = jsBool opts "duplicatesOnly" false
                    let hasConflict = jsBool opts "hasConflict" false
                    let mixedVerdicts = jsBool opts "mixedVerdicts" false
                    let compatibleOnly = jsBool opts "compatibleOnly" false
                    let conflictOnly = jsBool opts "conflictOnly" false
                    let noConflict = jsBool opts "noConflict" false
                    threshold, scope, judge, pairs, verdicts, rawVerdicts, rollup, profile, profiles, duplicatesOnly, hasConflict, mixedVerdicts, compatibleOnly, conflictOnly, noConflict
                else 0.82, [||], false, false, [||], None, false, None, None, false, false, false, false, false, false
            box (stamp "conflicts" (Primitives.conflicts cfg currentIndex session threshold scope judge pairs verdicts rawVerdicts rollup profile profiles duplicatesOnly hasConflict mixedVerdicts compatibleOnly conflictOnly noConflict)))) |> ignore

        // prune
        engine.SetValue("prune", Func<Jint.Native.JsValue, obj>(fun opts ->
            let scope, olderThanDays, apply =
                if isNull (box opts) || opts.IsUndefined() || opts.IsNull() then
                    [||], Primitives.pruneDefaultOlderThanDays, false
                elif opts.IsNumber() then
                    [||], int (opts.AsNumber()), false
                elif opts.IsString() then
                    [| opts.AsString() |], Primitives.pruneDefaultOlderThanDays, false
                elif opts.IsObject() then
                    let scope = jsStrArray opts "scope" [||]
                    let olderThanDays = jsInt opts "olderThanDays" (jsInt opts "olderThan" Primitives.pruneDefaultOlderThanDays)
                    let apply = jsBool opts "apply" false
                    scope, olderThanDays, apply
                else
                    [||], Primitives.pruneDefaultOlderThanDays, false
            box (stamp "prune" (Primitives.prune cfg refreshState currentIndex scope olderThanDays apply)))) |> ignore

        // session save/load/list
        engine.SetValue("saveSession", Func<string, obj>(fun name ->
            session.SaveSession(name)
            box (mdict [ "saved", box name; "refs", box session.RefCount ]))) |> ignore
        engine.SetValue("loadSession", Func<string, obj>(fun name ->
            if session.LoadSession(name) then
                box (mdict [ "loaded", box name; "refs", box session.RefCount ])
            else
                box (mdict [ "error", box (sprintf "session '%s' not found" name) ]))) |> ignore
        engine.SetValue("sessions", Func<obj>(fun () ->
            box (session.ListSessions()))) |> ignore

        // Composition helpers + user-defined functions (from Sight.Core)
        QueryHelpers.registerHelpers engine Format.formatValue
        QueryHelpers.registerUserFunctions engine
            { FileName = "knowledge-sight.functions.json"
              ReservedNames = FunctionStore.reservedNames }
            cfg.RepoRoot

        engine

    /// Wrap JS in an IIFE for evaluation.
    /// Evaluate JS with IIFE wrapping — human-readable formatted output.
    let eval (engine: Engine) (js: string) : string =
        QueryHelpers.eval engine Format.formatValue js

    /// Evaluate JS with IIFE wrapping — raw JSON output for machine consumption.
    let evalJson (engine: Engine) (js: string) : string =
        QueryHelpers.evalJson engine js
