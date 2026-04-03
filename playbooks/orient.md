# Playbook: Orient

Produce a codebase orientation brief.

## Output (exactly this structure)

1. **What it is** — one sentence
2. **Entry points** — 2-3 files where execution starts, with line refs
3. **Key modules** — 4-6 modules, each with a one-line role description
4. **Backbone types** — 3-5 types that are referenced widely
5. **Dependency flow** — one sentence: what depends on what

## Step 1: Get the landscape (one call)

```js
let m = modules();
let topModule = m.sort((a,b) => b.chunks - a.chunks)[0];
({
  moduleCount: m.length,
  modules: m.map(x => ({name: x.module, files: x.files, chunks: x.chunks, types: x.topTypes})),
  largestModule: topModule.module
})
```

This gives you the full module map. You can already fill "Key modules" from the names and types.

## Step 2: Find entry points (one call)

From Step 1, identify the module that looks like an app host or entry point. Then inspect its key files:

```js
// Adapt these file names based on what you saw in Step 1
let f1 = context("MAIN_FILE_HERE");
let f2 = context("SECOND_FILE_HERE");
({
  file1: {name: "MAIN_FILE_HERE", chunks: f1.chunks.length, imports: f1.imports, top: f1.chunks.slice(0,3).map(c => c.name + " L" + c.line)},
  file2: {name: "SECOND_FILE_HERE", chunks: f2.chunks.length, imports: f2.imports, top: f2.chunks.slice(0,3).map(c => c.name + " L" + c.line)}
})
```

If you can't guess the file names, use `search("main entry point startup", {limit:3})` instead.

## Step 3: Confirm backbone types (one call)

Pick 2-3 types from the `topTypes` in Step 1 that appear across modules:

```js
// Adapt type names from what Step 1 returned
let r1 = refs("TYPE_A", {limit:20});
let r2 = refs("TYPE_B", {limit:20});
({
  typeA: {refs: r1.length, files: [...new Set(r1.map(r => r.file))]},
  typeB: {refs: r2.length, files: [...new Set(r2.map(r => r.file))]}
})
```

## Done

3 calls. Synthesize all 5 sections and respond.

If a module is unclear, you may run ONE more `context("file")` call. But stop at 4 total.
