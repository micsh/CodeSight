/**
 * C# Language Plugin — chunking, naming, and signature extraction for C#.
 */

// ─── AST Helpers ──────────────────────────────────────────────────

function childByField(node, field) {
    const child = node.childForFieldName(field);
    return child ? child.text : null;
}

function childByType(node, type) {
    for (let i = 0; i < node.childCount; i++) {
        if (node.child(i).type === type) return node.child(i);
    }
    return null;
}

// ─── getName ──────────────────────────────────────────────────────

function getName(node) {
    const name = childByField(node, 'name');
    if (name) return name;
    if (node.type === 'field_declaration') {
        const decl = childByType(node, 'variable_declaration');
        if (decl) {
            const declarator = childByType(decl, 'variable_declarator');
            if (declarator) return childByField(declarator, 'name') || 'unknown';
        }
    }
    return 'unknown';
}

// ─── contextLabel ─────────────────────────────────────────────────

function contextLabel(node) {
    if (node.type === 'namespace_declaration' || node.type === 'file_scoped_namespace_declaration') {
        const name = node.childForFieldName('name');
        if (name) return 'namespace ' + name.text;
    }
    if (node.type === 'class_declaration' || node.type === 'struct_declaration' ||
        node.type === 'interface_declaration' || node.type === 'record_declaration') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    return null;
}

// ─── Signature Extraction ─────────────────────────────────────────

const TYPE_NODE_TYPES = new Set([
    'predefined_type', 'generic_name', 'nullable_type', 'tuple_type',
    'array_type', 'pointer_type', 'qualified_name',
]);

/** Extract return type from method/property children. */
function extractReturnType(node) {
    let returnType = null;
    for (let i = 0; i < node.childCount; i++) {
        const c = node.child(i);
        if (TYPE_NODE_TYPES.has(c.type)) {
            returnType = c.text.trim();
        } else if (c.type === 'identifier' && returnType === null) {
            returnType = c.text.trim();
        } else if (c.type === 'identifier' && returnType !== null) {
            break;
        } else if (c.type === 'parameter_list') {
            break;
        }
    }
    return (returnType === 'void') ? null : returnType;
}

/** Extract params from a C# parameter_list node. */
function extractParams(paramListNode) {
    const params = [];
    for (let i = 0; i < paramListNode.childCount; i++) {
        const p = paramListNode.child(i);
        if (p.type === 'parameter') {
            let ptype = null, pname = null, hasDefault = false;
            for (let j = 0; j < p.childCount; j++) {
                const c = p.child(j);
                if (c.type === 'identifier') {
                    pname = c.text;
                } else if (c.type === '=') {
                    hasDefault = true;
                } else if (c.type !== ',' && pname === null) {
                    ptype = c.text.trim();
                }
            }
            let param = pname || '?';
            if (ptype) param += ': ' + ptype;
            if (hasDefault) param += '?';
            params.push(param);
        }
    }
    return params;
}

function extractMethodSig(node, helpers) {
    const returnType = extractReturnType(node);
    const paramList = helpers.findChild(node, 'parameter_list');
    const params = paramList ? extractParams(paramList) : [];
    let sig = '(' + params.join(', ') + ')';
    if (returnType) sig += ' → ' + returnType;
    return sig;
}

function extractPropertySig(node) {
    let propType = null;
    for (let i = 0; i < node.childCount; i++) {
        const c = node.child(i);
        if (TYPE_NODE_TYPES.has(c.type)) {
            propType = c.text.trim();
            break;
        } else if (c.type === 'identifier' && propType === null) {
            propType = c.text.trim();
        } else if (c.type === 'identifier' && propType !== null) {
            break;
        }
    }
    return propType ? '→ ' + propType : null;
}

function extractConstructorSig(node, helpers) {
    const paramList = helpers.findChild(node, 'parameter_list');
    const params = paramList ? extractParams(paramList) : [];
    return '(' + params.join(', ') + ')';
}

function extractEnumSig(node, helpers) {
    const members = [];
    const body = helpers.findChild(node, 'enum_member_declaration_list');
    if (body) {
        for (let i = 0; i < body.childCount; i++) {
            if (body.child(i).type === 'enum_member_declaration') {
                const name = helpers.findChild(body.child(i), 'identifier');
                if (name) members.push(name.text);
            }
        }
    }
    return members.length > 0 ? '| ' + members.join(' | ') : null;
}

// ─── Main extractSignature dispatch ───────────────────────────────

function extractSignature(node, kind, helpers) {
    if (node.type === 'method_declaration') {
        return extractMethodSig(node, helpers);
    }
    if (node.type === 'constructor_declaration') {
        return extractConstructorSig(node, helpers);
    }
    if (node.type === 'property_declaration') {
        return extractPropertySig(node);
    }
    if (node.type === 'enum_declaration') {
        return extractEnumSig(node, helpers);
    }
    return null;
}

// ─── Export ───────────────────────────────────────────────────────

module.exports = {
    id: 'csharp',
    extensions: ['.cs'],
    grammar: 'tree-sitter-c-sharp',

    topLevel: [
        'class_declaration', 'interface_declaration', 'struct_declaration',
        'enum_declaration', 'record_declaration', 'record_struct_declaration',
        'namespace_declaration', 'file_scoped_namespace_declaration',
        'global_statement',
    ],
    importTypes: ['using_directive'],
    memberTypes: [
        'method_declaration', 'property_declaration', 'field_declaration',
        'constructor_declaration', 'event_declaration', 'indexer_declaration',
        'operator_declaration', 'conversion_operator_declaration',
        'destructor_declaration',
    ],
    kindMap: {
        class_declaration: 'type', interface_declaration: 'type',
        struct_declaration: 'type', enum_declaration: 'du',
        record_declaration: 'record', record_struct_declaration: 'record',
        method_declaration: 'member', property_declaration: 'member',
        field_declaration: 'let', constructor_declaration: 'member',
        event_declaration: 'member', indexer_declaration: 'member',
        operator_declaration: 'member', conversion_operator_declaration: 'member',
        destructor_declaration: 'member',
        global_statement: 'let',
    },
    containerTypes: [
        'namespace_declaration', 'file_scoped_namespace_declaration',
        'class_declaration', 'struct_declaration', 'interface_declaration',
        'record_declaration', 'record_struct_declaration',
        'declaration_list',
    ],

    getName,
    contextLabel,
    extractSignature,
};
