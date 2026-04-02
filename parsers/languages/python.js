/**
 * Python Language Plugin — chunking and naming for Python.
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
    if (node.type === 'class_definition') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    return null;
}

// ─── Signature Extraction ─────────────────────────────────────────

// TODO: implement signature extraction for Python
function extractSignature(_node, _kind, _helpers) {
    return null;
}

// ─── Export ───────────────────────────────────────────────────────

module.exports = {
    id: 'python',
    extensions: ['.py'],
    grammar: 'tree-sitter-python',

    topLevel: [
        'function_definition', 'class_definition',
        'decorated_definition',
    ],
    importTypes: ['import_statement', 'import_from_statement'],
    memberTypes: ['function_definition'],
    kindMap: {
        function_definition: 'let', class_definition: 'type',
        decorated_definition: 'let',
    },
    containerTypes: ['class_definition'],

    getName,
    contextLabel,
    extractSignature,
};
