# CodeSight

Code intelligence tool for any codebase. Indexes source code using tree-sitter AST parsing, then provides query primitives via CLI. Agents and developers can search semantically, trace references, analyze impact, and explore code structure — all without reading full files.

## Install

**macOS / Linux:**
```bash
curl -fsSL https://raw.githubusercontent.com/micsh/Sightline/main/install-code-sight.sh | bash
```

**Windows (PowerShell):**
```powershell
irm https://raw.githubusercontent.com/micsh/Sightline/main/install-code-sight.ps1 | iex
```

Or download binaries directly from [Releases](https://github.com/micsh/Sightline/releases).

## Prerequisites

- **.NET 10 SDK** (preview) — [download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Node.js** 18+ (for tree-sitter WASM parsers at runtime)
- **GitHub Copilot CLI** — required only for the `intel` command ([setup guide](https://docs.github.com/en/copilot/how-tos/copilot-sdk/set-up-copilot-sdk))
- A local **embedding server** (see below)

### Embedding server setup

CodeSight uses vector embeddings for semantic search. Any OpenAI-compatible `/v1/embeddings` endpoint works. The recommended model is **nomic-embed-text-v1.5**.

**Option A — llama-server (recommended):**

Download the GGUF from [Hugging Face](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF), then:

```bash
llama-server -m nomic-embed-text-v1.5.Q8_0.gguf --port 1234 --embedding
```

**Option B — LM Studio:**

Load `nomic-embed-text-v1.5` in LM Studio and start the server. Default endpoint: `http://localhost:1234/v1/embeddings`.

**Option C — any other provider:**

Set `embeddingUrl` in your repo's `code-intel.json`:

```json
{ "embeddingUrl": "http://your-server:port/v1/embeddings" }
```

The default URL is `http://localhost:1234/v1/embeddings`.

## Setup

For development (building from source):

```
cd parsers && npm install --legacy-peer-deps
dotnet build
```

### Contract verifier (developer)

After building CodeSight from source, run the committed hostile temp-repo verifier:

```powershell
pwsh ./src/CodeSight/verify-contracts.ps1
```

What it proves:
- inaccessible-subtree pruning for deeper valid glob excludes
- bare-segment + built-in default exclude behavior
- seeded stale-cache upgrade and partial-reindex `MaxChunkChars` repair for persisted `source-chunks.jsonl`
- structural queries still load from `chunks.tsv` when `code.emb` / `summary.emb` are missing or corrupt
- semantic queries stay explicitly degraded under damaged semantic artifacts and `--no-embed`
- live `search()` query-embedding failures return explicit transport-specific error objects without mutating persisted index artifacts
- explicit `--files` bypass and `index`/`preflight` discovery parity
- read-only `preflight` against populated index trees
- bounded `status` / `history` / `compare` viability
- unchanged `semantic-state.json` schema
- deterministic degraded-exit truth via a local embedding-failure harness
- default/custom `indexDir` boundaries

Use `-DllPath` if you want to point the verifier at a specific built `code-sight.dll`, and `-KeepTemp` if you want to inspect the temp repos after a failure.

### Robustness backlog closure ledger

This closes the original runtime-only robustness note in `.agents/design/codesight-index-robustness.md`. It is an audit map, not a new backlog: rows marked closed require new concrete failure evidence before they should be reopened.

| Original note area | Current status | Canonical current contract |
|---|---|---|
| `index --no-embed` structural-only mode | **Verifier-frozen** | `index --no-embed` is a structural-only success path; structural queries still work, while semantic queries return explicit semantic-unavailable results instead of pretending health. (This supersedes the original "return empty arrays" expectation.) |
| Partial embed failure must not produce an unloadable `.emb` | **Verifier-frozen** | Persisted semantic artifacts may be degraded, but `IndexStore.load` stays loadable and structural queries keep working instead of crashing on mixed/damaged semantic state. |
| `load` must succeed without `code.emb` | **Verifier-frozen** | Structural queries load from `chunks.tsv` even when `code.emb` / `summary.emb` are missing or corrupt; semantic operations degrade explicitly instead of blocking structural use. |
| Surface the real embed error / classify transport failures honestly | **Verifier-frozen** | Degraded indexing records truthful failure detail and exit state; live `search()` query embedding failures return explicit timeout / connect-refused / DNS / HTTP / malformed-response error objects without mutating persisted index artifacts. |
| Chunk/cache paths must honour `MaxChunkChars` | **Verifier-frozen** | The committed verifier proves seeded stale-cache upgrade and partial-reindex repair for persisted `source-chunks.jsonl`, so oversized cached content is not treated as healthy/up-to-date. |
| Non-zero exit when semantic indexing is degraded | **Verifier-frozen** | `code-sight index` exits `2` for degraded semantic health, `1` for invocation failure, and `0` for healthy or intentional structural-only `--no-embed` success. |
| `exclude` config must merge with defaults and preserve honest matching/pruning | **Verifier-frozen** | Built-in excludes remain active, custom excludes only narrow scope, bare entries stay segment matches, wildcard excludes get traversal-honest pruning, and explicit `--files` bypass stays direct-file resolution. |
| Nice-to-have: timeout differentiation + `localhost` default endpoint | **Accepted-and-closed** | `embeddingTimeoutSeconds` and the `localhost` default are now committed surfaces, and transport classification is part of the current hostile-proof contract rather than a future enhancement. |
| Nice-to-have: de-duplicate `embedBatch` | **Accepted-and-closed (non-goal)** | This is not a live robustness gap by itself. Reopen only if duplication causes a concrete behavioral failure; style cleanup alone is not evidence for a new robustness wave. |

Audit result: **no genuinely uncovered contract was found** in the original note after mapping it to the current committed verifier and accepted runtime behavior.

Reopen this surface only when at least one of the following exists:
- a new reproducible hostile case that the committed verifier does not already cover
- a mismatch between current runtime behavior and the contracts documented above
- a genuinely new robustness contract that cannot honestly be classified as already covered or intentionally closed

For binary distribution, publish a self-contained single-file executable:

```bash
dotnet publish -c Release -r win-x64    # Windows
dotnet publish -c Release -r linux-x64  # Linux
dotnet publish -c Release -r osx-arm64  # macOS Apple Silicon
```

The output is a single `code-sight` executable with all dependencies bundled. **Node.js** is the only runtime dependency (tree-sitter WASM parsers run as a child process). No `npm install` needed — all parser packages and WASM grammars are included in the binary.

## Usage

```bash
# Index a codebase (incremental — only re-chunks changed files)
code-sight index --repo /path/to/repo

# Inspect index scope/scale before a costly run (read-only)
code-sight preflight --repo /path/to/repo
code-sight preflight --repo /path/to/repo --files src/App.fs src/Missing.fs

# Inspect the latest run or a bounded recent history of index invocations
code-sight status
code-sight history
code-sight compare

# Project map — what modules exist, how many files, key types
code-sight modules

# Direct queries — 21 primitives via JavaScript
code-sight search 'refs("MyType", {limit:10})'
code-sight search 'context("Orchestrator.fs")'
code-sight search 'grep("Result<string, CliError>")'
code-sight search 'modules()'

# eval — alias for search, semantic clarity for non-search expressions
code-sight eval 'modules()'

# Natural language exploration — dispatches to gpt-mini scout
# Auto-discovers UDFs and includes their signatures in the AI tool description
code-sight intel "What does this codebase do?"
code-sight intel "Where should I add a new reactor?"
code-sight intel "What breaks if I change AgentConfig?"

# Scoped queries — filter to src, tests, or everything
code-sight search 'modules()' --scope src
code-sight search 'files("Test")' --scope tests

# Interactive REPL
code-sight repl

# List available scopes
code-sight scopes

# Show full help including all primitives
code-sight --help
```

If `--repo` is omitted, uses the current working directory.

## Primitives (21 query + 3 session)

| Function | Returns | Use for |
|----------|---------|---------|
| `search(query, {limit, kind, file})` | scored results with summary + signature + preview | semantic search |
| `refs(name, {limit})` | references with matchLine showing HOW it's used | tracing usage |
| `grep(pattern, {limit, kind, file})` | matches with matched line | exact pattern search |
| `modules()` | project-level map (files, chunks, types per module) | orientation |
| `files(pattern?)` | file list with chunk counts | file discovery |
| `context(file)` | all chunks + imports + dependents for a file | file overview |
| `impact(type)` | files that reference a type | type-level impact |
| `imports(file)` | what a file imports | dependency check |
| `deps(pattern)` | who imports from a module | reverse dependency |
| `expand(id)` | full source code + imports + dependents | read code |
| `neighborhood(id, {before, after})` | target code + surrounding chunks | code in context |
| `similar(id, {limit})` | structurally similar chunks | find patterns |
| `walk(name, {depth, limit})` | recursive reference chain tracing | call chains |
| `callers(name, {limit})` | call sites of a qualified name | find who calls a function |
| `changed(gitRef)` | chunks in files changed since a git ref | recent changes |
| `hotspots({by, min})` | structural metrics per file: chunks, LOC, fanIn, fanOut | complexity analysis |
| `explain(refId)` | full index metadata + findSource diagnosis | debugging refs |
| `trace(from, to)` | BFS shortest path between two files over import graph | dependency paths |
| `arch(file)` | owner module, boundary, depends_on, used_by, peer_modules | architectural context |

### Session Primitives

| Function | Description |
|----------|-------------|
| `saveSession(name)` | Save current ref IDs and results to `{indexDir}/sessions/{name}.json` |
| `loadSession(name)` | Restore a previously saved session |
| `sessions()` | List all saved sessions |

Results carry ref IDs (R1, R2...) — pass them to `expand()` or `neighborhood()` to drill deeper. Bare `R123` tokens are auto-quoted to `'R123'` before evaluation (ref-ID shorthand).

Compose with JavaScript: `.filter()`, `.map()`, `let` variables. Each query is isolated.

### Composition Helpers

In addition to the query primitives, four composition helpers are available as globals:

| Helper | Description |
|--------|-------------|
| `pipe(value, fn1, fn2, ...)` | Thread a value through a series of functions sequentially |
| `tap(value, fn)` | Run `fn` for side-effects (e.g. debugging), return `value` unchanged |
| `mergeBy(key, arr1, arr2, ...)` | Union multiple arrays with dedup by the specified key field |
| `print(value)` | Debug output to stderr (Jint has no `console` object — use `print` instead) |

```js
// Thread search results through similar then expand
pipe(search('auth'), function(r) { return similar(r[0].id) })

// Debug without breaking chains
tap(search('auth'), function(r) { print('found ' + r.length + ' results') })

// Combine results from different primitives with dedup
mergeBy('id', search('auth'), grep('authentication'))
```

## Functions

Save chains of primitives as reusable, named functions. Functions are **per-repo** — each repo has its own set, stored in `code-sight.functions.json` at the repo root.

### Defining functions

```bash
# Simple function with no parameters
code-sight fn add overview --desc "Quick project map" "modules()"

# Function with parameters
code-sight fn add deepSearch --params "q" --desc "Search + similar expansion" \
  "search(q).concat(similar(search(q)[0]))"

# Multi-parameter function
code-sight fn add scopedGrep --params "pattern,file" \
  "grep(pattern, {file: file})"

# Read body from a file (useful for complex functions)
code-sight fn add audit --file audit-fn.js --desc "Full code audit"
```

### Using functions

Once defined, functions are available in any `search` expression:

```bash
code-sight search "deepSearch('authentication')"
code-sight search "scopedGrep('TODO', 'Orchestrator.fs')"
```

### Managing functions

```bash
# List all functions (compact: name, params, description)
code-sight fn list

# Full details including function body
code-sight fn list --verbose

# Machine-readable JSON output
code-sight fn list --json

# Remove a function
code-sight fn rm deepSearch
```

### Options for `fn add`

| Option | Description |
|--------|-------------|
| `--params "a,b"` | Comma-separated parameter names |
| `--desc "..."` | Optional description |
| `--file <path>` | Read function body from a file instead of inline |

Function bodies use the same last-expression-return semantics as inline queries. Names are validated — they must be valid JS identifiers and cannot shadow built-in primitives.

### The `eval` command

`eval` is an alias for `search`. Use it when your expression isn't really a "search" — e.g. `eval "modules()"`. Behavior is identical.

### Intel UDF discovery

The `intel` command auto-discovers user-defined functions and includes their signatures in the AI tool description. This means the gpt-mini scout agent can use your UDFs when answering questions — no extra configuration needed. Define a function with `fn add`, and `intel` will pick it up automatically.

### Improved result formatting

Mixed result arrays (e.g. `refs('MyType').concat(search('MyType'))`) now format cleanly. Each result item carries internal type metadata, so the formatter applies deterministic per-item formatting instead of guessing the shape of the entire array. No action needed — this is automatic.

### Storage

Functions are stored in `code-sight.functions.json` in the repo root. This file is meant to be committed alongside your code so the whole team shares the same functions.

## Configuration

Place `code-intel.json` at the repo root. All fields are optional — defaults are auto-detected.

```json
{
    "srcDirs": ["src", "lib"],
    "extensions": [".fs", ".cs", ".ts", ".py"],
    "exclude": ["coverage", ".cache"],
    "scopes": {
        "src": ["src"],
        "tests": ["tests"],
        "all": ["src", "tests"]
    },
    "embeddingUrl": "http://localhost:1234/v1/embeddings",
    "embeddingTimeoutSeconds": 120,
    "chunkerScript": "path/to/ts-chunker.js"
}
```

- `exclude` entries support two forms: bare entries like `"dist"` stay **path-segment matches** for backward compatibility, while wildcard-bearing entries like `"**/dist/**"` use **glob-style repo-relative matching**.
- Custom `exclude` entries are appended to the built-in defaults (`node_modules`, `bin`, `obj`, `.git`, etc.), so adding repo-specific excludes can only narrow the indexed file set.
- Discovery-driven `index` and `preflight` walks prune excluded directory subtrees before descending into them. Explicit `index --files ...` / `preflight --files ...` stay direct-file resolution paths and are not filtered by excludes.
- `embeddingTimeoutSeconds` is a per-request timeout for CodeSight embedding calls (indexing and semantic query embedding). Default: `120`.

## Overrides per repo

Place custom files in `.code-intel/` at the repo root:
- `.code-intel/parsers/` — custom tree-sitter parsers (overrides built-in)
- `.code-intel/playbooks/` — custom playbooks for the intel command

## Architecture

```
code-sight.exe
  ├── src/Types/         Core types (CodeChunk, ChunkEntry, CodeIndex)
  ├── src/Config/        code-intel.json loader, scope resolution
  ├── src/Services/      Embedding HTTP client, file hashing (SHA256)
  ├── src/Parsing/       Tree-sitter Node.js process wrapper
  ├── src/Index/         TSV + binary persistence, in-memory queries
  ├── src/Query/         21 primitives, Jint engine, FunctionStore, formatting, intel
  ├── parsers/           JS files: ts-chunker.js, chunker-core.js, languages/*
  └── playbooks/         Strategy guides: orient, plan, blast, explore, review
```

## Notes

- **All features except `intel` work without GitHub Copilot.** The `intel` command uses the [GitHub Copilot SDK](https://github.com/github/copilot-sdk) to dispatch questions to a scout model. If you don't have Copilot, you still get all 21 primitives, UDFs, the REPL, session save/load, and composition helpers.
- The embedding server must be running before `index` or `search` commands. Other commands (`modules`, `files`, `refs`, `grep`, `fn`) work offline against the cached index.
- Index data is stored in `.code-intel/` at the repo root (gitignored by default).
- `code-sight preflight` is read-only: it resolves the same config/discovery inputs as `index` and reports candidate file-set scale without creating or mutating `.code-intel` artifacts.
- `code-sight status` reads the latest indexing invocation report from `.code-intel/latest-invocation.json`. It remains the single newest-run surface even when bounded recent history is enabled.
- `code-sight history` reads a fixed-size recent history of actual `index` invocations from the index directory. Preflight and query commands are excluded, so the preflight read-only contract stays intact.
- `code-sight compare` is read-only: it compares the latest indexing invocation against the previous retained run using the existing latest/history artifacts. Legacy retained entries captured before comparison facts existed render new structural comparison fields as unavailable instead of pretending they were zero/empty.
- `code-sight index` exits `2` when structural indexing succeeds but semantic embedding work is degraded; invocation failures still exit `1`, and `index --no-embed` remains a structural-only success path.

## Future Ideas

- **bridge(entity)** — Cross-index join between code-sight and knowledge-sight refs.
- **findings(query)** — Embedding search over accumulated session findings/insights.

## Source

[github.com/micsh/Sightline](https://github.com/micsh/Sightline/tree/main/src/CodeSight)

## License

MIT
