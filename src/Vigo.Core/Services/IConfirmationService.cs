namespace Vigo.Core.Services;

/// <summary>An irreversible action to confirm, possibly with one option (mockup: "Supprimer aussi origin/fix/cache-ttl").</summary>
/// <param name="OptionText">Check box shown under the message; null for none.</param>
/// <param name="IsDestructive">The confirm button is shown in the danger color.</param>
public sealed record ConfirmationRequest(
    string Title, string Message, string ConfirmText, string? OptionText = null, bool OptionChecked = false, bool IsDestructive = false);

/// <summary>The user's answer to a <see cref="ConfirmationRequest"/>.</summary>
public sealed record ConfirmationOutcome(bool Confirmed, bool OptionChecked);

/// <summary>Asks the user to confirm an irreversible action (modal dialog).</summary>
public interface IConfirmationService
{
    /// <summary>True when the user chose <paramref name="confirmText"/>; the default button is the cancel one.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default);

    /// <summary>Same as the simple overload, with the option of the request; the default button is the cancel one.</summary>
    async Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default) =>
        new(await ConfirmAsync(request.Title, request.Message, request.ConfirmText, cancellationToken), request.OptionChecked);
}

/// <summary>Used when no dialog is available (tests, headless): every destructive action is refused.</summary>
public sealed class RefuseConfirmations : IConfirmationService
{
    public static RefuseConfirmations Instance { get; } = new();

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
