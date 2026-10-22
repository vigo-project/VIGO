using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Views;

public sealed partial class IssueMetadataDialog : ContentDialog
{
    public IssueMetadataDialog(IssueMetadataDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public IssueMetadataDialogViewModel ViewModel { get; }

    public bool NoLabels => !ViewModel.HasLabels;

    public bool NoAssignees => !ViewModel.HasAssignees;
}
