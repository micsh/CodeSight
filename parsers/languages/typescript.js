/**
 * TypeScript Language Plugin — chunking and naming for TS.
 * Signature extraction not yet implemented.
 */

// ─── getName ──────────────────────────────────────────────────────

function getName(node) {
    if (node.type === 'export_statement') {
        for (let i = 0; i < node.childCount; i++) {
            const c = node.child(i);
            if (c.type === 'function_declaration' || c.type === 'class_declaration' ||
                c.type === 'interface_declaration' || c.type === 'type_alias_declaration' ||
                c.type === 'enum_declaration' || c.type === 'lexical_declaration' ||
                c.type === 'variable_declaration') {
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
    if (node.type === 'interface_declaration') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    return null;
}

// ─── Signature Extraction ─────────────────────────────────────────

// TODO: implement signature extraction for TypeScript
function extractSignature(_node, _kind, _helpers) {
    return null;
}

// ─── Export ───────────────────────────────────────────────────────

module.exports = {
    id: 'typescript',
    extensions: ['.ts'],
    grammar: 'tree-sitter-typescript',
    grammarKey: 'typescript',

    topLevel: [
        'function_declaration', 'class_declaration',
        'interface_declaration', 'type_alias_declaration',
        'enum_declaration', 'lexical_declaration',
        'variable_declaration', 'export_statement',
    ],
    importTypes: ['import_statement'],
    memberTypes: ['method_definition', 'public_field_definition', 'method_signature'],
    kindMap: {
        function_declaration: 'let', class_declaration: 'type',
        interface_declaration: 'type', type_alias_declaration: 'type',
        enum_declaration: 'du', lexical_declaration: 'let',
        variable_declaration: 'let', export_statement: 'let',
        method_definition: 'member', public_field_definition: 'let',
        method_signature: 'member',
    },
    containerTypes: ['class_declaration', 'interface_declaration', 'class_body'],

    getName,
    contextLabel,
    extractSignature,
};
