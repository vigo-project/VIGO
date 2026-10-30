# VIGO — native Windows Git client

Native Windows WinUI 3 Git client, fast and lightweight.
The name **VIGO** stands for **Visual Git Overview** (to reuse in the app: "About" screen, logo tooltip,
MSIX package description).

## Working method

- Before any structural technical choice: present the options and wait for approval.
- Prefix shell commands with `rtk` (token-saving proxy), e.g. `rtk dotnet build`.

## Claude Code tooling (`.claude/`, `.mcp.json`)

- Skills: `/aot-check` (AOT publish + IL warnings vs `il-baseline.txt` + startup probe). Agent `vigo-conventions-reviewer`: reviews the diff against the conventions below.
- Hook `guard.ps1` (PreToolUse): polices `git commit` (English message, no co-author or session, signing kept: see
  "Commits"), blocks branch creation, editing generated files, `{Binding}` in XAML and `Application.Current.Resources[...]` in C#. It scans any command whose text
  contains "git commit", even in a throwaway repo or a heredoc (so it must follow the same rules): to replay a
  commit in the exe, let VIGO create the commits; write docs with the Write/Edit tools. Same for branches (demo repo: clone a repo
  that has some, e.g. `git clone <repo>`, and let VIGO create the local branches). Only one git command per
  call: `git switch … && git -C … branch -D …` is wrongly blocked (the following `-C` is taken for `switch -C`).
- UI Automation replay: `SetForegroundWindow` (preceded by an Alt press) before `SendKeys`, otherwise the keys do not
  arrive; `ListView` rows are named by the `ToString()` of their data item.
- Some analyzers only apply in Release (CA1859, CA2261…): `rtk dotnet build VIGO.slnx -c Release
  -p:Platform=x64` before `/aot-check`. If `artifacts/publish/aot-x64/Vigo.exe` is open (the user is trying it),
  do not kill it: publish to another folder (`-o artifacts/publish/aot-x64-<name>`).
