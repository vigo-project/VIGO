using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.App.Services;

/// <summary>Current main window (handle for native pickers, UI dispatcher).</summary>
public sealed class WindowContext
{
    public WindowId WindowId { get; set; }
    public DispatcherQueue? Dispatcher { get; set; }

    /// <summary>Root of the window content, for dialogs (set once the content is loaded).</summary>
    public Microsoft.UI.Xaml.XamlRoot? XamlRoot { get; set; }

    /// <summary>A ContentDialog is shown: WinUI refuses a second one, so services check and set this flag.</summary>
    public bool IsDialogOpen { get; set; }
}

/// <summary>Windows App SDK folder picker (works both packaged and unpackaged).</summary>
public sealed class FolderPickerService(WindowContext window) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        var picker = new FolderPicker(window.WindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}

/// <summary>Windows App SDK open and save pickers (J7.12): patch files; one per window (ADR-033).</summary>
public sealed class FileDialogService(WindowContext window) : IFileDialogs
{
    public async Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default)
    {
        var picker = new FileSavePicker(window.WindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        foreach (var filter in filters.Where(f => !f.Extensions.Contains("*")))
            picker.FileTypeChoices.Add(filter.Name, [.. filter.Extensions]);
        if (filters.Count > 0 && filters[0].Extensions.Count > 0 && filters[0].Extensions[0] != "*")
            picker.DefaultFileExtension = filters[0].Extensions[0];
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickOpenFileAsync(IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default)
    {
        var picker = new FileOpenPicker(window.WindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        foreach (var extension in filters.SelectMany(f => f.Extensions)) picker.FileTypeFilter.Add(extension);
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}

/// <summary>
/// Confirmation of irreversible actions in a ContentDialog; the default button is "Annuler" so that Enter never
/// destroys anything. One dialog at a time (WinUI refuses a second one): a request made meanwhile is refused.
/// </summary>
public sealed class ConfirmationService(WindowContext window) : IConfirmationService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default)
    {
        if (window.IsDialogOpen || window.XamlRoot is not { } root) return false;
        // The XAML dialog gives "Annuler" its neutral style (a style read from the resources cannot be cast under AOT).
        var dialog = new Views.ConfirmationDialog(new ConfirmationRequest(title, message, confirmText))
        {
            XamlRoot = root,
            RequestedTheme = Appearance.Current.Theme,
        };
        window.IsDialogOpen = true;
        try
        {
            using var registration = cancellationToken.Register(dialog.Hide);
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            window.IsDialogOpen = false;
        }
    }

    /// <summary>Confirmation with an option and a danger button (<see cref="Views.ConfirmationDialog"/>).</summary>
    public async Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        if (window.IsDialogOpen || window.XamlRoot is not { } root) return new ConfirmationOutcome(false, false);
        var dialog = new Views.ConfirmationDialog(request) { XamlRoot = root, RequestedTheme = Appearance.Current.Theme };
        window.IsDialogOpen = true;
        try
        {
            using var registration = cancellationToken.Register(dialog.Hide);
            var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            return new ConfirmationOutcome(confirmed, confirmed && dialog.OptionChecked);
        }
        finally
        {
            window.IsDialogOpen = false;
        }
    }
}

/// <summary>Name dialogs (<see cref="Views.InputDialog"/>); refused while another dialog is shown.</summary>
public sealed class InputDialogService(WindowContext window) : IInputDialogService
{
    public async Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
    {
        if (window.IsDialogOpen || window.XamlRoot is not { } root) return false;
        var view = new Views.InputDialog(dialog) { XamlRoot = root, RequestedTheme = Appearance.Current.Theme };
        window.IsDialogOpen = true;
        try
        {
            using var registration = cancellationToken.Register(view.Hide);
            return await view.ShowAsync() == ContentDialogResult.Primary && dialog.CanConfirm;
        }
        finally
        {
            window.IsDialogOpen = false;
        }
    }

    public async Task<bool> ShowAsync(IssueMetadataDialogViewModel dialog, CancellationToken cancellationToken = default)
    {
        if (window.IsDialogOpen || window.XamlRoot is not { } root) return false;
        var view = new Views.IssueMetadataDialog(dialog) { XamlRoot = root, RequestedTheme = Appearance.Current.Theme };
        window.IsDialogOpen = true;
        try
        {
            using var registration = cancellationToken.Register(view.Hide);
            return await view.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            window.IsDialogOpen = false;
        }
    }

