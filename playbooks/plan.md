# Playbook: Plan

Produce an implementation plan for a feature.

## Output (exactly this structure)

1. **Edit targets** — 1-3 files to modify, in order, with line refs
2. **Pattern to follow** — one existing example to mimic (include ref ID)
3. **Wiring point** — where to register/connect the new code
4. **Dependencies** — what imports are needed
5. **Risks** — anything tightly coupled (or "none identified")

## Step 1: Find relevant code (one call)

```js
// Replace FEATURE_DESCRIPTION with the user's actual request
let hits = search("FEATURE_DESCRIPTION", {limit:8});
({
  top5: hits.slice(0,5).map(h => ({name: h.name, file: h.file, line: h.line, score: h.score, sig: h.signature, preview: h.preview}))
})
```

## Step 2: Understand the pattern and context (one call)

Take the best result's ref ID and explore its neighborhood + find similar code:

```js
// Use the actual ref ID from Step 1 (e.g. R1, R2)
let n = neighborhood("REF_ID", {before:2, after:2});
let sim = similar("REF_ID", {limit:3});
({
  file: n.file, imports: n.imports,
  before: n.before.map(c => c.name + " " + c.summary),
  target: n.target.name + " " + n.target.summary,
  after: n.after.map(c => c.name + " " + c.summary),
  similar: sim.map(s => ({name: s.name, file: s.file, preview: s.preview}))
})
```

## Step 3: Find wiring/registration (one call)

```js
let wiring = grep("register|wire|create.*handler|add.*tool|setup|configure", {limit:5});
({results: wiring.map(w => ({name: w.name, file: w.file, matchLine: w.matchLine}))})
```

## Done

3 calls. Synthesize the plan. If a wiring point is unclear, ONE more `context("file")` call.
