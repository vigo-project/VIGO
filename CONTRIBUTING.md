# Contributing to VIGO

Thanks for helping. VIGO is a native WinUI 3 Git client that ships as a Native AOT application, so a few rules differ
from a typical .NET project. Please read them before opening a pull request.

## Getting started

1. Install the .NET SDK pinned in `global.json`, Visual Studio with the .NET desktop and C++ workloads, and Git for Windows.
2. Build and test:

   ```powershell
   dotnet build VIGO.slnx -p:Platform=x64
   dotnet test --project tests/Vigo.Core.Tests
   dotnet test --project tests/Vigo.Git.Tests
   ```

   Some analyzers only run in Release (`dotnet build VIGO.slnx -c Release -p:Platform=x64`).
3. Before a release-impacting change, publish with Native AOT and run the paths you touched in the published exe:
   code that works in Debug can crash under AOT.

   ```powershell
   dotnet publish src/Vigo.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/publish/aot-x64
   ```

   The `aot-check` skill (`.claude/skills/aot-check`) compares IL warnings with `il-baseline.txt`: no new warning allowed.

Logs are written to `%LOCALAPPDATA%\VIGO\logs`.

## Pull requests

- Keep a pull request focused on one change; open an issue first for large features.
- Commit titles: short, imperative, in English (`Fix rename of a branch with spaces`).
- Add a user-oriented line to **both** `src/Vigo.Core/Changelog/CHANGELOG-en.md` and `CHANGELOG-fr.md`, in the top
  section (`## Unreleased`, renamed with the version before tagging).
- Make sure build and tests pass. Do not edit generated files (see below).

## Code conventions

- **Language**: all code, comments, XML docs, log messages and commit messages are in English. User-facing text is
  localized, never hard-coded.
- **Layers**: `App` depends on `Core`, `Git`, `Graph`, `Integrations`; `Git` and `Integrations` on `Core`; `Core` on
  `Graph`. `Core` and `Graph` have no UI dependency.
- **Async**: every Git operation is `async` with a `CancellationToken`. The UI thread never does I/O (LibGit2Sharp
  calls go through `Task.Run`).
- **Results**: Git operations return `GitResult<T>` / `GitResult` (C# unions); always handle them with an exhaustive
  `switch`. In tests, assert on `result.Value`.
- **Destructive actions** (discard, delete, force push...) are confirmed through `IConfirmationService`, with the
  default button on "Cancel". Path lists passed to git.exe go through stdin (`--pathspec-from-file=-`).
- **Logging**: source-generated `[LoggerMessage]`, no direct `LogInformation` calls.
- **LibGit2Sharp**: do not use APIs that take native callbacks (`Patch`, progress handlers); they fail under AOT. Use
  git.exe instead.

### Native AOT rules

- XAML: `x:Bind` only (no `{Binding}`), typed `DataTemplate`s (`x:DataType`).
- Never cast a resource read from `Application.Current.Resources` in C# (Brush, Style, FontFamily, Color): it throws
  under AOT. Use `{ThemeResource}` in XAML, or `DesignTokens` in C# / Win2D code.
- No reflection-based configuration (Serilog is configured in code).
- `ItemsSource` / `ItemsRepeater`: always an `ObservableCollection<T>`.
- Do not test visual-tree elements against intermediate types (`is ListViewItemPresenter`): walk up to a safe type
  (`ListViewItem`).

### UI and theming

- Sizes that depend on density are bound to `services:Appearance.Current.*`; theme brushes use `{ThemeResource}`.
- Font sizes in XAML are `{ThemeResource VigoFontSizeN}` / `{ThemeResource VigoLineHeightN}`, never literals.
- Shortcuts are declared with `KeyboardShortcuts.Add(...)`, never as XAML `KeyboardAccelerator`.
- Every source file view goes through the `CodeEditor` control (native LoomEditor, ADR-090).
- Anything specific to one window is created by `WindowManager`, never a DI singleton.

### Localization

The app is multilingual (French and English). Strings live in `src/Vigo.App/Strings/{fr-FR,en-US}/Resources.resw`:

- XAML: `x:Uid` (e.g. `PrefAutoFetchTitle` gives `PrefAutoFetchTitle.Text`).
- Code without UI: `Loc.Get("Zone.Key")` / `Loc.Format("Zone.Key", args)` (`Vigo.Core`).
- The same key must exist in both `.resw` files (checked by `LocalizationResourcesTests`).

### Generated files

Do not edit by hand. The design sources of the first two generators are kept by the maintainers: open an issue to request a change to tokens or icons.

| Generated | Generator |
|---|---|
| `Themes/Tokens.xaml`, `Themes/DesignTokens.g.cs` | `node tools/gen-tokens.mjs docs/design/VIGO.dc.html.txt` |
| `Controls/FluentIcons.g.cs` | `node tools/gen-icons.mjs tools/FluentSystemIcons-Regular.json docs/design/*.txt` |

## Tests

xUnit v3 with its native `Assert` (no FluentAssertions). Tests use real Git repositories created with
`TestRepoBuilder` (deterministic commit dates), not mocks of LibGit2Sharp. Remote tests use a local bare repository;
any test that triggers credentials must first empty the local `credential.helper` list so it never touches the
developer's Windows Credential Manager.

## Third-party code

New dependencies must use a permissive license (MIT, BSD, Apache-2.0). Add them to
`THIRD-PARTY-NOTICES.md` in the same pull request.

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
