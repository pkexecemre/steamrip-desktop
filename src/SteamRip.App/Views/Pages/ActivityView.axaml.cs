using Avalonia.Controls;

namespace SteamRip.App.Views.Pages;

public partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
        Loaded += (_, _) => RuleSweep.Play(Rule);
    }
}
