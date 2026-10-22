namespace Vigo.Core.Models;

/// <summary>A hook of the repository's hooks folder (J7.31); a disabled one is the file renamed <c>name.disabled</c>.</summary>
public sealed record HookEntry(string Name, bool IsEnabled);

/// <summary>Hook settings of a repository (J7.31).</summary>
/// <param name="HooksPath"><c>core.hooksPath</c> of the local config as written (null: default <c>.git/hooks</c>).</param>
/// <param name="Hooks">Hooks of the effective folder, without the <c>.sample</c> files.</param>
/// <param name="SkipHooks"><c>vigo.skiphooks</c>: commits and pushes made by VIGO pass <c>--no-verify</c>.</param>
public sealed record RepositoryHooks(string? HooksPath, IReadOnlyList<HookEntry> Hooks, bool SkipHooks);