    public async Task<bool> ShowAsync(NewPullRequestDialogViewModel dialog, CancellationToken cancellationToken = default)
    {
        if (window.IsDialogOpen || window.XamlRoot is not { } root) return false;
        var view = new Views.NewPullRequestDialog(dialog) { XamlRoot = root, RequestedTheme = Appearance.Current.Theme };
        window.IsDialogOpen = true;
        try
        {
            using var registration = cancellationToken.Register(view.Hide);
            return await view.ShowAsync() == ContentDialogResult.Primary && dialog.CanConfirm;
        }
        finally
        {
            window.IsDialogOpen = false;
        }
    }
}

/// <summary>
/// Authentication dialog after a refused fetch / pull / push (<see cref="Views.AuthenticationDialog"/>). Returns null
/// when canceled, or when another dialog is already shown (the caller then reports the failure as a toast).
/// </summary>
public sealed class AuthenticationPromptService(WindowContext window) : IAuthenticationPrompt
{
    public async Task<AuthenticationChoice?> ResolveAsync(AuthenticationProblem problem, CancellationToken cancellationToken = default)
    {
        if (window.IsDialogOpen || window.XamlRoot is not { } root) return null;
        var viewModel = new AuthenticationDialogViewModel(problem);
        var dialog = new Views.AuthenticationDialog(viewModel) { XamlRoot = root, RequestedTheme = Appearance.Current.Theme };
        window.IsDialogOpen = true;
        try
        {
            using var registration = cancellationToken.Register(dialog.Hide);
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? viewModel.ToChoice() : null;
        }
        finally
        {
            window.IsDialogOpen = false;
            // The token only lives as long as the dialog.
            viewModel.Token = string.Empty;
        }
    }
}

/// <summary>
/// "Cloner un dépôt" dialog (<see cref="Views.CloneDialog"/>). False when the user closed it or another dialog is shown.
/// </summary>
public sealed class CloneDialogService(WindowContext window) : ICloneDialog
{
    public async Task<bool> ShowAsync(CloneViewModel clone, CancellationToken cancellationToken = default)
    {
        if (window.IsDialogOpen || window.XamlRoot is not { } root) return false;
        var dialog = new Views.CloneDialog(clone) { XamlRoot = root, RequestedTheme = Appearance.Current.Theme };
        window.IsDialogOpen = true;
        try
        {
            using var registration = cancellationToken.Register(dialog.Hide);
            await dialog.ShowAsync();
            return dialog.ClosedByViewModel;
        }
        finally
        {
            window.IsDialogOpen = false;
        }
    }
}

/// <summary>In-app toasts shown at the bottom right of the window (stacked InfoBars).</summary>
public sealed class UserNotifier(WindowContext window) : IUserNotifier
{
    public ObservableCollection<Notification> Items { get; } = [];

    public void Notify(NotificationSeverity severity, string title, string? message = null)
    {
        var item = new Notification(severity switch
        {
            NotificationSeverity.Success => InfoBarSeverity.Success,
            NotificationSeverity.Warning => InfoBarSeverity.Warning,
            NotificationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        }, title, message ?? string.Empty);

        if (window.Dispatcher is { HasThreadAccess: false } dispatcher)
            dispatcher.TryEnqueue(() => Add(item));
        else
            Add(item);
    }

    // Non-blocking toasts close by themselves: the countdown is the animation of their bottom bar (MainWindow).
    private void Add(Notification item)
    {
        Items.Add(item);
        if (Items.Count > 3) Items.RemoveAt(0);
    }
}

public sealed partial class Notification(InfoBarSeverity severity, string title, string message)
{
    /// <summary>Time a non-blocking toast stays shown (pointer over it: paused).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    public InfoBarSeverity Severity { get; } = severity;
    public string Title { get; } = title;
    public string Message { get; } = message;

    /// <summary>Errors stay until closed; the other toasts close at the end of their countdown bar.</summary>
    public bool AutoCloses => Severity != InfoBarSeverity.Error;

    /// <summary>Countdown animation (bar shrinking over <see cref="Lifetime"/>), started once the toast is shown.</summary>
    internal Microsoft.UI.Xaml.Media.Animation.Storyboard? Countdown { get; set; }
}
