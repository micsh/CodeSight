@echo off
echo Setting up CodeSight parsers...
cd /d "%~dp0parsers"
call npm install
echo.
echo Setup complete. Tree-sitter grammars installed.
echo Note: F# support requires tree-sitter-fsharp to be built separately.
echo See: https://github.com/ionide/tree-sitter-fsharp
