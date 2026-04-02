/**
 * F# Language Plugin — chunking, naming, and signature extraction for F#.
 */

// ─── AST Helpers (shared patterns) ────────────────────────────────

function childByType(node, type) {
    for (let i = 0; i < node.childCount; i++) {
        if (node.child(i).type === type) return node.child(i);
    }
    return null;
}

function findFirstIdentifier(node) {
    if (node.type === 'identifier' || node.type === 'long_identifier') return node.text;
    for (let i = 0; i < node.childCount; i++) {
        const result = findFirstIdentifier(node.child(i));
        if (result) return result;
    }
    return null;
}

// ─── getName ──────────────────────────────────────────────────────

function getName(node) {
    if (node.type === 'value_declaration' || node.type === 'function_or_value_defn') {
        const target = node.type === 'value_declaration'
            ? childByType(node, 'function_or_value_defn') || node
            : node;
        for (let i = 0; i < target.childCount; i++) {
            const c = target.child(i);
            if (c.type === 'function_declaration_left' || c.type === 'value_declaration_left') {
                return findFirstIdentifier(c) || 'unknown';
            }
            if (c.type === 'identifier') return c.text;
        }
        if (node.type === 'value_declaration') {
            const fovd = childByType(node, 'function_or_value_defn');
            if (fovd) return getName(fovd);
        }
        return findFirstIdentifier(target) || 'unknown';
    }
    if (node.type === 'type_definition') {
        const tn = childByType(node, 'type_name');
        if (tn) return tn.text.split('<')[0].trim();
        return findFirstIdentifier(node) || 'unknown';
    }
    if (node.type === 'module_defn') {
        const lid = childByType(node, 'long_identifier');
        if (lid) return lid.text;
        return findFirstIdentifier(node) || 'unknown';
    }
    if (node.type === 'member_defn') {
        const text = node.text;
        const m = text.match(/member\s+(?:\w+\.)?(\w+)/);
        if (m) return m[1];
        const m2 = text.match(/override\s+(?:\w+\.)?(\w+)/);
        if (m2) return m2[1];
        return findFirstIdentifier(node) || 'unknown';
    }
    return findFirstIdentifier(node) || 'unknown';
}

// ─── contextLabel ─────────────────────────────────────────────────

function contextLabel(node) {
    if (node.type === 'namespace_declaration' || node.type === 'file_scoped_namespace_declaration') {
        const name = node.childForFieldName('name');
        if (name) return 'namespace ' + name.text;
    }
    if (node.type === 'module_defn') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'module ' + n;
    }
    if (node.type === 'type_definition') {
        const n = getName(node);
        if (n && n !== 'unknown') return 'type ' + n;
    }
    return null;
}

// ─── Signature Extraction ─────────────────────────────────────────

/** Extract params from an argument_patterns node. */
function extractParams(argNode, helpers) {
    const params = [];
    for (let i = 0; i < argNode.childCount; i++) {
        const c = argNode.child(i);
        if (c.type === 'typed_pattern') {
            const ident = helpers.findChild(c, 'identifier_pattern') || helpers.findChild(c, 'identifier');
            let ptype = null;
            for (let j = 0; j < c.childCount; j++) {
                if (c.child(j).type === ':') {
                    const next = c.child(j + 1);
                    if (next) ptype = next.text.trim();
                }
            }
            const pname = ident ? ident.text : '?';
            params.push(pname + (ptype ? ': ' + ptype : ''));
        } else if (c.type === 'identifier_pattern' || c.type === 'identifier') {
            params.push(c.text);
        } else if (c.type === 'long_identifier' || c.type === 'long_identifier_or_op') {
            const text = c.text.trim();
            if (text && text !== '()') params.push(text);
        } else if (c.type === 'const') {
            // Unit param — handled by hasArgPatterns flag
        } else if (c.type === 'paren_pattern' || c.type === 'tuple_pattern') {
            const text = c.text.replace(/^\(/, '').replace(/\)$/, '').trim();
            if (text) params.push(text);
        } else if (c.type === 'attribute_pattern') {
            for (let j = 0; j < c.childCount; j++) {
                if (c.child(j).type === 'typed_pattern' || c.child(j).type === 'identifier_pattern') {
                    const inner = c.child(j);
                    if (inner.type === 'typed_pattern') {
                        const ident2 = helpers.findChild(inner, 'identifier_pattern') || helpers.findChild(inner, 'identifier');
                        let ptype2 = null;
                        for (let k = 0; k < inner.childCount; k++) {
                            if (inner.child(k).type === ':') {
                                const next = inner.child(k + 1);
                                if (next) ptype2 = next.text.trim();
                            }
                        }
                        params.push((ident2 ? ident2.text : '?') + (ptype2 ? ': ' + ptype2 : '') + '?');
                    } else {
                        params.push(inner.text + '?');
                    }
                }
            }
        }
    }
    return params;
}

