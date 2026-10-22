# PreToolUse guard for VIGO conventions (see CLAUDE.md).
# Reads the hook payload on stdin; exit 2 blocks the tool call and shows stderr to Claude.
$ErrorActionPreference = 'Stop'

$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$tool = $payload.tool_name
$in = $payload.tool_input

function Block([string]$reason) {
    [Console]::Error.WriteLine("VIGO guard: $reason")
    exit 2
}

# 1. Branches are the user's job. Commits are allowed through git.exe only (never an MCP git tool), under the rules of
#    Test-CommitCommand: English message, no co-author, no session reference, signing and hooks kept.
if ($tool -match '^mcp__.*__git_(commit|branch)$') {
    Block 'use git.exe for commits (git commit -F <file> or -m, English, no co-author or session line); branches are made by the user.'
}

function Test-CommitCommand([string]$cmd, [string]$workDir) {
    # Message text = the command itself (-m, heredoc) plus the content of a -F / --file file.
    $text = $cmd
    if ($cmd -match '(?:-F|--file)(?:\s+|=)(["'']?)([^\s"'']+)\1') {
        $file = $Matches[2]
        if ($file -ne '-') {
            if (-not [IO.Path]::IsPathRooted($file) -and $workDir) { $file = Join-Path $workDir $file }
            if (Test-Path -LiteralPath $file) { $text += "`n" + [IO.File]::ReadAllText($file) }
            else { Block "commit message file $file not found: write it first (Write tool)." }
        }
    }
    if ($cmd -match '--no-verify|--no-gpg-sign|\s-n\s|commit\.gpgsign\s*=\s*false|--author|--reset-author') {
        Block 'commits keep hooks, GPG signing and the configured author: no --no-verify, --no-gpg-sign, commit.gpgsign=false or --author.'
    }
    if ($text -match '(?i)co-authored-by|\bclaude\b|anthropic|generated with|signed-off-by|https?://\S*session|session[_ -]?(id|url|link)\b') {
        Block 'commit message: no co-author, no Claude / Anthropic mention, no session reference.'
    }
    if ($text -match '[^\x00-\x7F]' -or $text -cmatch '\b(le|la|les|des|du|pour|avec|dans|une|sans|est|sur|ajout|correction)\b') {
        Block 'commit messages are written in English (no accents, no French words).'
    }
}

if ($tool -in 'Bash', 'PowerShell') {
    $cmd = [string]$in.command
    $git = '\bgit(\s+(-c\s+\S+|-C\s+\S+|--\S+))*\s+'
    if ($cmd -match "${git}commit\b") {
        Test-CommitCommand $cmd ([string]$payload.cwd)
    }
    if ($cmd -match "${git}(checkout\s+(\S+\s+)*-[bB]\b|switch\s+(\S+\s+)*(-[cC]\b|--create\b|--force-create\b)|branch\s+(?!-)[^\s|;&]+|worktree\s+add\s+(\S+\s+)*-[bB]\b)") {
        Block 'never create a branch: work on the current branch, the user manages branches.'
    }
    exit 0
}

# 2. File edits: generated files and AOT rules.
$path = [string]$in.file_path
if (-not $path) { exit 0 }
$norm = $path -replace '\\', '/'

if ($norm -match '\.g\.cs$' -or $norm -match '/Themes/Tokens\.xaml$') {
    Block "$path is generated. Change the generator instead (node tools/gen-tokens.mjs docs/design/VIGO.dc.html.txt, node tools/gen-icons.mjs tools/FluentSystemIcons-Regular.json docs/design/*.txt)."
}
if ($norm -match '/src/Vigo\.App/Editor/') {
    Block "$path is the generated code editor bundle. Change tools/editor/src instead, then: cd tools/editor; npm ci; node build.mjs."
}

$texts = @()
if ($in.content) { $texts += [string]$in.content }
if ($in.new_string) { $texts += [string]$in.new_string }
if ($in.edits) { $texts += $in.edits | ForEach-Object { [string]$_.new_string } }
$text = $texts -join "`n"

if ($norm -match '\.xaml$' -and $text -match '\{Binding\b') {
    Block '{Binding} is not AOT-safe: use x:Bind with a typed x:DataType.'
}
if ($norm -match '\.cs$' -and $text -match 'Application\.Current\.Resources\s*\[') {
    Block 'do not read/cast Application.Current.Resources in C# (InvalidCastException under AOT): use {ThemeResource} in XAML or DesignTokens for Win2D.'
}

exit 0
