# VIGO — client Git natif Windows

Client Git WinUI 3 natif Windows, rapide et léger.
Le nom **VIGO** signifie **Visual Git Overview** (à reprendre dans l'app : écran « À propos », infobulle du logo,
description du paquet MSIX).

## Méthode de travail

- Avant tout choix technique structurant : présenter les options et attendre validation.
- Préfixer les commandes shell par `rtk` (proxy d'économie de tokens), ex. `rtk dotnet build`.

## Outillage Claude Code (`.claude/`, `.mcp.json`)

- Skills : `/aot-check` (publication AOT + avertissements IL vs `il-baseline.txt` + sonde de démarrage). Agent `vigo-conventions-reviewer` : revue du diff selon les conventions ci-dessous.
- Hook `guard.ps1` (PreToolUse) : encadre `git commit` (message anglais, sans co-auteur ni session, signature conservée : voir
  « Commits »), bloque la création de branche, l'édition des fichiers générés (dont
  `src/Vigo.App/Editor/`), `{Binding}` en XAML et `Application.Current.Resources[...]` en C#. Il analyse toute commande dont le texte
  contient « git commit », même dans un dépôt jetable ou un heredoc (donc elle doit respecter les mêmes règles) : pour rejouer un
  commit dans l'exe, laisser VIGO créer les commits ; documenter via les outils Write/Edit. Idem pour les branches (dépôt de démo : cloner un dépôt
  qui en a, p. ex. `git clone <repo>`, et laisser VIGO créer les branches locales). Une seule commande git par
  appel : `git switch … && git -C … branch -D …` est bloqué à tort (le `-C` suivant est pris pour `switch -C`).
