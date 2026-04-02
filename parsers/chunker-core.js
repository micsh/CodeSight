/**
 * Core chunking engine — language-agnostic AST-to-chunks logic.
 *
 * Takes a parsed tree-sitter tree and a language plugin (from languages/),
 * and produces an array of chunk objects with:
 *   { name, kind, startLine, endLine, content, context, module, filePath }
 *
 * All language-specific logic lives in language plugins (languages/*.js).
 * This file is the generic engine — it should never contain F#, C#, etc. specific code.
 */

// ─── Shared helpers (also available to language plugins) ──────────

/** Find first child of given type (searches 1 level deep). */
function findChild(node, type) {
    for (let i = 0; i < node.childCount; i++) {
        if (node.child(i).type === type) return node.child(i);
    }
    for (let i = 0; i < node.childCount; i++) {
        for (let j = 0; j < node.child(i).childCount; j++) {
            if (node.child(i).child(j).type === type) return node.child(i).child(j);
        }
    }
    return null;
}

/** Find the top-level '=' that starts the body (not inside parens/brackets). */
function findTopLevelEquals(text) {
    let depth = 0;
    let inString = false;
    for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (ch === '"') inString = !inString;
        if (inString) continue;
        if (ch === '(' || ch === '<' || ch === '[') depth++;
        if (ch === ')' || ch === '>' || ch === ']') depth--;
        if (ch === '=' && depth === 0 && i > 0 && text[i-1] !== '<' && text[i-1] !== '!' && text[i+1] !== '>') {
            return i;
        }
    }
    return -1;
}

/** Extract content of first balanced parentheses. Returns null if no parens found. */
function extractBalancedParens(text) {
    const start = text.indexOf('(');
    if (start < 0) return null;
    let depth = 0;
    for (let i = start; i < text.length; i++) {
        if (text[i] === '(') depth++;
        if (text[i] === ')') depth--;
        if (depth === 0) return text.substring(start + 1, i);
    }
    return null;
}

/** Parse a parameter list string, cleaning up attributes and whitespace. */
function parseParamList(paramText) {
    const params = [];
    let current = '';
    let depth = 0;
    for (let i = 0; i < paramText.length; i++) {
        const ch = paramText[i];
        if (ch === '(' || ch === '<' || ch === '[') depth++;
        if (ch === ')' || ch === '>' || ch === ']') depth--;
        if (ch === ',' && depth === 0) {
            if (current.trim()) params.push(current.trim());
            current = '';
        } else {
            current += ch;
        }
    }
    if (current.trim()) params.push(current.trim());

    return params.map(p => {
        let clean = p;
        while (clean.includes('[<')) {
            const start = clean.indexOf('[<');
            let depth = 0;
            let end = -1;
            for (let i = start; i < clean.length - 1; i++) {
                if (clean[i] === '[' || clean[i] === '(') depth++;
                if (clean[i] === ']' || clean[i] === ')') depth--;
                if (clean[i] === '>' && clean[i+1] === ']') {
                    end = i + 2;
                    break;
                }
            }
            if (end > start) {
                clean = (clean.substring(0, start) + clean.substring(end)).trim();
            } else break;
        }
        clean = clean.replace(/\s+/g, ' ');
        if (p.includes('Optional')) clean += '?';
        return clean;
    }).filter(p => p && p !== '?');
}

/** The helpers object passed to language plugins for signature extraction. */
const helpers = { findChild, findTopLevelEquals, extractBalancedParens, parseParamList };

// ─── Context building ─────────────────────────────────────────────

/** Walk up the tree to build a context chain using lang.contextLabel. */
function buildContext(node, lang) {
    const parts = [];
    let current = node.parent;
    while (current) {
        if (lang.contextLabel) {
            const label = lang.contextLabel(current);
            if (label) parts.unshift(label);
        }
        current = current.parent;
    }
    return parts.join('\n');
}

/** Get the module path (dot-separated) from context — uses namespace/module labels. */
function buildModuleName(node, lang) {
    const parts = [];
    let current = node.parent;
    while (current) {
        if (lang.contextLabel) {
            const label = lang.contextLabel(current);
            if (label && (label.startsWith('namespace ') || label.startsWith('module '))) {
                parts.unshift(label.replace(/^(namespace|module)\s+/, ''));
            }
        }
        current = current.parent;
    }
    return parts.join('.');
}

// ─── Member collection ────────────────────────────────────────────

/** Collect member nodes from a container, descending into body wrappers. */
function collectMembers(node, lang, members) {
    const bodyWrappers = new Set([
        'declaration_list', 'class_body', 'block', 'interface_body',
        ...(lang.containerTypes || []),
    ]);

    for (let i = 0; i < node.childCount; i++) {
        const child = node.child(i);
        if (lang.memberTypes.includes(child.type)) {
            members.push(child);
        } else if (lang.kindMap[child.type] && bodyWrappers.has(node.type)) {
            // A chunkable node inside a container body — treat as member
            members.push(child);
        } else if (bodyWrappers.has(child.type)) {
            collectMembers(child, lang, members);
        }
    }
}

