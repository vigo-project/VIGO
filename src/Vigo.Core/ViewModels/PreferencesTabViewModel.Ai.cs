using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Choice of the engine of the AI commit message.</summary>
public sealed record AiProviderOption(AiProvider Provider, string Label)
{
    public override string ToString() => Label;
}

/// <summary>"Message de commit IA" of Préférences › Éditeur et outils (J7.9, ADR-070).</summary>
public sealed partial class PreferencesTabViewModel
{
    private readonly CommitMessageGenerator? _commitMessages;

    /// <summary>The generator is there: the section is shown.</summary>
    public bool HasAi => _commitMessages is not null;

    public ObservableCollection<AiProviderOption> AiProviderOptions { get; } =
    [
        new(AiProvider.Off, Loc.Get("Ai.Provider.Off")),
        new(AiProvider.Cli, Loc.Get("Ai.Provider.Cli")),
        new(AiProvider.Api, Loc.Get("Ai.Provider.Api")),
    ];

    public ObservableCollection<AiCliToolOption> AiCliToolOptions { get; } =
    [
        new(AiCliTool.Claude, "Claude (claude -p)"),
        new(AiCliTool.Copilot, "GitHub Copilot (copilot -p)"),
    ];

    /// <summary>Tool the command-line engine runs: it decides the default command and how the prompt is handed over.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiCliCommandPlaceholder), nameof(AiCliCommandHint))]
    public partial AiCliToolOption? AiCliToolChoice { get; set; }

    partial void OnAiCliToolChoiceChanged(AiCliToolOption? value)
    {
        if (value is null) return;
        Update(p => p with { AiCliTool = value.Tool });
        AiStatus = string.Empty;
        // The command field shows the command of the tool just chosen.
        AiCliCommandText = CommandTextOf(_preferences.Current with { AiCliTool = value.Tool });
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiCli), nameof(IsAiApi), nameof(IsAiOn))]
    [NotifyCanExecuteChangedFor(nameof(TestAiCommand))]
    public partial AiProviderOption? AiProviderChoice { get; set; }

    partial void OnAiProviderChoiceChanged(AiProviderOption? value)
    {
        if (value is null) return;
        Update(p => p with { AiProvider = value.Provider });
        AiStatus = string.Empty;
    }

    public bool IsAiOn => AiProviderChoice?.Provider is AiProvider.Cli or AiProvider.Api;

    /// <summary>The engine is a command line (Claude or Copilot): the tool and command fields are shown.</summary>
    public bool IsAiCli => AiProviderChoice?.Provider == AiProvider.Cli;

    public bool IsAiApi => AiProviderChoice?.Provider == AiProvider.Api;

    /// <summary>Command line of the CLI engine; empty means <see cref="AiSettings.DefaultCliCommand"/>.</summary>
    [ObservableProperty]
    public partial string AiCliCommandText { get; set; } = string.Empty;

    partial void OnAiCliCommandTextChanged(string value)
    {
        // Each tool keeps its own command.
        if (AiCliToolChoice?.Tool == AiCliTool.Copilot) Update(p => p with { AiCopilotCommand = value });
        else Update(p => p with { AiCliCommand = value });
    }

    public string AiCliCommandPlaceholder => AiSettings.DefaultCommandFor(AiCliToolChoice?.Tool ?? AiCliTool.Claude);

    /// <summary>How the prompt reaches the tool, for the description of the command field.</summary>
    public string AiCliCommandHint => AiCliToolChoice?.Tool == AiCliTool.Copilot
        ? Loc.Get("Ai.CommandHint.Copilot")
        : Loc.Get("Ai.CommandHint.Claude");

    private static string CommandTextOf(UserPreferences p) =>
        (p.AiCliTool == AiCliTool.Copilot ? p.AiCopilotCommand : p.AiCliCommand) ?? string.Empty;

    /// <summary>Model name; empty means the engine's default.</summary>
    [ObservableProperty]
    public partial string AiModelText { get; set; } = string.Empty;

    partial void OnAiModelTextChanged(string value) => Update(p => p with { AiModel = value });

    public static string AiModelPlaceholder => AiSettings.DefaultApiModel;

    /// <summary>Key being typed (never read back from the vault, never logged).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAiApiKeyCommand))]
    public partial string AiApiKeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiApiKeyState))]
    public partial bool HasAiApiKey { get; private set; }

    public string AiApiKeyState => Loc.Get(HasAiApiKey ? "Ai.KeyState.Stored" : "Ai.KeyState.None");

    /// <summary>Result of the last "Tester" (or of a key change).</summary>
    [ObservableProperty]
    public partial string AiStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestAiCommand))]
    public partial bool IsAiTesting { get; private set; }

    private void SyncAi(UserPreferences current)
    {
        if (_commitMessages is null) return;
        AiProviderChoice = AiProviderOptions.FirstOrDefault(o => o.Provider == current.AiProvider) ?? AiProviderOptions[0];
        AiCliToolChoice = AiCliToolOptions.FirstOrDefault(o => o.Tool == current.AiCliTool) ?? AiCliToolOptions[0];
        AiCliCommandText = CommandTextOf(current);
        AiModelText = current.AiModel ?? string.Empty;
        HasAiApiKey = _commitMessages.HasApiKey;
    }

    [RelayCommand(CanExecute = nameof(CanSaveAiApiKey))]
    private void SaveAiApiKey()
    {
        if (_commitMessages is null) return;
        _commitMessages.SaveApiKey(AiApiKeyInput);
        AiApiKeyInput = string.Empty;
        HasAiApiKey = true;
        AiStatus = Loc.Get("Ai.Status.KeySaved");
    }

    private bool CanSaveAiApiKey() => !string.IsNullOrWhiteSpace(AiApiKeyInput);

    [RelayCommand]
    private void RemoveAiApiKey()
    {
        if (_commitMessages is null) return;
        _commitMessages.RemoveApiKey();
        HasAiApiKey = false;
        AiStatus = Loc.Get("Ai.Status.KeyRemoved");
    }

    /// <summary>"Tester": the command exists, or the key and the model are accepted by the API (one tiny request).</summary>
    [RelayCommand(CanExecute = nameof(CanTestAi))]
    private async Task TestAiAsync(CancellationToken cancellationToken)
    {
        if (_commitMessages is null) return;
        IsAiTesting = true;
        AiStatus = Loc.Get("Ai.Status.Testing");
        try
        {
            AiStatus = await _commitMessages.CheckAsync(cancellationToken) switch
            {
                Done => Loc.Get("Ai.Status.Works"),
                Failure f => f.Message,
                AuthenticationFailed a => a.Message,
                Canceled => string.Empty,
                _ => Loc.Get("Common.UnexpectedResult"),
            };
        }
        finally
        {
            IsAiTesting = false;
        }
    }

    private bool CanTestAi() => !IsAiTesting && IsAiOn;
}

/// <summary>Choice of the tool run by the command-line engine.</summary>
public sealed record AiCliToolOption(AiCliTool Tool, string Label)
{
    public override string ToString() => Label;
}
