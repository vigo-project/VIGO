## Unreleased
- Integrated terminal: new native engine (no WebView2), lighter; same features (shells, theme, font, copy and paste, links, shortcuts).

## 0.27.1 — 2026-10-23
- Preferences › About: new "Receive pre-release versions" option to also get pre-releases as updates (off by default: stable releases only).
- Releases are now built and published automatically from the public repository github.com/vigo-project/vigo; no functional change.

## 0.27.0 — 2026-10-22
- VIGO is now open source (MIT license): the code and releases are published at github.com/vigo-project/vigo, and automatic updates come from that repository starting with this version.
- GitHub: browser sign-in uses a new OAuth application; if authorization is refused, reconnect the account in Preferences › Integrations.
- Azure DevOps: browser sign-in is no longer offered by default; use a personal access token.

## 0.26.1 — 2026-10-22
- Creating or renaming a branch, tag, remote, worktree and Git Flow names: a space typed or pasted in the name becomes "-" instead of showing an error.

## 0.26.0 — 2026-10-22
- Confirmations: the "Cancel" button is neutral (no longer blue) next to the action button.
- Diff: the unified / side-by-side layout (diff, file history, pull request) is now a two-icon toggle; the names are in the tooltips.
- Clone: the proposed destination folder decodes spaces (`%20`) from the URL instead of keeping them encoded.
- Session restore: a repository that is missing at one launch is reported; if it is still missing at the next launch, it is removed from the session without a warning.
- Working tree: "Discard the changes" now applies to every selected file, not only the one under the cursor.
- Updates: a downloaded update is installed the next time VIGO starts (with a progress window) instead of when it closes.
- Preferences › Current repository: edit the Git Flow branches and prefixes of the repository (or initialize Git Flow).
- Preferences › Current repository: Git hooks folder (core.hooksPath), enable or disable each hook, and a per-repository "Skip hooks" switch for the commits and pushes made by VIGO.
- Preferences › Current repository: default encoding of the repository's files and diffs (gui.encoding key of its configuration).
- GitHub and Azure DevOps accounts: fetch, pull, push and clone use the signed-in account's token (no Git Credential Manager window); if it is refused, git falls back to your usual credentials and a message says so.
- Preferences › Editor and tools: "Ligatures" option for the editor (Cascadia Code, Fira Code, JetBrains Mono…), off by default.
- GitHub pull requests: "Delete the branch" button on a merged or closed pull request (with an option to delete the local branch too).
- Graph: the ↑ / ↓ arrow keys (and Page Up, Page Down, Home, End) change the selected commit after clicking a commit.

## 0.25.0 — 2026-10-21
- Preferences › Appearance › Text formatting: choose the interface font and text size (apply at the next launch).
- Preferences › Editor and tools: editor font, size, tab size, end of line, syntax highlighting, line numbers and word wrap; integrated terminal font, size, line height, cursor style and dimming when unfocused.

## 0.24.0 — 2026-10-21
- Staging (or unstaging) the file shown in the preview opens the next file of the same list instead of going back to the graph.
- The Stash button uses the title and description typed for the commit; a Pop puts them back in the commit form.
- The automatic fetch (including the one at launch) shows the toolbar progress bar.

## 0.23.0 — 2026-10-20
- In the status bar, the age of the last fetch uses the same format as the rest of the app ("yesterday", "2 wk ago"…).
- The size of an Azure DevOps work item attachment is shown in the application language units.
- Private images of an issue or pull request no longer stay visible after switching profile or signing out.
- Large images (over 25 MB) of an Azure DevOps work item are no longer downloaded in full.
- Images in issues and pull requests are no longer loaded over plain http: a link to the image is shown instead.
- Applying many files of a stash at once no longer fails because of the command line length.
- A build writing into bin/, obj/ or node_modules/ no longer triggers a full git status every 2 seconds.
- Tabs and projects are saved a few seconds after each change, not only on a normal exit.
- Staging a line added after a last line without a final newline no longer glues the two lines together.
- In a linked worktree, branches, remotes and tags now refresh by themselves.
- Opening a very large file no longer loads it into memory before refusing it.
- Azure DevOps no longer renews the token once per work item image: one renewal is shared.
- A renewed GitHub token is no longer lost when the request is canceled, and a Credential Manager error is now reported.
- A repository that is unreachable at startup (network share, VPN) stays in the session instead of vanishing from it.
- Discarding an untracked nested git repository now shows an error instead of reporting success while deleting nothing.
- Blame works on paths with accents, and the git setting `diff.suppressBlankEmpty` no longer shifts diff line numbers.
- A version's release notes are no longer lost when another dialog opens at startup: they show at the next launch.
- Quitting VIGO while tabs are being reopened no longer loses the ones not yet reopened.
- Files that are not UTF-8 can no longer be staged or discarded line by line (their encoding was being corrupted).
- A commit's diff shows the right file when its name contains [, * or ?.
- A second launch of VIGO now opens its repository in the running window instead of starting another instance.
- An unreadable settings file is kept as .bad instead of being silently overwritten, and the session of other profiles is no longer erased.
- Closing a tab during a git write no longer interrupts it (no more stale index.lock).
- Force push no longer erases a colleague's commits that a background fetch brought in (--force-if-includes).
- Preference to load the graph commits as you scroll.
- Preference for the number of commits the graph shows first.
- Preference to keep submodules up to date after a pull or a checkout.
- Preference to prune remote branches deleted on the server on every fetch (on by default).
- Stash with a title and an optional description.

