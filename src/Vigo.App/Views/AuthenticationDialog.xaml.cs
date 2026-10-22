using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Views;

public sealed partial class AuthenticationDialog : ContentDialog
{
    public AuthenticationDialog(AuthenticationDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public AuthenticationDialogViewModel ViewModel { get; }

    // PasswordBox.Password is not bound: the token goes to the view model only, and is cleared from the box on close.
    private void OnTokenChanged(object sender, RoutedEventArgs e) => ViewModel.Token = TokenBox.Password;
}
