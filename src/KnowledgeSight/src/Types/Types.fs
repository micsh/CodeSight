namespace AITeam.KnowledgeSight

open System
open System.Collections.Generic

/// A section chunk extracted from a markdown document.
type DocChunk = {
    FilePath: string
    Heading: string       // section heading text (or "(intro)" for pre-heading content)
    HeadingPath: string   // full chain: "Parent > Child > Subsection"
    Level: int            // 0 = intro/frontmatter, 1 = h1, 2 = h2, etc.
    StartLine: int
    EndLine: int
    Content: string
    Summary: string       // first meaningful sentence or LLM-generated
    Tags: string[]        // from frontmatter or inferred
    OutLinks: string[]    // outgoing markdown links from this section
}

/// A link between documents.
type DocLink = {
    SourceFile: string
    SourceHeading: string
    TargetPath: string    // raw link target (may be relative)
    TargetResolved: string // resolved absolute path (empty if broken)
    LinkText: string
    Line: int
}

/// Frontmatter metadata.
type FrontmatterValue =
    | Scalar of string
    | StringList of string[]

type Frontmatter = {
    Id: string
    Title: string
    Status: string
    Tags: string[]
    Related: string[]
    Extra: Map<string, FrontmatterValue>
}

/// Index entry (persisted, without full content).
type ChunkEntry = {
    FilePath: string
    Heading: string
    HeadingPath: string
    Level: int
    StartLine: int
    EndLine: int
    Summary: string
    Tags: string
    LinkCount: int
    WordCount: int
}

/// The full in-memory index.
type DocIndex = {
    Chunks: ChunkEntry[]
    Embeddings: float32[][]
    Links: DocLink[]
    Frontmatters: Map<string, Frontmatter>
    EmbeddingDim: int
}

/// Mutable dictionary builder — Jint needs writable dictionaries.
[<AutoOpen>]
module DictHelper =
    let mdict (pairs: (string * obj) list) =
        let d = Dictionary<string, obj>()
        for (k, v) in pairs do d.[k] <- v
        d

[<AutoOpen>]
module FrontmatterHelper =
    let private isTypedFrontmatterKey (key: string) =
        match key.ToLowerInvariant() with
        | "id"
        | "title"
        | "status"
        | "tags"
        | "related" -> true
        | _ -> false

    let private frontmatterScalarValue = function
        | Scalar value when not (String.IsNullOrWhiteSpace(value)) -> Some value
        | StringList values when values.Length > 0 -> Some values.[0]
        | _ -> None

    let private frontmatterListValue = function
        | StringList values -> values |> Array.filter (String.IsNullOrWhiteSpace >> not)
        | Scalar value when not (String.IsNullOrWhiteSpace(value)) -> [| value |]
        | _ -> [||]

    let private fieldList (key: string) (values: string[]) =
        let filtered = values |> Array.filter (String.IsNullOrWhiteSpace >> not)
        if filtered.Length = 0 then None
        else Some (key, StringList filtered)

    let private fieldScalar (key: string) (value: string) =
        if String.IsNullOrWhiteSpace(value) then None
        else Some (key, Scalar value)

    let frontmatterFields (frontmatter: Frontmatter) =
        let typedFields =
            [
                fieldScalar "id" frontmatter.Id
                fieldScalar "title" frontmatter.Title
                fieldScalar "status" frontmatter.Status
                fieldList "tags" frontmatter.Tags
                fieldList "related" frontmatter.Related
            ]
            |> List.choose id

        let extraFields =
            frontmatter.Extra
            |> Map.filter (fun key _ -> not (isTypedFrontmatterKey key))
            |> Map.toList

        typedFields @ extraFields
        |> Map.ofList

    let frontmatterFromFields (fields: Map<string, FrontmatterValue>) =
        let scalar key =
            fields
            |> Map.tryFind key
            |> Option.bind frontmatterScalarValue
            |> Option.defaultValue ""

        let list key =
            fields
            |> Map.tryFind key
            |> Option.map frontmatterListValue
            |> Option.defaultValue [||]

        {
            Id = scalar "id"
            Title = scalar "title"
            Status = scalar "status"
            Tags = list "tags"
            Related = list "related"
            Extra = fields |> Map.filter (fun key _ -> not (isTypedFrontmatterKey key))
        }

    let frontmatterValueToObj = function
        | Scalar value -> box value
        | StringList values -> box values

    let frontmatterToDict (frontmatter: Frontmatter) =
        frontmatterFields frontmatter
        |> Map.toList
        |> List.map (fun (key, value) -> key, frontmatterValueToObj value)
        |> mdict
