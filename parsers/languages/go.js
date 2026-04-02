/**
 * Go Language Plugin — chunking and naming for Go.
 * Signature extraction not yet implemented.
 */

// ─── getName ──────────────────────────────────────────────────────

function getName(node) {
    const name = node.childForFieldName('name');
    if (name) return name.text;
    return 'unknown';
}

// ─── contextLabel ─────────────────────────────────────────────────

function contextLabel(node) {
    if (node.type === 'type_declaration') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    return null;
}

// ─── Signature Extraction ─────────────────────────────────────────

// TODO: implement signature extraction for Go
function extractSignature(_node, _kind, _helpers) {
    return null;
}

// ─── Export ───────────────────────────────────────────────────────

module.exports = {
    id: 'go',
    extensions: ['.go'],
    grammar: 'tree-sitter-go',

    topLevel: [
        'function_declaration', 'method_declaration',
        'type_declaration',
    ],
    importTypes: ['import_declaration'],
    memberTypes: [],
    kindMap: {
        function_declaration: 'let', method_declaration: 'member',
        type_declaration: 'type',
    },
    containerTypes: ['type_declaration'],

    getName,
    contextLabel,
    extractSignature,
};
