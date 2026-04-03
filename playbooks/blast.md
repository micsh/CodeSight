# Playbook: Blast

Produce an impact assessment for changing a type, function, or module.

## Output (exactly this structure)

1. **Blast radius** — N files, M direct references
2. **How it's used** — group by usage pattern, quote matchLines
3. **Risk zones** — most fragile references
4. **Safe changes** — what won't break callers
5. **Related types** — anything similar that might need the same change

## Step 1: Find definition + all references (one call)

```js
// Replace TARGET_NAME with the actual type/function name
let def = search("TARGET_NAME", {limit:1});
let r = refs("TARGET_NAME", {limit:30});
let byFile = {};
r.forEach(x => { if (!byFile[x.file]) byFile[x.file] = []; byFile[x.file].push(x.matchLine); });
({
  definedIn: def[0] ? def[0].file + ":" + def[0].line : "?",
  totalRefs: r.length,
  fileCount: Object.keys(byFile).length,
  byFile: byFile
})
```

## Step 2: Module-level impact (one call)

```js
let d = deps("TARGET_NAME");
let imp = impact("TARGET_NAME");
({
  moduleDependers: d,
  typeImpactFiles: imp.map(i => i.file)
})
```

## Done

2 calls. The matchLines from Step 1 tell you HOW it's used — categorize into constructor calls, type annotations, method calls. Synthesize.

If a specific reference looks risky, ONE `expand()` call to see full context.
