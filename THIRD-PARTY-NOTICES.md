# Third-party notices

VIGO itself is released under the [MIT License](LICENSE). It redistributes or links the components below, each under
its own license. Full texts that are not reproduced here ship in the NuGet / npm packages, and in [`third-party/`](third-party/).

## Linked libraries (NuGet)

| Component | License |
|---|---|
| .NET runtime and libraries (self-contained / Native AOT) | MIT |
| Windows App SDK (`Microsoft.WindowsAppSDK`) | Microsoft Software License Terms (redistributable runtime, not open source) |
| Win2D (`Microsoft.Graphics.Win2D`) | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Extensions.Hosting, DependencyInjection and related | MIT |
| Markdig | BSD-2-Clause, Copyright (c) Alexandre Mutel |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0, Copyright (c) Serilog Contributors |
| Velopack | MIT |
| LibGit2Sharp | MIT, Copyright (c) LibGit2Sharp contributors |
| libgit2 (native, via `LibGit2Sharp.NativeBinaries`) | GPL-2.0 **with linking exception**: see [third-party/libgit2.license.txt](third-party/libgit2.license.txt) |

libgit2 is used unmodified as a separately shipped native library; its linking exception allows distributing it with
programs under other licenses. Its bundled dependencies (zlib, PCRE, ntlmclient, http-parser, xdiff, …) are covered by
the same file.

Build and test only (not redistributed): xunit.v3 (Apache-2.0), Microsoft.Testing.Platform extensions (MIT),
BenchmarkDotNet (MIT), Microsoft.Windows.SDK.BuildTools (Microsoft license).

## Bundled web components (`src/Vigo.App/Editor/`, built by `tools/editor`)

| Component | License |
|---|---|
| Monaco Editor 0.56.0 | MIT, Copyright (c) Microsoft Corporation: see `src/Vigo.App/Editor/LICENSE-monaco.txt`; third-party parts: [third-party/monaco-ThirdPartyNotices.txt](third-party/monaco-ThirdPartyNotices.txt) |
| Codicons font (`codicon.ttf`, shipped with Monaco) | CC-BY-4.0, Copyright (c) Microsoft Corporation (icons); code MIT |
| xterm.js 6.0.0 and addons (fit, unicode11, web-links, webgl) | MIT, Copyright (c) The xterm.js authors |
| markdown-it 15.0.2, linkify-it, mdurl, uc.micro, punycode.js | MIT |
| entities | BSD-2-Clause |
| DOMPurify 3.4.17 | used under Apache-2.0 (dual-licensed MPL-2.0 OR Apache-2.0), Copyright (c) Cure53 and contributors |
| marked (inside Monaco) | MIT |

Build tool only: esbuild (MIT).

## Icons and fonts

| Component | License |
|---|---|
| Fluent System Icons (`Assets/Fonts/FluentSystemIcons-Regular.ttf`) | MIT, Copyright (c) Microsoft Corporation |
| Segoe UI Variable, Cascadia Mono, Consolas | Windows system fonts, referenced but not redistributed |

## Attribution

Codicons are licensed under the Creative Commons Attribution 4.0 International License
(<https://creativecommons.org/licenses/by/4.0/>), Copyright (c) Microsoft Corporation, from
<https://github.com/microsoft/vscode-codicons>.
