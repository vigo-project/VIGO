using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Controls;

namespace Vigo.App.Views;

/// <summary>Entry of a menu of the profiles (status bar, welcome screen): the active one checked, the icon of the profile when it has one.</summary>
internal static class ProfileMenuItems
{
    public static ToggleMenuFlyoutItem Create(string label, bool isActive, string? icon, Action pick)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = isActive };
        if (!string.IsNullOrEmpty(icon))
        {
            item.Icon = new FontIcon
            {
                FontFamily = new FontFamily("ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"),
                Glyph = FluentIcons.FromName(icon),
            };
        }
        item.Click += (_, _) => pick();
        return item;
    }
}