## 0.22.1 — 2026-10-19
- Changelog in Preferences › About.
- "What's new" dialog on the first launch of each new version.

## 0.21.3 — 2026-10-19
- The commit dropdown is disabled when no commit is possible.

## 0.21.2 — 2026-10-19
- Folder context menu in the working directory tree.

## 0.21.1 — 2026-10-19
- Native AOT build fixed on .NET 11 RC2.

## 0.21.0 — 2026-10-19
- Stage and unstage several files or a whole folder at once.
- Projects can take any color, chosen with a color picker.

## 0.20.0 — 2026-10-19
- Delete SSH keys and generate GPG keys from the preferences.

## 0.19.1 — 2026-10-16
- Hide branches, tags, stashes and worktrees from the graph.

## 0.19.0 — 2026-10-16
- Stashes shown in the graph, with a stash menu; apply only the selected stash files.
- Read-only Azure DevOps work item screen.

## 0.18.3 — 2026-10-16
- AI agent checkpoint refs are hidden from the graph.

## 0.18.2 — 2026-10-16
- Online help command opening the documentation site.

## 0.18.1 — 2026-10-16
- Summary and statistics pages in the left panel.
- Clone from a connected hosting account; new clone dialog layout.
- Conflict prevention against the default remote branch.
- Detailed pull request checks (GitHub and Azure DevOps).

## 0.17.0 — 2026-10-15
- The whole application is available in French and English.

## 0.16.0 — 2026-10-15
- First French / English localization of the main views.

## 0.15.0 — 2026-10-14
- Repository and branch selectors and commit search in the toolbar.
- Inline pull request review in the editor; PR files as a resizable tree.
- Azure DevOps pull requests: votes, threads, comments, draft, merge.
- Richer pull request creation: reviewers, labels, assignees, linked issues.
- Force push offered after a rejected push.
- Pull request suggested when a branch is dropped on another badge.

## 0.14.1 — 2026-10-13
- Read-only Azure DevOps pull request screen with file diffs.

## 0.14.0 — 2026-10-13
- Integrated terminal and Git command log in the bottom panel.

## 0.13.0 — 2026-10-13
- AI-generated commit messages (Claude, Copilot CLI or API).
- Git Flow (init, start, finish).
- Signing format and key per Git profile.
- Fix: tag creation from the graph menu.

## 0.12.0 — 2026-10-13
- Create, edit, close and reopen GitHub issues.
- Deleted remote branches pruned on fetch; remote branches grouped in folders.

## 0.11.0 — 2026-10-13
- Filter, page and comment GitHub issues.
- Light theme: more legible diffs; non-UTF-8 files decoded.
- Fix: empty graph after a session restore.

## 0.10.0 — 2026-10-13
- Expiring GitHub tokens renewed automatically.

## 0.9.0 — 2026-10-12
- Double click a branch badge to check it out and pull.
- Local changes carried automatically on checkout.

## 0.8.0 — 2026-10-12
- Inline pull request review, auto-merge, reactions and branch update.

## 0.7.0 — 2026-10-12
- Restart button in the status bar when an update is ready.

## 0.6.0 — 2026-10-12
- Azure DevOps sign-in with a token only, from its URL.

## 0.5.0 — 2026-10-12
- Repository tabs grouped into colored projects.
- GitHub pull request screen with Markdown and write actions.

## 0.4.0 — 2026-10-10
- Git LFS section: track, untrack and pull.

## 0.3.0 — 2026-10-10
- Customizable graph lane colors.

## 0.2.0-beta.1 — 2026-10-10
- Commit signature verification in the commit details.
- Configurable keyboard shortcuts.
- About section in the preferences.

## 0.1.0-beta.3 — 2026-10-09
- Updates checked every hour.

## 0.1.0-beta.2 — 2026-10-09
- App version shown in the status bar.
- Fix: commit details spinner stuck.

## 0.1.0-beta.1 — 2026-10-09
First beta.
- Commit graph, details, diffs, stage / unstage by file, hunk or line, commit and amend.
- Fetch, pull, push; branches, tags, remotes, merge, rebase (interactive too), cherry-pick, revert, reset, stash.
- Code editor for diffs, conflict editor, image diff, undo / redo.
- Tabs, multiple windows, session restore, command palette, themes and density.
- GitHub and Azure DevOps accounts, pull requests and issues.
- Worktrees, submodules, multiple Git profiles.
- Installer and automatic updates.