// ─── Chunk emission ───────────────────────────────────────────────

function emitChunk(chunks, filePath, moduleName, name, kind, startLine, endLine, text, context, maxChars) {
    if (text.length <= maxChars) {
        chunks.push({ name, kind, startLine, endLine, content: text, context, module: moduleName, filePath });
    } else {
        // Split oversized chunks at line boundaries
        const lines = text.split('\n');
        const signature = lines[0];
        let partStart = 0;
        let partSize = 0;
        let partNum = 1;

        for (let i = 0; i < lines.length; i++) {
            const lineLen = lines[i].length + 1;
            if (partSize + lineLen > maxChars && partSize > 0) {
                const partText = lines.slice(partStart, i).join('\n');
                const partName = `${name}_part${partNum}`;
                chunks.push({
                    name: partName, kind, startLine: startLine + partStart,
                    endLine: startLine + i - 1,
                    content: partNum > 1 ? `// continued: ${signature}\n${partText}` : partText,
                    context, module: moduleName, filePath
                });
                partStart = i;
                partSize = lineLen;
                partNum++;
            } else {
                partSize += lineLen;
            }
        }
        // Emit remaining
        if (partStart < lines.length) {
            const partText = lines.slice(partStart).join('\n');
            const partName = partNum > 1 ? `${name}_part${partNum}` : name;
            chunks.push({
                name: partName, kind, startLine: startLine + partStart,
                endLine: endLine,
                content: partNum > 1 ? `// continued: ${signature}\n${partText}` : partText,
                context, module: moduleName, filePath
            });
        }
    }
}

// ─── Node chunking ────────────────────────────────────────────────

function chunkNode(node, lang, filePath, maxChars, chunks) {
    const kind = lang.kindMap[node.type];
    if (!kind) return;

    const name = lang.getName(node);
    const text = node.text;
    const startLine = node.startPosition.row + 1;
    const endLine = node.endPosition.row + 1;
    const context = buildContext(node, lang);
    const moduleName = buildModuleName(node, lang);

    // If it's a container type (class/type/struct/impl) and too large, split into members
    const isContainer = ['type', 'record'].includes(kind) || node.type === 'impl_item';
    if (isContainer && text.length > maxChars && lang.memberTypes.length > 0) {
        const members = [];
        collectMembers(node, lang, members);

        if (members.length > 0) {
            // Emit type header (everything before first member)
            const firstMemberLine = members[0].startPosition.row;
            const headerLines = text.split('\n').slice(0, firstMemberLine - node.startPosition.row);
            if (headerLines.length > 0 && headerLines.join('\n').trim().length > 0) {
                emitChunk(chunks, filePath, moduleName, name, kind,
                    startLine, firstMemberLine, headerLines.join('\n'), context, maxChars);
            }

            // Emit each member
            for (const member of members) {
                const memberName = lang.getName(member);
                const memberKind = lang.kindMap[member.type] || 'member';
                const memberContext = context ? `${context}\ntype ${name}` : `type ${name}`;
                const fullName = `${name}.${memberName}`;
                emitChunk(chunks, filePath, moduleName, fullName, memberKind,
                    member.startPosition.row + 1, member.endPosition.row + 1,
                    member.text, memberContext, maxChars);
            }
            return;
        }
    }

    // Emit as a single chunk (or split if too large)
    emitChunk(chunks, filePath, moduleName, name, kind, startLine, endLine, text, context, maxChars);
}

// ─── Tree extraction ──────────────────────────────────────────────

function extractChunks(tree, lang, filePath, maxChars) {
    const chunks = [];

    function walk(node) {
        if (lang.topLevel.includes(node.type)) {
            // For namespaces/modules, walk inside but don't chunk the container itself
            if (node.type === 'namespace_declaration' || node.type === 'file_scoped_namespace_declaration') {
                for (let i = 0; i < node.childCount; i++) walk(node.child(i));
                return;
            }
            chunkNode(node, lang, filePath, maxChars, chunks);
        } else {
            for (let i = 0; i < node.childCount; i++) walk(node.child(i));
        }
    }

    walk(tree.rootNode);
    return chunks;
}

// ─── Import extraction ─────────────────────────────────────────────

