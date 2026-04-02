/**
 * Rust Language Plugin — chunking and naming for Rust.
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
    if (node.type === 'impl_item') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    if (node.type === 'trait_item') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    if (node.type === 'mod_item') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'module ' + n;
    }
    return null;
}

// ─── Signature Extraction ─────────────────────────────────────────

// TODO: implement signature extraction for Rust
function extractSignature(_node, _kind, _helpers) {
    return null;
}

// ─── Export ───────────────────────────────────────────────────────

module.exports = {
    id: 'rust',
    extensions: ['.rs'],
    grammar: 'tree-sitter-rust',

    topLevel: [
        'function_item', 'impl_item', 'struct_item',
        'enum_item', 'trait_item', 'mod_item', 'type_item',
    ],
    importTypes: ['use_declaration'],
    memberTypes: ['function_item'],
    kindMap: {
        function_item: 'let', impl_item: 'type',
        struct_item: 'record', enum_item: 'du',
        trait_item: 'type', mod_item: 'module',
        type_item: 'type',
    },
    containerTypes: ['impl_item', 'trait_item', 'mod_item'],

    getName,
    contextLabel,
    extractSignature,
};
