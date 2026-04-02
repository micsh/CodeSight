/**
 * Language Plugin Registry — auto-discovers language plugins from this directory.
 *
 * Each plugin file exports a standard interface:
 *
 *   module.exports = {
 *     // Identity
 *     id: 'fsharp',                     // unique language identifier
 *     extensions: ['.fs', '.fsi'],       // file extensions this handles
 *
 *     // Grammar
 *     grammar: 'tree-sitter-fsharp',     // npm package name
 *     grammarKey: 'fsharp',              // sub-key in grammar module (optional)
 *
 *     // Chunk config (data)
 *     topLevel: [...],                   // AST node types to chunk at top level
 *     memberTypes: [...],               // AST node types that are members inside containers
 *     importTypes: [...],               // AST node types for import/open/using
 *     kindMap: { nodeType: 'kind' },    // AST type → chunk kind (type/let/member/du/module/record)
 *
 *     // Hooks (functions)
 *     getName(node): string,            // extract declaration name from AST node
 *     contextLabel(node): string|null,  // label for context chain: "namespace X", "type Y", etc.
 *     extractSignature(node, kind, helpers): string|null,  // extract typed signature
 *     isTypeRef(text): boolean,         // is this identifier a type reference? (optional)
 *   }
 *
 * The `helpers` object passed to extractSignature contains shared utilities:
 *   - findChild(node, type): find first child of given type (1 level deep)
 *   - findTopLevelEquals(text): find the '=' that starts a body (not inside parens)
 *   - extractBalancedParens(text): extract content of first balanced ()
 *
 * To add a language: create a new .js file in this directory.
 */

const fs = require('fs');
const path = require('path');

const plugins = {};
const byExtension = {};

// Auto-discover all .js files in this directory (except index.js)
const dir = __dirname;
for (const file of fs.readdirSync(dir)) {
    if (file === 'index.js' || !file.endsWith('.js')) continue;
    try {
        const plugin = require(path.join(dir, file));
        if (!plugin.id || !plugin.extensions) {
            console.error(`Language plugin ${file}: missing id or extensions, skipping`);
            continue;
        }
        plugins[plugin.id] = plugin;
        for (const ext of plugin.extensions) {
            // Store without the dot for compatibility with current code
            byExtension[ext.replace(/^\./, '')] = plugin;
        }
    } catch (e) {
        console.error(`Failed to load language plugin ${file}: ${e.message}`);
    }
}

module.exports = { plugins, byExtension, LANGUAGES: byExtension };
