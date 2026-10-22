using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Window-bound services of one tab (toasts, dialogs, folder picker, opening a repository), forwarded to the window
/// that currently hosts the tab. A tab keeps this relay for its whole life: moving it to another window only changes
/// <see cref="Host"/>, so its view models (built with these services) keep their state (J4.7).
/// </summary>
internal sealed class TabServices(ShellViewModel host)
    : IUserNotifier, IConfirmationService, IInputDialogService, IAuthenticationPrompt, IFolderPicker, IFileDialogs
{
    public ShellViewModel Host { get; set; } = host;

    public void Notify(NotificationSeverity severity, string title, string? message = null) =>
        Host.Notifier.Notify(severity, title, message);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
        Host.Confirmations?.ConfirmAsync(title, message, confirmText, cancellationToken) ?? Task.FromResult(false);

    public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default) =>
        Host.Confirmations?.ConfirmAsync(request, cancellationToken) ?? Task.FromResult(new ConfirmationOutcome(false, false));

    public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default) =>
        Host.Inputs?.ShowAsync(dialog, cancellationToken) ?? Task.FromResult(false);

    public Task<bool> ShowAsync(IssueMetadataDialogViewModel dialog, CancellationToken cancellationToken = default) =>
        Host.Inputs?.ShowAsync(dialog, cancellationToken) ?? Task.FromResult(false);

    public Task<bool> ShowAsync(NewPullRequestDialogViewModel dialog, CancellationToken cancellationToken = default) =>
        Host.Inputs?.ShowAsync(dialog, cancellationToken) ?? Task.FromResult(false);

    public Task<AuthenticationChoice?> ResolveAsync(AuthenticationProblem problem, CancellationToken cancellationToken = default) =>
        Host.Authentication?.ResolveAsync(problem, cancellationToken) ?? Task.FromResult<AuthenticationChoice?>(null);

    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) =>
        Host.FolderPicker.PickFolderAsync(cancellationToken);

    public Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default) =>
        Host.FileDialogs?.PickSaveFileAsync(suggestedName, filters, cancellationToken) ?? Task.FromResult<string?>(null);

    public Task<string?> PickOpenFileAsync(IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default) =>
        Host.FileDialogs?.PickOpenFileAsync(filters, cancellationToken) ?? Task.FromResult<string?>(null);

    public Task OpenRepositoryAsync(string path, CancellationToken cancellationToken) =>
        Host.OpenRepositoryAsync(path, cancellationToken);

    public Task CloneAsync(CancellationToken cancellationToken) => Host.CloneCommand.ExecuteAsync(cancellationToken);
}