/** Extract params from a paren_pattern (value_declaration_left functions). */
function extractParamsFromParen(parenNode, helpers) {
    const params = [];
    for (let i = 0; i < parenNode.childCount; i++) {
        const c = parenNode.child(i);
        if (c.type === 'typed_pattern') {
            const ident = helpers.findChild(c, 'identifier_pattern') || helpers.findChild(c, 'identifier');
            let ptype = null;
            for (let j = 0; j < c.childCount; j++) {
                if (c.child(j).type === ':') {
                    const next = c.child(j + 1);
                    if (next) ptype = next.text.trim();
                }
            }
            const pname = ident ? ident.text : '?';
            params.push(pname + (ptype ? ': ' + ptype : ''));
        } else if (c.type === 'identifier_pattern' || c.type === 'identifier') {
            params.push(c.text);
        } else if (c.type === 'tuple_pattern') {
            const text = c.text.trim();
            if (text) params.push(text);
        }
    }
    return params;
}

function extractFunctionSig(node, helpers) {
    let params = [];
    let returnType = null;
    let hasArgPatterns = false;

    for (let i = 0; i < node.childCount; i++) {
        const c = node.child(i);
        if (c.type === 'function_declaration_left') {
            const argPatterns = helpers.findChild(c, 'argument_patterns');
            if (argPatterns) {
                hasArgPatterns = true;
                params = extractParams(argPatterns, helpers);
            }
        }
        if (c.type === 'value_declaration_left') {
            const identPat = helpers.findChild(c, 'identifier_pattern');
            if (identPat) {
                for (let j = 0; j < identPat.childCount; j++) {
                    const pp = identPat.child(j);
                    if (pp.type === 'paren_pattern') {
                        hasArgPatterns = true;
                        params = params.concat(extractParamsFromParen(pp, helpers));
                    }
                }
            }
        }
        if (c.type === ':') {
            const next = node.child(i + 1);
            if (next && next.type !== '=') {
                returnType = next.text.split('\n')[0].trim();
            }
        }
    }

    if (!hasArgPatterns && returnType === null) return null;

    let sig = params.length > 0
        ? '(' + params.join(', ') + ')'
        : '()';
    if (returnType) sig += ' → ' + returnType;
    return sig;
}