- Rejeu UI Automation : `SetForegroundWindow` (précédé d'un appui Alt) avant `SendKeys`, sinon les touches n'arrivent
  pas ; les lignes d'une `ListView` sont nommées par le `ToString()` de leur donnée.
- Certains analyseurs ne s'appliquent qu'en Release (CA1859, CA2261…) : `rtk dotnet build VIGO.slnx -c Release
  -p:Platform=x64` avant `/aot-check`. Si `artifacts/publish/aot-x64/Vigo.exe` est ouvert (l'utilisateur l'essaie),
  ne pas le tuer : publier dans un autre dossier (`-o artifacts/publish/aot-x64-<nom>`).
- Rejeu UI Automation sous Windows PowerShell 5.1 : enregistrer le script en UTF-8 **avec BOM** (sinon « Résumé »
  est lu en ANSI et aucun élément n'est trouvé). Ne pas nommer une fonction comme un alias (`Select` → `Select-Object`
  l'emporte) ni comme une commande qu'elle appelle (`function Git` qui lance `git` : récursion, PowerShell ignore la
  casse). Distant de démo : un dépôt nu jetable (`git clone --bare <repo> …` puis clone de travail), jamais le
  dépôt réel ; tuer l'exe en fin de rejeu (une fermeture normale écrase `session.json` de l'utilisateur).
- Le hook bloque aussi un heredoc Bash dont le texte contient une commande de création de branche : écrire la doc
  avec Write/Edit.
- Rejeu souris (survol, glisser-déposer) : `SetProcessDPIAware()` puis `mouse_event` en coordonnées absolues
  (`0x8001`, normalisées sur `GetSystemMetrics`) ; un `SetCursorPos` ou un déplacement relatif nul ne produit aucun
  événement pointeur. Pendant un glisser, `DragEventArgs.OriginalSource` est le conteneur (la liste), pas la ligne :
  retrouver la ligne par position (`VisualTreeHelper.FindElementsInHostCoordinates`).
- Dépôt de démo avec conflit : `git clone -b <branche> <repo> …`, modifier un fichier, faire le commit dans VIGO
  (« Tout indexer », résumé, bouton Valider), puis fusionner une branche qui a modifié le même fichier.
- Rejeu de l'éditeur (WebView2) sans toucher au clavier ni à la souris de l'utilisateur : lancer l'exe de test avec
  `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` (variable posée pour ce processus seulement),
  puis protocole DevTools (`http://127.0.0.1:9333/json`, WebSocket natif de Node) : `Input.dispatchMouseEvent` /
  `dispatchKeyEvent` (avec `windowsVirtualKeyCode`), `Runtime.evaluate`. Captures XAML : `PrintWindow` (flag 2), même
  fenêtre masquée. Recherches UIA : sauter les éléments `FrameworkId == "Chrome"` (arbre du DOM énorme, `FindFirst`
  expire). Avant tout `SendKeys` / clic réel, vérifier `GetForegroundWindow()` == VIGO (sinon les touches partent dans
  une autre application) ; quand Windows refuse le premier plan, ne pas forcer. Si le VIGO de l’utilisateur tourne, donner à l’instance de test
  son propre profil WebView2 (`VIGO_WEBVIEW2_USER_DATA=<dossier>`, sinon les arguments du navigateur diffèrent et le démarrage échoue) ; en Debug la page du
  terminal intégré (natif depuis ADR-076, plus de WebView2) se rejoue au clavier / à la souris réels (`SendKeys`, `mouse_event`) : jamais `Ctrl+Shift+S` (stash !) ni de caractère AltGr (antislash…) avec `SendKeys`.
- Instance unique (J9.5) : un second lancement passe sa ligne de commande à la première instance et sort. Pour lancer une instance de test à côté
  de celle de l'utilisateur, poser `VIGO_ALLOW_MULTIPLE=1` pour ce processus (la sonde `--startup-probe` y échappe seule).
- MCP projet : `mslearn` (doc Microsoft : WinUI, Windows App SDK, .NET) et `context7` (bibliothèques tierces).

## Stack

.NET 11 (RC) / C# 15, WinUI 3 (Windows App SDK 2.x), CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting/DI,
Serilog, LibGit2Sharp + git.exe, Win2D, Monaco (WebView2), xUnit v3 avec ses assertions natives `Assert` (Microsoft.Testing.Platform ; **pas de FluentAssertions**), Native AOT.

## Architecture

```
src/Vigo.App           WinUI 3 : shell (MainWindow, onglets en barre de titre), vues, contrôles Win2D, thèmes
  Themes/Tokens.xaml   GÉNÉRÉ (tools/gen-tokens.mjs) : brushes/couleurs clair+sombre du design system
  Themes/DesignTokens.g.cs  GÉNÉRÉ : mêmes couleurs en C# pour Win2D
  Themes/Styles.xaml   styles VIGO (typo, boutons, cartes, champs)
  Controls/FluentIcons.g.cs GÉNÉRÉ (tools/gen-icons.mjs) : glyphes Fluent System Icons utilisés par les maquettes
  Controls/CodeEditor  éditeur de code (Monaco dans WebView2, ADR-038) : diff unifié / côte à côte, texte, conflits ; messages JSON
  Controls/ConflictView éditeur de conflits (J5.4, ADR-040) : en-tête XAML + CodeEditor en mode `conflict`
  Controls/RebaseView  rebase interactif (J5.5, ADR-041) : liste réordonnable + carte Résultat, à la place du graphe
  Services/CredentialLockerVault jetons des hébergeurs dans le Credential Locker (J6.1, ADR-044) ; comptes : `HostAccounts` (Core)
  Controls/ImageDiffView diff d'images (J5.7, ADR-043) : côte à côte / superposition / curseur, à la place du CodeEditor
  Controls/FileHistoryView, BlameView  historique de fichier et blame (J5.6, ADR-042) ; colonne de blame dessinée
                       par la page (CodeEditor mode `blame`)
  Controls/PullRequestView, MarkdownView  écran d'une pull request GitHub en lecture seule, à la place du graphe (J6.9a, ADR-056) ;
                       `MarkdownView` dessine le modèle `Vigo.Core.Markdown` (Markdig) en XAML natif
  Controls/TerminalView, Views/TerminalPanel  terminal intégré (J7.10, ADR-071, ADR-076) : contrôle natif LoomTerminal.WinUI (Win2D, sans WebView2),
                       un shell par onglet du panneau bas, derrière ConPTY (paquets LoomTerminal.Pty / .Presentation / .Core)
  Editor/              GÉNÉRÉ (tools/editor/build.mjs) : page Monaco, servie sur https://vigo.editor/
  Services/KeyboardShortcuts  raccourcis de l'app (accélérateurs XAML + table relayée depuis l'éditeur)
src/Vigo.Core          ViewModels, modèles, IGitService, unions GitResult — aucune dépendance UI
                       (dont `WorktreeActionsViewModel`, J7.1, ADR-049 : worktrees liés, section du panneau gauche ;
                       `SubmoduleActionsViewModel`, J7.2, ADR-050 : submodules, section du panneau gauche ;
                       `PullRequestViewModel`, J6.9a, ADR-056 : écran d'une pull request ; `Markdown/` : Markdig → modèle de blocs)
src/Vigo.Git           GitService (LibGit2Sharp lecture, git.exe réseau/écriture/repli), GitCli,
                       Log/CommitLogReader (git log streamé via PipeReader), Refs/RefReader (libgit2 + repli for-each-ref),
                       Conflicts/ConflictReader (étages ls-files -u, sortie de merge-file --zdiff3),
                       History/FileHistoryReader (log --follow streamé), History/BlameReader (blame --incremental),
                       Terminal/WindowsShellLocator (shells du terminal intégré, J7.10, ADR-071),
                       Status/StatusReader (status porcelain v2), Status/OperationReader
                       (merge / rebase en cours, lus dans le répertoire git), Watching/RepositoryWatcher (FileSystemWatcher + debounce,
                       signaux « status » et « refs » distincts)
src/Vigo.Graph         calcul des lanes (pur, testable, sans dépendance Git) — J2.3
src/Vigo.Integrations  hébergeurs (GitHub : `GitHubClient`, device flow OAuth + jeton, PR / issues REST avec ETag, création de PR, J6.2 ; GitLab, Azure DevOps, Bitbucket à venir) — J6
tests/Vigo.TestUtilities  TestRepoBuilder : dépôts Git réels générés via git.exe, config isolée
tests/Vigo.*.Tests     xUnit v3 (Assert natif), dont Vigo.Integrations.Tests (clients HTTP contre un gestionnaire simulé)
benchmarks/            BenchmarkDotNet (à partir de J2.3)
tools/                 générateurs (tokens, icônes) — Node.js ; tools/editor : page de l'éditeur (monaco-editor, esbuild)
```

Dépendances : App → Core, Git, Graph, Integrations ; Git → Core ; Core → Graph ; Integrations → Core.

## Commandes

```powershell
rtk dotnet build VIGO.slnx -p:Platform=x64                       # build complet
# tests (MTP : --project obligatoire). `rtk dotnet test` ajoute lui-même `--report-trx` : les projets de test référencent donc
# Microsoft.Testing.Extensions.TrxReport (sans lui : handshake MTP en échec, code 5). Les deux formes marchent : `rtk dotnet test --project …` et `rtk test dotnet test --project …`
# Ne PAS poser `DOTNET_TEST_RUNNER=VSTest` : rtk ajoute `--report-trx` que MSBuild refuse (MSB1001), `--logger trx` ne change rien. Les .trx vont dans `TestResults/`
rtk test dotnet test --project tests/Vigo.Core.Tests
rtk test dotnet test --project tests/Vigo.Git.Tests
rtk dotnet build src/Vigo.App -c Debug -p:Platform=x64            # app seule
# Publication Native AOT (prototype validé J1.2)
rtk dotnet publish src/Vigo.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/publish/aot-x64
# Mesure du démarrage (écrit le temps jusqu'à la première frame, en ms, puis quitte)
artifacts/publish/aot-x64/Vigo.exe --startup-probe $env:TEMP\probe.txt
# Ouvrir un dépôt au lancement
Vigo.exe --open D:\chemin\depot
# Régénérer tokens et icônes après modification du design
node tools/gen-tokens.mjs docs/design/VIGO.dc.html.txt
node tools/gen-icons.mjs tools/FluentSystemIcons-Regular.json docs/design/*.txt
# Icône de l'app (Vigo.ico, logo du design) et assets du site : cd tools; npm i --no-save --no-package-lock sharp; node gen-app-icon.mjs <site-assets-dir>
# Reconstruire la page de l'éditeur après modification de tools/editor/src (versions épinglées : package-lock.json)
cd tools/editor; npm ci; node build.mjs
```

Logs : `%LOCALAPPDATA%\VIGO\logs\vigo-AAAAMMJJ.log`.

## Conventions

- **Branches** : Claude ne crée jamais de branche (ni `checkout -b`, ni `switch -c`) : il travaille sur la branche
  courante, l'utilisateur gère les branches.
- **Multilingue** : l'application est multilingue, **FR + EN** pour commencer. Tout texte affiché à l'utilisateur
  doit être localisable (pas de nouvelle chaîne UI codée en dur sans la signaler pour le `.resw`) ; le mécanisme
  retenu doit être compatible AOT. **Mécanisme (ADR-075)** : `src/Vigo.App/Strings/{fr-FR,en-US}/Resources.resw` ; XAML :
  `x:Uid` (sans point, ex. `PrefAutoFetchTitle` → `PrefAutoFetchTitle.Text`) ; code sans UI : `Loc.Get("Zone.Cle")` /
  `Loc.Format("Zone.Cle", args)` (Vigo.Core). Même clé dans les deux `.resw` (test `LocalizationResourcesTests`).
  Changement de langue = redémarrage ; `VIGO_LANGUAGE=en|fr` (Debug) pour les rejeux.
- **Changelog** (J7.23) : toute nouvelle fonctionnalité (ou correctif visible) ajoute une ligne, orientée utilisateur, dans
  `src/Vigo.Core/Changelog/CHANGELOG-fr.md` **et** `CHANGELOG-en.md`, dans la section de la prochaine version, en tête de
  fichier (`## x.y.z — AAAA-MM-JJ` ; tant que la version n'est pas fixée : `## Unreleased`, à renommer avant le tag). La
  section dont le titre est exactement la version publiée s'affiche au premier lancement de cette version (dialogue
  « Nouveautés ») ; sans section, rien ne s'affiche. Noms en tiret : `.fr.md` serait pris pour une culture (assembly satellite).
- **Langue** : **tout le code source est en anglais** (identifiants, commentaires, docs XML, messages de log,
  messages d'exception internes, scripts `tools/`, tests) ainsi que les **messages de commit**. Restent en français :
  la documentation du dépôt (ce fichier) et les textes affichés à l'utilisateur (UI, `Failure.Message`
  présenté en toast), en attendant la localisation `.resw` FR/EN (J8) — voir « Multilingue ».
- **Shell** : toute commande passe par `rtk`, sans exception (`rtk test …`, `rtk proxy …` si le filtre gêne).
- **Commits** : depuis le 2026-10-16, **Claude fait les commits** (git.exe, signés GPG avec la config de l'utilisateur), sur
  demande. Message en **anglais**, titre court à l'impératif, **jamais** de ligne
  `Co-Authored-By`, de mention de Claude / Anthropic ni de lien ou id de session. Écrire le message avec l'outil Write dans
  `artifacts/` (ignoré par git) puis `git commit -F artifacts/<fichier>`. Le hook `guard.ps1` le vérifie et refuse aussi
  `--no-verify`, `--no-gpg-sign`, `commit.gpgsign=false` et `--author`.
- **Async** : toute opération Git est `async` + `CancellationToken` ; le thread UI ne fait jamais d'I/O
  (LibGit2Sharp est appelé via `Task.Run`). Runtime Async activé (`Features=runtime-async=on`).
- **Résultats** : les opérations Git renvoient `GitResult<T>` / `GitResult` (types `union` C# 15) :
  `Success<T>|Done`, `Conflict`, `AuthenticationFailed`, `Canceled`, `Failure`. Toujours un `switch` exhaustif.
  Une union est un `struct` exposant `object Value` : en test, asserter sur `result.Value` (`Assert.IsType<Success<T>>(result.Value)`).
- **Actions destructives** (rejet, suppression…) : toujours confirmées par `IConfirmationService` (bouton par défaut
  « Annuler ») ; les listes de chemins passées à git.exe vont sur stdin (`--pathspec-from-file=- --pathspec-file-nul`).
- **Logs** : `[LoggerMessage]` source-générés (pas d'appels `LogInformation` directs, CA1848).
- **AOT (règles impératives)** :
  - XAML : `x:Bind` uniquement (pas de `{Binding}`), DataTemplates typés `x:DataType`.
  - Ne JAMAIS caster en C# une ressource lue dans `Application.Current.Resources` (Brush, Style, FontFamily, Color) :
    `InvalidCastException` sous AOT. Utiliser `{ThemeResource}` en XAML, ou `DesignTokens` (C#) pour Win2D.
  - Pas de configuration réflexive (Serilog configuré en code, host `CreateEmptyApplicationBuilder`).
  - `ItemsSource` / `ItemsRepeater` : toujours une `ObservableCollection<T>` (tableau ou `IReadOnlyList<T>` d’un type
    maison : « not a supported vector » sous AOT).
  - Arbre visuel : un élément créé par XAML natif (`VisualTreeHelper.GetParent`…) peut revenir sous un type de base
    (`ListViewItemPresenter` → `FrameworkElement`) : un test `is ListViewItemPresenter` échoue en silence sous AOT.
    Remonter jusqu'à un type sûr (`ListViewItem`) sans tester les intermédiaires. Même piège pour
    `AppWindow.Presenter is OverlappedPresenter` (revient en `AppWindowPresenter`) : créer le presenter
    (`OverlappedPresenter.Create()` + `SetPresenter`) et garder sa référence.
  - LibGit2Sharp : aucune API à callback natif (`Patch`, progressions…) : fail-fast sous AOT (ADR-019). git.exe sinon.
  - Vérifier avant chaque version : `dotnet publish -c Release` sans nouvel avertissement IL, puis lancer l'exe publié
    **et y rejouer les parcours touchés** (un code qui marche en Debug/JIT peut planter en AOT).
- **Thème et densité à chaud** (ADR-032) : tailles qui dépendent de la densité liées à
  `services:Appearance.Current.*` (`x:Bind` statique, `Mode=OneWay`), jamais en `StaticResource` ; brosses de thème
  en `{ThemeResource}`, et tout alias `<StaticResource>` de brosse dans des `ThemeDictionaries` (Default + Light) ;
  `RequestedTheme = Appearance.Current.Theme` sur chaque `ContentDialog`.
- **Fenêtres multiples** (ADR-033) : rien de propre à une fenêtre en singleton DI (dialogues, toasts, sélecteur,
  shell : créés par `WindowManager`) ; un onglet n'utilise que les services reçus via son relais `TabServices`, jamais
  ceux d'une fenêtre capturés directement (sinon ses toasts et dialogues restent dans l'ancienne fenêtre après un
  déplacement). Pas de `TabView.CanTearOutTabs` (les onglets deviennent barre de titre : clics détournés, fenêtre qui
  arrache l'onglet sélectionné) ; pas de `SetWindowSubclass` (comctl32 5 sans manifeste Common Controls 6).
- **Win2D** : les vues sont mises en cache par onglet ; libérer les ressources (`Release()`) à la fermeture
  de l'onglet, pas sur `Unloaded`.
- **Tests** : dépôts réels via `TestRepoBuilder` (dates de commit déterministes), pas de mocks de LibGit2Sharp.
  Distants : dépôt nu local (`TestRepoBuilder.Create(bare: true)` + `Clone`) ; authentification : `UnauthorizedGitServer`.
  Tout test qui déclenche des identifiants **vide d'abord la liste des helpers en local** (`credential.helper` vide,
  puis `store --file=` dans le dépôt de test) : `GitService` lit la config globale, et sinon GCM écrirait dans le
  Gestionnaire d'identifiants Windows de l'utilisateur.
- **Taille et police du texte** (préférences, appliquées au lancement) : en XAML, tout `FontSize` / `LineHeight` (hors `FontIcon`) est
  `{ThemeResource VigoFontSizeN}` / `{ThemeResource VigoLineHeightN}`, jamais un littéral ; les clés (et `VigoFont`, `VigoDisplayFont`) sont déclarées
  **au niveau racine de `App.xaml`** (pas dans `Styles.xaml` : un style y trouverait sa propre copie avant la valeur écrite par `UiText.Apply`) et
  listées dans `Services/UiText.cs`. Texte construit en code (Win2D, `new TextBlock`) : `Appearance.Scaled(n)` et `Appearance.Current.UiFontFamily`.
  Une `Setter` de `TextBlock` implicite ne doit pas fixer `FontSize` (casse l'héritage depuis un `Button`).
- **Fichiers générés** (`*.g.cs`, `Tokens.xaml`, `src/Vigo.App/Editor/`) : ne pas éditer à la main, modifier le générateur.
- **Raccourcis** (ADR-038) : déclarés par `KeyboardShortcuts.Add(owner, touche, modificateurs, action)`, jamais en
  `<KeyboardAccelerator>` XAML : WebView2 garde les touches, l'éditeur les renvoie et `KeyboardShortcuts.TryInvoke`
  ne connaît que la table. Côté page, toujours `keyCode` (disposition active), jamais `code` (position QWERTY).
- **Éditeur de code** : tout affichage de fichier source passe par `CodeEditor` (pas de nouvel afficheur Win2D) ;
  textes affichés dans la page fournis par le C# (localisation) ; le diff unifié garde les hunks de git (indexation).
- Tests de submodules : git refuse le transport `file` pour les submodules (config locale sans effet) ; appeler
  `TestRepoBuilder.AllowFileSubmodules()`. Un submodule seulement indexé ne se désinitialise pas sans `--force` : valider avant.
- Bash : les heredocs contenant une apostrophe échouent dans l'outil Bash ; utiliser l'outil Write.
- XAML : un `x:Name` qui porte le même nom qu'une propriété liée par `x:Bind` dans un `DataTemplate` de la même vue fait échouer le compilateur
  avec « Xaml Internal Error WMC9999 » sans message utile (ADR-056) : nommer les éléments autrement. Pour trouver la cause d'un tel échec,
  retirer des blocs de la vue jusqu'à ce qu'elle compile.
