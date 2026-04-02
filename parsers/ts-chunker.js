/**
 * Tree-sitter based code chunker — interactive JSONL protocol.
 *
 * Protocol:
 *   → stdin:  {"cmd":"parse","file":"/path/to/File.fs","maxChars":3000}
 *   ← stdout: {"ok":true,"chunks":[...]}
 *
 *   → stdin:  {"cmd":"quit"}
 *   (process exits)
 *
 * Each chunk has: { name, kind, startLine, endLine, content, context, module, filePath }
 *
 * Structure:
 *   languages/*.js   — language plugins (add a file to support a new language)
 *   chunker-core.js  — generic AST engine (stable, language-agnostic)
 *   ts-chunker.js    — parser cache, JSONL protocol (this file, stable)
 */

const Parser = require('tree-sitter');
const fs = require('fs');
const path = require('path');
const readline = require('readline');

const { LANGUAGES } = require('./languages/index');
const { extractChunks, extractImports, extractTypeRefs, extractSignatures } = require('./chunker-core');

// ─── Parser cache ─────────────────────────────────────────────────

const parsers = {};

function getParser(ext) {
    if (parsers[ext]) return parsers[ext];

    const lang = LANGUAGES[ext];
    if (!lang) return null;

    const parser = new Parser();
    const langMod = require(lang.grammar || lang.mod);
    const langObj = (lang.grammarKey || lang.key) ? langMod[lang.grammarKey || lang.key] : langMod;
    parser.setLanguage(langObj);
    parsers[ext] = { parser, lang };
    return parsers[ext];
}

// ─── Interactive JSONL protocol ───────────────────────────────────

const rl = readline.createInterface({ input: process.stdin, terminal: false });

rl.on('line', (line) => {
    try {
        const cmd = JSON.parse(line);

        if (cmd.cmd === 'quit') {
            process.exit(0);
        }

        if (cmd.cmd === 'parse') {
            const filePath = cmd.file;
            const maxChars = cmd.maxChars || 3000;
            const ext = path.extname(filePath).slice(1);

            const cached = getParser(ext);
            if (!cached) {
                console.log(JSON.stringify({ ok: false, error: `Unsupported extension: .${ext}` }));
                return;
            }

            const code = fs.readFileSync(filePath, 'utf8');
            const tree = cached.parser.parse(code);
            const chunks = extractChunks(tree, cached.lang, filePath, maxChars);

            console.log(JSON.stringify({ ok: true, chunks }));
        } else if (cmd.cmd === 'imports') {
            const filePath = cmd.file;
            const ext = path.extname(filePath).slice(1);

            const cached = getParser(ext);
            if (!cached) {
                console.log(JSON.stringify({ ok: false, error: `Unsupported extension: .${ext}` }));
                return;
            }

            const code = fs.readFileSync(filePath, 'utf8');
            const tree = cached.parser.parse(code);
            const imports = extractImports(tree, cached.lang, filePath);

            console.log(JSON.stringify({ ok: true, imports }));
        } else if (cmd.cmd === 'typerefs') {
            const filePath = cmd.file;
            const ext = path.extname(filePath).slice(1);

            const cached = getParser(ext);
            if (!cached) {
                console.log(JSON.stringify({ ok: false, error: `Unsupported extension: .${ext}` }));
                return;
            }

            const code = fs.readFileSync(filePath, 'utf8');
            const tree = cached.parser.parse(code);
            const typeRefs = extractTypeRefs(tree, cached.lang, filePath);

            console.log(JSON.stringify({ ok: true, typeRefs }));
        } else if (cmd.cmd === 'signatures') {
            const filePath = cmd.file;
            const ext = path.extname(filePath).slice(1);

            const cached = getParser(ext);
            if (!cached) {
                console.log(JSON.stringify({ ok: false, error: `Unsupported extension: .${ext}` }));
                return;
            }

            const code = fs.readFileSync(filePath, 'utf8');
            const tree = cached.parser.parse(code);
            const signatures = extractSignatures(tree, cached.lang, filePath);

            console.log(JSON.stringify({ ok: true, signatures }));
        } else if (cmd.cmd === 'languages') {
            console.log(JSON.stringify({ ok: true, languages: Object.keys(LANGUAGES) }));
        } else {
            console.log(JSON.stringify({ ok: false, error: `Unknown command: ${cmd.cmd}` }));
        }
    } catch (e) {
        console.log(JSON.stringify({ ok: false, error: e.message }));
    }
});

// Also support single-shot mode (backward compat with old splitter.js)
if (process.argv.length >= 3 && !process.stdin.isTTY) {
    // Interactive mode — handled by readline above
} else if (process.argv.length >= 3) {
    const filePath = process.argv[2];
    const maxChars = parseInt(process.argv[3] ?? '3000');
    const ext = path.extname(filePath).slice(1);

    const cached = getParser(ext);
    if (!cached) {
        console.error(`Unsupported extension: .${ext}`);
        process.exit(1);
    }

    const code = fs.readFileSync(filePath, 'utf8');
    const tree = cached.parser.parse(code);
    const chunks = extractChunks(tree, cached.lang, filePath, maxChars);
    console.log(JSON.stringify(chunks));
    process.exit(0);
}
