using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Views;

public sealed partial class NewPullRequestDialog : ContentDialog
{
    public NewPullRequestDialog(NewPullRequestDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public NewPullRequestDialogViewModel ViewModel { get; }
}
