using Avalonia.Controls;

namespace SteamRip.App.Views.Pages;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) => RuleSweep.Play(Rule);
    }
}
