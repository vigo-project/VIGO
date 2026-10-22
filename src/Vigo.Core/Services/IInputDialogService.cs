using Vigo.Core.ViewModels;

namespace Vigo.Core.Services;

/// <summary>Shows a name dialog (new branch, tag, remote; renaming) and waits for the user.</summary>
public interface IInputDialogService
{
    /// <summary>
    /// True when the user confirmed (the fields of <paramref name="dialog"/> then hold valid values); false when
    /// canceled or when another dialog is already shown.
    /// </summary>
    Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same for the labels, assignees and milestone of an issue (J6.10d). The default is "not available" (false): a window without
    /// that dialog leaves the edit to the browser.
    /// </summary>
    Task<bool> ShowAsync(IssueMetadataDialogViewModel dialog, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Same for the creation of a pull request with reviewers, labels and linked issues (J6.9l). The default is "not available" (false).
    /// </summary>
    Task<bool> ShowAsync(NewPullRequestDialogViewModel dialog, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