function extractMemberSig(node, helpers) {
    for (let i = 0; i < node.childCount; i++) {
        const c = node.child(i);
        if (c.type === 'method_or_prop_defn') {
            const fullText = c.text;
            const eqIdx = helpers.findTopLevelEquals(fullText);
            if (eqIdx < 0) return null;

            const sigText = fullText.substring(0, eqIdx).trim();
            const nameMatch = sigText.match(/^\w+\.(\w+)/);
            if (!nameMatch) return null;

            const nameEnd = sigText.indexOf(nameMatch[0]) + nameMatch[0].length;
            const afterName = sigText.substring(nameEnd);
            const parenContent = helpers.extractBalancedParens(afterName);

            if (parenContent !== null) {
                const params = helpers.parseParamList(parenContent);
                let returnType = null;
                const colonAfterParens = afterName.lastIndexOf(':');
                if (colonAfterParens > afterName.indexOf(parenContent)) {
                    returnType = afterName.substring(colonAfterParens + 1).trim();
                }
                let sig = '(' + params.join(', ') + ')';
                if (returnType) sig += ' → ' + returnType;
                return sig;
            } else {
                const retMatch = sigText.match(/:\s+(\S[\w<>,\s]*\S)\s*$/);
                if (retMatch) return '→ ' + retMatch[1].trim();
                return null;
            }
        }
    }
    return null;
}

function extractTypeSig(node, helpers) {
    for (let i = 0; i < node.childCount; i++) {
        const c = node.child(i);

        if (c.type === 'record_type_defn') {
            const fields = [];
            const rf = helpers.findChild(c, 'record_fields');
            if (rf) {
                for (let j = 0; j < rf.childCount; j++) {
                    if (rf.child(j).type === 'record_field') {
                        const field = rf.child(j);
                        const fname = helpers.findChild(field, 'identifier');
                        let ftype = null;
                        for (let k = 0; k < field.childCount; k++) {
                            if (field.child(k).type === ':') {
                                const next = field.child(k + 1);
                                if (next) ftype = next.text.trim();
                            }
                        }
                        if (fname) {
                            fields.push(fname.text + (ftype ? ': ' + ftype : ''));
                        }
                    }
                }
            }
            return fields.length > 0 ? '{ ' + fields.join('; ') + ' }' : null;
        }

        if (c.type === 'union_type_defn') {
            const cases = [];
            const utc = helpers.findChild(c, 'union_type_cases');
            if (utc) {
                for (let j = 0; j < utc.childCount; j++) {
                    if (utc.child(j).type === 'union_type_case') {
                        cases.push(utc.child(j).text.split('\n')[0].trim());
                    }
                }
            }
            return cases.length > 0 ? '| ' + cases.join(' | ') : null;
        }

        if (c.type === 'anon_type_defn' || c.type === 'type_extension_elements') {
            const tn = helpers.findChild(node, 'type_name');
            if (tn) {
                const text = tn.text;
                const m = text.match(/\(([^)]+)\)/);
                if (m) {
                    return '(' + m[1].split(',').map(p => p.trim()).join(', ') + ')';
                }
            }
        }
    }
    return null;
}

// ─── Main extractSignature dispatch ───────────────────────────────

function extractSignature(node, kind, helpers) {
    if (node.type === 'type_definition') {
        return extractTypeSig(node, helpers);
    }
    if (node.type === 'member_defn') {
        return extractMemberSig(node, helpers);
    }
    if (node.type === 'value_declaration' || node.type === 'function_or_value_defn') {
        return extractFunctionSig(node, helpers);
    }
    return null;
}

// ─── Export ───────────────────────────────────────────────────────

module.exports = {
    id: 'fsharp',
    extensions: ['.fs', '.fsi'],
    grammar: 'tree-sitter-fsharp',
    grammarKey: 'fsharp',

    topLevel: [
        'module_defn', 'type_definition', 'value_declaration',
        'namespace_declaration',
    ],
    importTypes: ['import_decl'],
    memberTypes: [
        'member_defn', 'additional_constr_defn', 'function_or_value_defn',
    ],
    kindMap: {
        module_defn: 'module', type_definition: 'type',
        value_declaration: 'let',
        member_defn: 'member', additional_constr_defn: 'member',
        function_or_value_defn: 'let',
        union_type_case: 'du',
    },
    containerTypes: [
        'namespace_declaration', 'file_scoped_namespace_declaration',
        'module_defn', 'type_definition', 'anon_type_defn',
        'declaration_list', 'type_extension_elements',
    ],

    getName,
    contextLabel,
    extractSignature,
};
