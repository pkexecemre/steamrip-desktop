using Avalonia.Controls;

namespace SteamRip.App.Views.Pages;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        Loaded += (_, _) => RuleSweep.Play(Rule);
    }
}