- UI Automation replay under Windows PowerShell 5.1: save the script as UTF-8 **with BOM** (otherwise « Résumé »
  is read as ANSI and no element is found). Do not name a function like an alias (`Select` → `Select-Object`
  wins) nor like a command it calls (`function Git` that runs `git`: recursion, PowerShell ignores
  case). Demo remote: a throwaway bare repo (`git clone --bare <repo> …` then a working clone), never the
  real repo; kill the exe at the end of the replay (a normal close overwrites the user's `session.json`).
- The hook also blocks a Bash heredoc whose text contains a branch creation command: write the docs
  with Write/Edit.
- Mouse replay (hover, drag and drop): `SetProcessDPIAware()` then `mouse_event` in absolute coordinates
  (`0x8001`, normalized on `GetSystemMetrics`); a `SetCursorPos` or a zero relative move produces no
  pointer event. During a drag, `DragEventArgs.OriginalSource` is the container (the list), not the row:
  find the row by position (`VisualTreeHelper.FindElementsInHostCoordinates`).
- Demo repo with a conflict: `git clone -b <branch> <repo> …`, modify a file, commit it in VIGO
  (« Tout indexer » (Stage all), summary, « Valider » (Commit) button), then merge a branch that modified the same file.
- XAML captures: `PrintWindow` (flag 2), even for a hidden window. Before any `SendKeys` / real click, check `GetForegroundWindow()` == VIGO
  (otherwise the keys go to another application); when Windows refuses the foreground, do not force it. The code editor (native since
  ADR-090) and the integrated terminal (native since ADR-076) are replayed with the real keyboard / mouse (`SendKeys`, `mouse_event`): never
  `Ctrl+Shift+S` (stash!) nor an AltGr character (backslash…) with `SendKeys`.
- Single instance (J9.5): a second launch passes its command line to the first instance and exits. To run a test instance next
  to the user's one, set `VIGO_ALLOW_MULTIPLE=1` for that process (the `--startup-probe` probe is exempt on its own).
- Project MCP: `mslearn` (Microsoft docs: WinUI, Windows App SDK, .NET) and `context7` (third-party libraries).

## Stack

.NET 11 (RC) / C# 15, WinUI 3 (Windows App SDK 2.x), CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting/DI,
Serilog, LibGit2Sharp + git.exe, Win2D, LoomEditor (native code editor), xUnit v3 with its native `Assert` assertions (Microsoft.Testing.Platform; **no FluentAssertions**), Native AOT.

## Architecture

```
src/Vigo.App           WinUI 3: shell (MainWindow, tabs in the title bar), views, Win2D controls, themes
  Themes/Tokens.xaml   GENERATED (tools/gen-tokens.mjs): light+dark brushes/colors of the design system
  Themes/DesignTokens.g.cs  GENERATED: same colors in C# for Win2D
  Themes/Styles.xaml   VIGO styles (typography, buttons, cards, fields)
  Controls/FluentIcons.g.cs GENERATED (tools/gen-icons.mjs): Fluent System Icons glyphs used by the mockups
  Controls/CodeEditor  native LoomEditor code editor (ADR-090, replaces Monaco / ADR-038): blame, unified diff with staging, side by side,
                       conflicts, PR review (`CodeEditor.Native*.cs`, `DiffHunkHeader`, `ConflictPaneHeader`, `ConflictZone`, `Review*`)
  Controls/ConflictView conflict editor (J5.4, ADR-040): XAML header + CodeEditor in `conflict` mode
  Controls/RebaseView  interactive rebase (J5.5, ADR-041): reorderable list + Result card, in place of the graph
  Services/CredentialLockerVault host tokens in the Credential Locker (J6.1, ADR-044); accounts: `HostAccounts` (Core)
  Controls/ImageDiffView image diff (J5.7, ADR-043): side by side / overlay / slider, in place of the CodeEditor
  Controls/FileHistoryView, BlameView  file history and blame (J5.6, ADR-042); blame column drawn
                       in Win2D by the CodeEditor
  Controls/PullRequestView, MarkdownView  read-only GitHub pull request screen, in place of the graph (J6.9a, ADR-056);
                       `MarkdownView` draws the `Vigo.Core.Markdown` model (Markdig) in native XAML
  Controls/TerminalView, Views/TerminalPanel  integrated terminal (J7.10, ADR-071, ADR-076): native LoomTerminal.WinUI control (Win2D, no WebView2),
                       one shell per tab of the bottom panel, behind ConPTY (packages LoomTerminal.Pty / .Presentation / .Core)
  Services/KeyboardShortcuts  app shortcuts (XAML accelerators + table relayed from the terminal)
src/Vigo.Core          ViewModels, models, IGitService, GitResult unions — no UI dependency
                       (including `WorktreeActionsViewModel`, J7.1, ADR-049: linked worktrees, left panel section;
                       `SubmoduleActionsViewModel`, J7.2, ADR-050: submodules, left panel section;
                       `PullRequestViewModel`, J6.9a, ADR-056: pull request screen; `Markdown/`: Markdig → block model)
src/Vigo.Git           GitService (LibGit2Sharp reads, git.exe network/writes/fallback), GitCli,
                       Log/CommitLogReader (git log streamed via PipeReader), Refs/RefReader (libgit2 + for-each-ref fallback),
                       Conflicts/ConflictReader (ls-files -u stages, merge-file --zdiff3 output),
                       History/FileHistoryReader (streamed log --follow), History/BlameReader (blame --incremental),
                       Terminal/WindowsShellLocator (integrated terminal shells, J7.10, ADR-071),
                       Status/StatusReader (status porcelain v2), Status/OperationReader
                       (merge / rebase in progress, read from the git directory), Watching/RepositoryWatcher (FileSystemWatcher + debounce,
                       separate "status" and "refs" signals)
src/Vigo.Graph         lane computation (pure, testable, no Git dependency) — J2.3
src/Vigo.Integrations  hosts (GitHub: `GitHubClient`, OAuth device flow + token, REST PRs / issues with ETag, PR creation, J6.2; GitLab, Azure DevOps, Bitbucket to come) — J6
tests/Vigo.TestUtilities  TestRepoBuilder: real Git repos generated via git.exe, isolated config
tests/Vigo.*.Tests     xUnit v3 (native Assert), including Vigo.Integrations.Tests (HTTP clients against a fake handler)
benchmarks/            BenchmarkDotNet (from J2.3)
tools/                 generators (tokens, icons) — Node.js
```

Dependencies: App → Core, Git, Graph, Integrations; Git → Core; Core → Graph; Integrations → Core.

## Commands

```powershell
rtk dotnet build VIGO.slnx -p:Platform=x64                       # full build
# tests (MTP: --project required). `rtk dotnet test` adds `--report-trx` itself: the test projects therefore reference
# Microsoft.Testing.Extensions.TrxReport (without it: MTP handshake fails, code 5). Both forms work: `rtk dotnet test --project …` and `rtk test dotnet test --project …`
# Do NOT set `DOTNET_TEST_RUNNER=VSTest`: rtk adds `--report-trx`, which MSBuild rejects (MSB1001), `--logger trx` changes nothing. The .trx files go to `TestResults/`
rtk test dotnet test --project tests/Vigo.Core.Tests
rtk test dotnet test --project tests/Vigo.Git.Tests
rtk dotnet build src/Vigo.App -c Debug -p:Platform=x64            # app only
# Native AOT publish (prototype validated in J1.2)
rtk dotnet publish src/Vigo.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/publish/aot-x64
# Startup measurement (writes the time to first frame, in ms, then exits)
artifacts/publish/aot-x64/Vigo.exe --startup-probe $env:TEMP\probe.txt
# Open a repo at launch
Vigo.exe --open D:\path\to\repo
# Regenerate tokens and icons after a design change
node tools/gen-tokens.mjs docs/design/VIGO.dc.html.txt
node tools/gen-icons.mjs tools/FluentSystemIcons-Regular.json docs/design/*.txt
# App icon (Vigo.ico, design logo) and site assets: cd tools; npm i --no-save --no-package-lock sharp; node gen-app-icon.mjs <site-assets-dir>
```

Logs: `%LOCALAPPDATA%\VIGO\logs\vigo-YYYYMMDD.log`.

## Conventions

- **Branches**: Claude never creates a branch (neither `checkout -b` nor `switch -c`): it works on the current
  branch, the user manages branches.
- **Multilingual**: the app is multilingual, **FR + EN** to start with. Any text shown to the user
  must be localizable (no new hard-coded UI string without flagging it for the `.resw`); the chosen
  mechanism must be AOT-compatible. **Mechanism (ADR-075)**: `src/Vigo.App/Strings/{fr-FR,en-US}/Resources.resw`; XAML:
  `x:Uid` (without a dot, e.g. `PrefAutoFetchTitle` → `PrefAutoFetchTitle.Text`); non-UI code: `Loc.Get("Zone.Cle")` /
  `Loc.Format("Zone.Cle", args)` (Vigo.Core). Same key in both `.resw` (test `LocalizationResourcesTests`).
  Changing the language = restart; `VIGO_LANGUAGE=en|fr` (Debug) for replays.
- **Changelog** (J7.23): every new feature (or visible fix) adds a user-oriented line in
  `src/Vigo.Core/Changelog/CHANGELOG-fr.md` **and** `CHANGELOG-en.md`, in the next version's section, at the top of the
  file (`## x.y.z — YYYY-MM-DD`; while the version is not set: `## Unreleased`, to rename before the tag). The
  section whose title is exactly the released version is shown on the first launch of that version (« Nouveautés »
  (What's new) dialog); without a section, nothing is shown. Hyphenated names: `.fr.md` would be taken for a culture (satellite assembly).
- **Language**: **all source code is in English** (identifiers, comments, XML docs, log messages,
  internal exception messages, `tools/` scripts, tests), as are **commit messages** and this file (CLAUDE.md). Stay in French:
  the documentation in `docs/` (PROGRESS, DECISIONS, AUDIT…) and user-facing texts (UI, `Failure.Message`
  shown in a toast), the latter localized through `.resw` (FR/EN) — see "Multilingual".
- **Shell**: every command goes through `rtk`, no exception (`rtk test …`, `rtk proxy …` if the filter gets in the way).
- **Commits**: since 2026-10-16, **Claude makes the commits** (git.exe, GPG-signed with the user's config), on
  request. Message in **English**, short imperative title, **never** a
  `Co-Authored-By` line, a mention of Claude / Anthropic, or a session link or id. Write the message with the Write tool in
  `artifacts/` (ignored by git) then `git commit -F artifacts/<file>`. The `guard.ps1` hook checks this and also rejects
  `--no-verify`, `--no-gpg-sign`, `commit.gpgsign=false` and `--author`.
- **Async**: every Git operation is `async` + `CancellationToken`; the UI thread never does I/O
  (LibGit2Sharp is called via `Task.Run`). Runtime Async enabled (`Features=runtime-async=on`).
- **Results**: Git operations return `GitResult<T>` / `GitResult` (C# 15 `union` types):
  `Success<T>|Done`, `Conflict`, `AuthenticationFailed`, `Canceled`, `Failure`. Always an exhaustive `switch`.
  A union is a `struct` exposing `object Value`: in tests, assert on `result.Value` (`Assert.IsType<Success<T>>(result.Value)`).
- **Destructive actions** (discard, delete…): always confirmed through `IConfirmationService` (default button
  « Annuler » (Cancel)); path lists passed to git.exe go on stdin (`--pathspec-from-file=- --pathspec-file-nul`).
- **Logs**: source-generated `[LoggerMessage]` (no direct `LogInformation` calls, CA1848).
- **AOT (mandatory rules)**:
  - XAML: `x:Bind` only (no `{Binding}`), DataTemplates typed with `x:DataType`.
  - NEVER cast in C# a resource read from `Application.Current.Resources` (Brush, Style, FontFamily, Color):
    `InvalidCastException` under AOT. Use `{ThemeResource}` in XAML, or `DesignTokens` (C#) for Win2D.
  - No reflection-based configuration (Serilog configured in code, host `CreateEmptyApplicationBuilder`).
  - `ItemsSource` / `ItemsRepeater`: always an `ObservableCollection<T>` (array or `IReadOnlyList<T>` of a custom
    type: « not a supported vector » under AOT).
  - Visual tree: an element created by native XAML (`VisualTreeHelper.GetParent`…) can come back as a base type
    (`ListViewItemPresenter` → `FrameworkElement`): an `is ListViewItemPresenter` test fails silently under AOT.
    Walk up to a safe type (`ListViewItem`) without testing the intermediates. Same trap for
    `AppWindow.Presenter is OverlappedPresenter` (comes back as `AppWindowPresenter`): create the presenter
    (`OverlappedPresenter.Create()` + `SetPresenter`) and keep its reference.
  - LibGit2Sharp: no API with a native callback (`Patch`, progress…): fail-fast under AOT (ADR-019). git.exe otherwise.
  - Check before every release: `dotnet publish -c Release` with no new IL warning, then run the published exe
    **and replay the affected flows in it** (code that works in Debug/JIT can crash under AOT).
- **Live theme and density** (ADR-032): density-dependent sizes bound to
  `services:Appearance.Current.*` (static `x:Bind`, `Mode=OneWay`), never as `StaticResource`; theme brushes
  as `{ThemeResource}`, and every brush `<StaticResource>` alias inside `ThemeDictionaries` (Default + Light);
  `RequestedTheme = Appearance.Current.Theme` on every `ContentDialog`.
- **Multiple windows** (ADR-033): nothing window-specific as a DI singleton (dialogs, toasts, picker,
  shell: created by `WindowManager`); a tab only uses the services received through its `TabServices` relay, never
  those of a window captured directly (otherwise its toasts and dialogs stay in the old window after a
  move). No `TabView.CanTearOutTabs` (the tabs become title bar: hijacked clicks, window that
  tears out the selected tab); no `SetWindowSubclass` (comctl32 5 without a Common Controls 6 manifest).
- **Win2D**: views are cached per tab; release the resources (`Release()`) when the tab
  closes, not on `Unloaded`.
- **Tests**: real repos via `TestRepoBuilder` (deterministic commit dates), no LibGit2Sharp mocks.
  Remotes: local bare repo (`TestRepoBuilder.Create(bare: true)` + `Clone`); authentication: `UnauthorizedGitServer`.
  Any test that triggers credentials **first clears the helper list locally** (empty `credential.helper`,
  then `store --file=` in the test repo): `GitService` reads the global config, and otherwise GCM would write to the
  user's Windows Credential Manager.
- **Text size and font** (preferences, applied at launch): in XAML, every `FontSize` / `LineHeight` (except `FontIcon`) is
  `{ThemeResource VigoFontSizeN}` / `{ThemeResource VigoLineHeightN}`, never a literal; the keys (and `VigoFont`, `VigoDisplayFont`) are declared
  **at the root level of `App.xaml`** (not in `Styles.xaml`: a style there would find its own copy before the value written by `UiText.Apply`) and
  listed in `Services/UiText.cs`. Text built in code (Win2D, `new TextBlock`): `Appearance.Scaled(n)` and `Appearance.Current.UiFontFamily`.
  An implicit `TextBlock` `Setter` must not set `FontSize` (breaks inheritance from a `Button`).
- **Generated files** (`*.g.cs`, `Tokens.xaml`): do not edit by hand, change the generator.
- **Shortcuts**: declared with `KeyboardShortcuts.Add(owner, key, modifiers, action)`, never as XAML
  `<KeyboardAccelerator>`: the table holds the configurable shortcuts, and the terminal (which keeps the keys) sends back
  those its shell does not use to `KeyboardShortcuts.TryInvoke`, which only knows the table.
- **Code editor** (ADR-090): every source file display goes through `CodeEditor` (native LoomEditor; no new
  viewer); its texts come from the `.resw` (`EditorReviewLabels`, labels passed by the views); the unified diff keeps git's
  hunks (staging); a XAML element of a removed view zone is never put back into another one (fail-fast crash): one new element per zone.
- Submodule tests: git refuses the `file` transport for submodules (local config has no effect); call
  `TestRepoBuilder.AllowFileSubmodules()`. A submodule that is only staged cannot be deinitialized without `--force`: commit first.
- Bash: heredocs containing an apostrophe fail in the Bash tool; use the Write tool.
- XAML: an `x:Name` with the same name as a property bound by `x:Bind` in a `DataTemplate` of the same view makes the compiler fail
  with « Xaml Internal Error WMC9999 » and no useful message (ADR-056): name the elements differently. To find the cause of such a failure,
  remove blocks from the view until it compiles.
