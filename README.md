# VIGO

**VIGO** stands for **Visual Git Overview**: a fast, lightweight, native Git client for Windows, built with WinUI 3 and
Native AOT.

[![CI](https://github.com/vigo-project/vigo/actions/workflows/ci.yml/badge.svg)](https://github.com/vigo-project/vigo/actions)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Features

- Commit graph drawn with Win2D, with lanes, stashes, tags and remote branches
- Staging by file, hunk or line; diff (unified or side by side), image diff, file history and blame
- Code editor and diff view based on Monaco
- Merge, rebase (interactive), cherry-pick, revert, reset, conflict resolution with a three-way editor
- Worktrees, submodules, Git LFS, Git Flow, stashes, tags, remotes
- GitHub and Azure DevOps accounts: clone from your repositories, pull requests, issues, CI checks
- SSH keys and several Git profiles (identity, signing)
- Integrated terminal, command palette, configurable shortcuts
- Optional AI-assisted commit messages (off by default)
- English and French interface, light and dark themes

## Install

Download the latest installer from the [Releases](https://github.com/vigo-project/vigo/releases) page. VIGO updates
itself from the same releases.

Requirements: Windows 10 (build 19041) or later, and [Git for Windows](https://gitforwindows.org/) installed.

## Build from source

Prerequisites: Windows, the .NET SDK pinned in [`global.json`](global.json), and the Windows App SDK build tooling
(Visual Studio with the .NET desktop and C++ workloads is needed for Native AOT publishing). Node.js is only needed to
rebuild the editor bundle.

```powershell
dotnet build VIGO.slnx -p:Platform=x64
dotnet test --project tests/Vigo.Core.Tests
dotnet test --project tests/Vigo.Git.Tests
dotnet publish src/Vigo.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/publish/aot-x64
```

## Repository layout

| Folder | Content |
|---|---|
| `src/Vigo.App` | WinUI 3 shell, views, Win2D controls, themes |
| `src/Vigo.Core` | view models, models, services contracts (no UI dependency) |
| `src/Vigo.Git` | Git service (LibGit2Sharp and git.exe), log, status, watcher, terminal |
| `src/Vigo.Graph` | lane computation for the commit graph |
| `src/Vigo.Integrations` | hosting providers (GitHub, Azure DevOps), AI backends |
| `tests/` | xUnit v3 tests against real Git repositories |
| `tools/` | generators (tokens, icons) and the editor bundle build |


## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code of conduct](CODE_OF_CONDUCT.md). Report security issues as described in [SECURITY.md](SECURITY.md).

## Privacy

VIGO has no telemetry. It only contacts the services you configure (your Git remotes, GitHub or Azure DevOps when you
sign in), the release feed for updates, and the Anthropic API if you enable AI commit messages and add a key.
Tokens are stored in the Windows Credential Locker.

## Code signing policy

Release binaries are intended to be signed through the [SignPath Foundation](https://signpath.org/) once the project is
accepted: *Free code signing provided by SignPath.io, certificate by SignPath Foundation.* Only binaries built from this
repository by the GitHub Actions release workflow are signed. Until then, Windows SmartScreen may warn on first launch.

## License

[MIT](LICENSE). Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
