using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Views;

public sealed partial class CloneDialog : ContentDialog
{
    public CloneDialog(CloneViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => ViewModel.CloseRequested -= OnCloseRequested;
        Opened += OnOpened;
        // "Cloner" keeps the dialog open: the progress is shown in it until the view model closes it.
        PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            ViewModel.CloneCommand.Execute(null);
        };
    }

    public CloneViewModel ViewModel { get; }

    /// <summary>Closed by the view model (cloned, or authentication needed), not by the user.</summary>
    public bool ClosedByViewModel { get; private set; }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (ViewModel.StartAutomatically)
        {
            ViewModel.CloneCommand.Execute(null);
            return;
        }
        UrlBox.Focus(FocusState.Programmatic);
        UrlBox.SelectAll();
    }

    // A double click on a repository of the list clones it (the first click selected it and filled the address).
    private void OnRepositoryDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.CloneCommand.CanExecute(null)) ViewModel.CloneCommand.Execute(null);
    }

    // A project header is only a title: the pointer and the Tab key pass over it (rows are recycled, so both cases are set).
#pragma warning disable CA1822 // An event handler named in XAML cannot be static.
    private void OnRowContainerChanging(Microsoft.UI.Xaml.Controls.ListViewBase sender, Microsoft.UI.Xaml.Controls.ContainerContentChangingEventArgs args)
    {
        var header = args.Item is CloneRepositoryItem { IsHeader: true };
        args.ItemContainer.IsHitTestVisible = !header;
        args.ItemContainer.IsTabStop = !header;
    }
#pragma warning restore CA1822

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        ClosedByViewModel = true;
        Hide();
    }
}
