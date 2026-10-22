using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Views;

public sealed partial class InputDialog : ContentDialog
{
    public InputDialog(InputDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        NameBox.TextChanging += (box, _) =>
        {
            if (!ViewModel.SpacesBecomeDashes) return;
            var fixedText = InputDialogViewModel.ReplaceSpaces(box.Text);
            if (fixedText == box.Text) return;
            var caret = box.SelectionStart;
            box.Text = fixedText;
            box.SelectionStart = caret;
        };
        Opened += (_, _) =>
        {
            // Ready to type; a prefilled name (renaming) is selected so that typing replaces it.
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectAll();
        };
    }

    public InputDialogViewModel ViewModel { get; }

    /// <summary>A message field shows a few lines; a URL field one.</summary>
    public double SecondMinHeight => ViewModel.IsSecondMultiline ? 88 : 0;
}
