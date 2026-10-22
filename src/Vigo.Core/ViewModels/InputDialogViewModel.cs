using CommunityToolkit.Mvvm.ComponentModel;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Name dialog (new branch, tag or remote, renaming; mockup "Boîte de dialogue"): a name validated while typing, an
/// optional second field (tag message, remote URL) and an optional check box ("Extraire après création").
/// </summary>
/// <remarks>
/// A validator returns null when the value is valid, an empty string when it is invalid without anything worth saying
/// (unchanged name), or the reason shown under the field. The texts are given by the callers (already localized).
/// </remarks>
public sealed partial class InputDialogViewModel : ObservableObject
{
    private readonly Func<string, string?> _validateName;
    private readonly Func<string, string?>? _validateSecond;

    public InputDialogViewModel(
        string title, string confirmText, string nameHeader, Func<string, string?> validateName,
        string name = "", Func<string, string?>? validateSecond = null)
    {
        Title = title;
        ConfirmText = confirmText;
        NameHeader = nameHeader;
        _validateName = validateName;
        _validateSecond = validateSecond;
        Name = name;
    }

    public string Title { get; }

    /// <summary>Line under the title ("Depuis a1b2c3d · Fix cache TTL"); hidden when empty.</summary>
    public string Description { get; init; } = string.Empty;

    public bool HasDescription => Description.Length > 0;

    public string ConfirmText { get; }

    public string NameHeader { get; }

    public string NamePlaceholder { get; init; } = string.Empty;

    /// <summary>Branch, tag and remote names: whitespace typed or pasted in the name field turns into "-" (see <see cref="ReplaceSpaces"/>).</summary>
    public bool SpacesBecomeDashes { get; init; }

    /// <summary>Replaces each whitespace character with "-" (same length, so the caret position is unchanged).</summary>
    public static string ReplaceSpaces(string text) => text.Any(char.IsWhiteSpace) ? string.Concat(text.Select(c => char.IsWhiteSpace(c) ? '-' : c)) : text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError), nameof(HasNameError), nameof(CanConfirm))]
    public partial string Name { get; set; }

    /// <summary>Why the name is refused; empty while it is valid, or while nothing has been typed yet.</summary>
    public string NameError => Name.Length == 0 ? string.Empty : _validateName(Name.Trim()) ?? string.Empty;

    public bool HasNameError => NameError.Length > 0;

    /// <summary>Header of the second field; null hides it.</summary>
    public string? SecondHeader { get; init; }

    public bool HasSecondField => SecondHeader is not null;

    public string SecondPlaceholder { get; init; } = string.Empty;

    /// <summary>Several lines (tag message) rather than one (URL).</summary>
    public bool IsSecondMultiline { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondError), nameof(HasSecondError), nameof(CanConfirm))]
    public partial string SecondText { get; set; } = string.Empty;

    public string SecondError => SecondText.Length == 0 || _validateSecond is null ? string.Empty : _validateSecond(SecondText.Trim()) ?? string.Empty;

    public bool HasSecondError => SecondError.Length > 0;

    /// <summary>Text of the check box; null hides it.</summary>
    public string? OptionText { get; init; }

    public bool HasOption => OptionText is not null;

    [ObservableProperty]
    public partial bool OptionChecked { get; set; }

    public bool CanConfirm => _validateName(Name.Trim()) is null && (_validateSecond is null || _validateSecond(SecondText.Trim()) is null);

    /// <summary>The name as it will be used (trimmed).</summary>
    public string Value => Name.Trim();

    public string SecondValue => SecondText.Trim();
}