/** Extract import/open/using statements from a parsed tree. */
function extractImports(tree, lang, filePath) {
    const imports = [];
    const importTypes = lang.importTypes || [];
    if (importTypes.length === 0) return imports;

    function walk(node) {
        if (importTypes.includes(node.type)) {
            // Extract the module/namespace being imported
            let module = '';
            // F#: import_decl children are 'open' keyword + long_identifier
            // C#: using_directive has a 'name' field or child qualified_name
            // JS/TS: import_statement has a 'source' field (string literal)
            // Python: import_statement/import_from_statement — grab text
            // Go: import_declaration may contain import_spec_list
            // Rust: use_declaration has a 'path' child

            const nameChild = node.childForFieldName('name');
            if (nameChild) {
                module = nameChild.text;
            } else {
                // Walk children for identifiers/qualified names
                for (let i = 0; i < node.childCount; i++) {
                    const c = node.child(i);
                    if (c.type === 'long_identifier' || c.type === 'qualified_name' ||
                        c.type === 'identifier' || c.type === 'scoped_identifier') {
                        module = c.text;
                        break;
                    }
                    // JS/TS: source is a string
                    if (c.type === 'string' || c.type === 'string_literal') {
                        module = c.text.replace(/['"]/g, '');
                        break;
                    }
                }
            }
            if (!module) {
                // Fallback: clean up the raw text
                module = node.text
                    .replace(/^(open|using|import|from|use)\s+/, '')
                    .replace(/;?\s*$/, '')
                    .trim();
            }

            imports.push({
                module,
                line: node.startPosition.row + 1,
                raw: node.text.trim()
            });
        } else {
            for (let i = 0; i < node.childCount; i++) walk(node.child(i));
        }
    }

    walk(tree.rootNode);
    return imports;
}

// ─── Type reference extraction ─────────────────────────────────────

/**
 * Extract type-like identifier references from a parsed tree.
 * Returns identifiers that are USED (not defined) — PascalCase names
 * that appear as AST tokens outside of definition-name positions.
 *
 * This is much more precise than string matching because it:
 *   - Ignores identifiers in comments and strings
 *   - Excludes definition sites (the name being defined)
 *   - Only finds actual AST tokens (not substrings)
 */
function extractTypeRefs(tree, lang, filePath) {
    const refs = new Set();
    const definitions = new Set();

    // First pass: collect all definition names (so we can exclude them as "def sites")
    function collectDefs(node) {
        if (lang.kindMap[node.type]) {
            const name = lang.getName(node);
            if (name && name !== 'unknown') {
                // Store the short name (last part of dotted name)
                const shortName = name.split('.').pop();
                definitions.add(`${node.startPosition.row}:${shortName}`);
            }
        }
        for (let i = 0; i < node.childCount; i++) collectDefs(node.child(i));
    }
    collectDefs(tree.rootNode);

    // Second pass: collect all PascalCase identifiers that aren't at definition sites
    function collectRefs(node) {
        if (node.type === 'identifier' || node.type === 'type_name') {
            const text = node.text.split('<')[0].trim(); // strip generic params
            if (text.length >= 4 &&
                text[0] >= 'A' && text[0] <= 'Z' &&
                !definitions.has(`${node.startPosition.row}:${text}`)) {
                refs.add(text);
            }
        } else if (node.type === 'long_identifier') {
            // For dotted identifiers like "IPostToBoard.Post", extract the first part
            const parts = node.text.split('.');
            const first = parts[0].trim();
            if (first.length >= 4 &&
                first[0] >= 'A' && first[0] <= 'Z' &&
                !definitions.has(`${node.startPosition.row}:${first}`)) {
                refs.add(first);
            }
        }
        // Don't descend into string literals or comments
        if (node.type === 'string' || node.type === 'string_literal' ||
            node.type === 'verbatim_string' || node.type === 'triple_quoted_string' ||
            node.type === 'comment' || node.type === 'block_comment' ||
            node.type === 'xml_doc') {
            return;
        }
        for (let i = 0; i < node.childCount; i++) collectRefs(node.child(i));
    }
    collectRefs(tree.rootNode);

    return Array.from(refs).sort();
}

// ─── Signature extraction ──────────────────────────────────────────

/**
 * Extract signatures from all chunkable declarations.
 * Delegates to lang.extractSignature() for language-specific logic.
 * Returns array of { name, kind, signature, filePath, startLine }
 */
function extractSignatures(tree, lang, filePath) {
    if (!lang.extractSignature) return [];

    const sigs = [];
    const containerSet = new Set(lang.containerTypes || []);

    function walk(node, depth) {
        if (depth > 8) return;

        const kind = lang.kindMap ? lang.kindMap[node.type] : null;

        if (kind) {
            const name = lang.getName(node);
            const startLine = node.startPosition.row + 1;
            const sig = lang.extractSignature(node, kind, helpers);

            if (sig) {
                sigs.push({ name, kind, signature: sig, filePath, startLine });
            }
        }

        // Continue walking into containers
        if (!kind || containerSet.has(node.type)) {
            for (let i = 0; i < node.childCount; i++) walk(node.child(i), depth + 1);
        }
    }

    walk(tree.rootNode, 0);
    return sigs;
}

module.exports = { extractChunks, extractImports, extractTypeRefs, extractSignatures, helpers };