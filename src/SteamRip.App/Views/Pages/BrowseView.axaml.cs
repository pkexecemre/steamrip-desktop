using Avalonia.Controls;

namespace SteamRip.App.Views.Pages;

public partial class BrowseView : UserControl
{
    public BrowseView()
    {
        InitializeComponent();
        Loaded += (_, _) => RuleSweep.Play(Rule);
    }
}
