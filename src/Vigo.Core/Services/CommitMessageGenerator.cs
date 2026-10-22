using System.Text;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Core.Services;

/// <summary>What a backend is asked: the prompt and the user's settings for it.</summary>
/// <param name="Prompt">Instructions and staged changes.</param>
/// <param name="Model">Model name; null for the backend's default.</param>
/// <param name="CliCommand">Command line of the CLI backend (<c>claude -p</c>); null for the default of <paramref name="Tool"/>.</param>
/// <param name="Tool">Tool the CLI backend runs.</param>
public sealed record AiRequest(string Prompt, string? Model, string? CliCommand, AiCliTool Tool = AiCliTool.Claude);

/// <summary>A way to get a text completion (CLI on the machine, or HTTP API). Failures are <see cref="Failure"/> with a readable message.</summary>
public interface IAiBackend
{
    AiProvider Provider { get; }

    /// <summary>Whether the backend can run now (a key is stored, the command exists) — the reason otherwise.</summary>
    Task<GitResult> CheckAsync(AiRequest settings, CancellationToken cancellationToken = default);

    Task<GitResult<string>> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Defaults of the AI settings.</summary>
public static class AiSettings
{
    public const string DefaultCliCommand = "claude -p";

    /// <summary><c>{prompt}</c> is replaced by the prompt (cut to fit the Windows command line); <c>--silent</c> prints the reply only.</summary>
    public const string DefaultCopilotCommand = "copilot -p {prompt} --silent";

    public const string PromptPlaceholder = "{prompt}";

    public static string DefaultCommandFor(AiCliTool tool) => tool == AiCliTool.Copilot ? DefaultCopilotCommand : DefaultCliCommand;

    /// <summary>Characters of prompt that fit in an argument (CreateProcess allows 32 767 for the whole line).</summary>
    public const int MaxArgumentPromptChars = 24_000;

    /// <summary>Fast and cheap model for one-shot summaries.</summary>
    public const string DefaultApiModel = "claude-haiku-5-5";
}

/// <summary>A message proposed by a backend, split like the commit form (summary and description).</summary>
public sealed record GeneratedCommitMessage(string Summary, string Description);

/// <summary>Prompt and reply handling of the commit message generation (pure, tested).</summary>
public static class CommitMessagePrompt
{
    public static string Build(StagedPatch patch)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Write a git commit message for the staged changes below.");
        builder.AppendLine("Rules:");
        builder.AppendLine("- First line: imperative summary, 72 characters at most, no trailing period.");
        builder.AppendLine("- Then, only if the change deserves it, a blank line and a short body explaining what and why (wrapped at 72 columns).");
        builder.AppendLine("- Match the language and style of the recent commit subjects when they are given.");
        builder.AppendLine("- Reply with the commit message only: no quotes, no code fence, no explanation, no Co-Authored-By line.");
        if (patch.RecentSubjects.Count > 0)
        {
            builder.AppendLine().AppendLine("Recent commit subjects:");
            foreach (var subject in patch.RecentSubjects) builder.Append("- ").AppendLine(subject);
        }
        builder.AppendLine().AppendLine("Staged changes:");
        builder.AppendLine(patch.Text);
        if (patch.Truncated) builder.AppendLine("[The patch was truncated: summarize from what is shown.]");
        return builder.ToString();
    }

    /// <summary>Message of a reply: code fences, surrounding quotes and a "Commit message:" lead-in removed; null when empty.</summary>
    public static GeneratedCommitMessage? Parse(string reply)
    {
        var text = reply.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n', StringComparison.Ordinal);
            text = firstLine < 0 ? string.Empty : text[(firstLine + 1)..];
            var closing = text.LastIndexOf("```", StringComparison.Ordinal);
            if (closing >= 0) text = text[..closing];
            text = text.Trim();
        }

        const string leadIn = "commit message:";
        if (text.StartsWith(leadIn, StringComparison.OrdinalIgnoreCase)) text = text[leadIn.Length..].Trim();
        if (text.Length > 1 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        if (text.Length == 0) return null;

        var (summary, description) = CommitMessage.Split(text);
        return summary.Length == 0 ? null : new GeneratedCommitMessage(summary, description);
    }
}

/// <summary>
/// Message de commit IA (J7.9, ADR-070): reads the staged changes, asks the backend chosen in the preferences (CLI on the
/// machine or Anthropic API) and parses its reply. Off by default: nothing leaves the machine until the user picks one.
/// </summary>
public sealed class CommitMessageGenerator(Preferences preferences, IGitService git, ISecretVault vault, IEnumerable<IAiBackend> backends)
{
    private readonly IAiBackend[] _backends = [.. backends];

    /// <summary>An Anthropic API key is stored in the Credential Locker.</summary>
    public bool HasApiKey => vault.Retrieve(SecretNames.AnthropicApiKey) is { Length: > 0 };

    public void SaveApiKey(string key) => vault.Store(SecretNames.AnthropicApiKey, key.Trim());

    public void RemoveApiKey() => vault.Remove(SecretNames.AnthropicApiKey);

    /// <summary>A backend is chosen in the preferences.</summary>
    public bool IsEnabled => preferences.Current.AiProvider != AiProvider.Off;

    /// <summary>Raised when the preferences change (the button follows <see cref="IsEnabled"/>).</summary>
    public event EventHandler? Changed
    {
        add => preferences.Changed += value;
        remove => preferences.Changed -= value;
    }

    public async Task<GitResult<GeneratedCommitMessage>> GenerateAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var current = preferences.Current;
        if (_backends.FirstOrDefault(b => b.Provider == current.AiProvider) is not { } backend)
            return new Failure(Loc.Get("Ai.Error.Disabled"));

        var patch = await git.GetStagedPatchAsync(repositoryPath, cancellationToken: cancellationToken);
        switch (patch)
        {
            case Success<StagedPatch> { Value: { Text.Length: > 0 } staged }:
                var request = new AiRequest(CommitMessagePrompt.Build(staged), current.AiModel, CommandOf(current), current.AiCliTool);
                var reply = await backend.CompleteAsync(request, cancellationToken);
                switch (reply)
                {
                    case Success<string> s:
                        return CommitMessagePrompt.Parse(s.Value) is { } message
                            ? new Success<GeneratedCommitMessage>(message)
                            : new Failure(Loc.Get("Ai.Error.NoMessage"), Details: s.Value);
                    case Failure f: return f;
                    case AuthenticationFailed a: return a;
                    case Canceled c: return c;
                    case Conflict c: return c;
                }
                return new Failure(Loc.Get("Ai.Error.Unexpected"));
            case Success<StagedPatch>:
                return new Failure(Loc.Get("Ai.Error.NoStaged"));
            case Failure f: return f;
            case AuthenticationFailed a: return a;
            case Canceled c: return c;
            case Conflict c: return c;
        }
        return new Failure(Loc.Get("Ai.Error.Unexpected"));
    }

    private static string? CommandOf(UserPreferences p) => p.AiCliTool == AiCliTool.Copilot ? p.AiCopilotCommand : p.AiCliCommand;

    /// <summary>"Tester" of the preferences: whether the chosen backend can run.</summary>
    public async Task<GitResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = preferences.Current;
        return _backends.FirstOrDefault(b => b.Provider == current.AiProvider) is { } backend
            ? await backend.CheckAsync(new AiRequest(string.Empty, current.AiModel, CommandOf(current), current.AiCliTool), cancellationToken)
            : new Failure(Loc.Get("Ai.Error.NoBackend"));
    }
}
