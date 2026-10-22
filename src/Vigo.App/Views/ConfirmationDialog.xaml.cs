using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Services;

namespace Vigo.App.Views;

public sealed partial class ConfirmationDialog : ContentDialog
{
    public ConfirmationDialog(ConfirmationRequest request)
    {
        Request = request;
        InitializeComponent();
        // The danger style comes from XAML (a style read from the resources cannot be cast in C# under AOT).
        if (!request.IsDestructive) ClearValue(PrimaryButtonStyleProperty);
    }

    // No default button: ContentDialog paints it with the accent style, and "Annuler" must stay neutral next to the danger button.
    // Focus "Annuler" instead, so that Enter still cancels and never destroys anything.
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Deferred: the dialog moves the focus to its first button after raising Opened.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindCloseButton(this) is { } close) close.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        });
    }

    private static Microsoft.UI.Xaml.Controls.Control? FindCloseButton(Microsoft.UI.Xaml.DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Control { Name: "CloseButton" } control) return control;
            if (FindCloseButton(child) is { } found) return found;
        }
        return null;
    }

    public ConfirmationRequest Request { get; }

    public bool HasOption => Request.OptionText is not null;

    public bool OptionChecked => Option.IsChecked == true;
}
