/**
 * JavaScript Language Plugin — chunking and naming for JS.
 * Signature extraction not yet implemented.
 */

// ─── getName ──────────────────────────────────────────────────────

function getName(node) {
    if (node.type === 'export_statement') {
        for (let i = 0; i < node.childCount; i++) {
            const c = node.child(i);
            if (c.type === 'function_declaration' || c.type === 'class_declaration' ||
                c.type === 'lexical_declaration' || c.type === 'variable_declaration') {
                return getName(c);
            }
        }
    }
    const name = node.childForFieldName('name');
    if (name) return name.text;
    for (let i = 0; i < node.childCount; i++) {
        if (node.child(i).type === 'identifier') return node.child(i).text;
    }
    return 'unknown';
}

// ─── contextLabel ─────────────────────────────────────────────────

function contextLabel(node) {
    if (node.type === 'class_declaration') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    return null;
}

// ─── Signature Extraction ─────────────────────────────────────────

// TODO: implement signature extraction for JavaScript
function extractSignature(_node, _kind, _helpers) {
    return null;
}

// ─── Export ───────────────────────────────────────────────────────

module.exports = {
    id: 'javascript',
    extensions: ['.js'],
    grammar: 'tree-sitter-javascript',

    topLevel: [
        'function_declaration', 'class_declaration',
        'lexical_declaration', 'variable_declaration',
        'export_statement',
    ],
    importTypes: ['import_statement'],
    memberTypes: ['method_definition', 'field_definition'],
    kindMap: {
        function_declaration: 'let', class_declaration: 'type',
        lexical_declaration: 'let', variable_declaration: 'let',
        export_statement: 'let',
        method_definition: 'member', field_definition: 'let',
    },
    containerTypes: ['class_declaration', 'class_body'],

    getName,
    contextLabel,
    extractSignature,
};
