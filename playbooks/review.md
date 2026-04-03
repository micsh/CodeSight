# Playbook: Review

Produce an architecture and code quality review.

## Output (exactly this structure)

1. **Architecture** — module structure, dependency direction
2. **Backbone types** — 3-5 most referenced types
3. **Duplication** — repeated patterns that should be extracted
4. **Complexity hotspots** — largest files
5. **Verdict** — one sentence

## Step 1: Structure + backbone (one call)

```js
let m = modules();
// Pick 2 types that appear in multiple modules' topTypes
let types = m.flatMap(x => (x.topTypes || "").split(", ")).filter(t => t.length > 2);
let counts = {};
types.forEach(t => counts[t] = (counts[t] || 0) + 1);
let top2 = Object.entries(counts).sort((a,b) => b[1] - a[1]).slice(0,2).map(x => x[0]);
let r1 = top2[0] ? refs(top2[0], {limit:20}) : [];
let r2 = top2[1] ? refs(top2[1], {limit:20}) : [];
({
  modules: m.map(x => ({name: x.module, files: x.files, chunks: x.chunks})),
  backbone: [{type: top2[0], refs: r1.length}, {type: top2[1], refs: r2.length}]
})
```

## Step 2: Duplication + complexity (one call)

```js
let big = files("").sort((a,b) => b.chunks - a.chunks).slice(0,5);
// Look for common boilerplate patterns
let dup = grep("TODO|HACK|FIXME|copy.paste|duplicat", {limit:5});
({
  largestFiles: big.map(f => ({file: f.file, chunks: f.chunks})),
  possibleDuplication: dup.map(d => ({name: d.name, file: d.file, matchLine: d.matchLine}))
})
```

## Done

2 calls. Assess architecture soundness, flag duplication, note complexity. Synthesize.
