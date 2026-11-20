using Avalonia.Controls;

namespace SteamRip.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Below this the rail drops its labels and keeps the icons and counts only.</summary>
    private const double CompactWidth = 900;

    private const double RailWidth = 216;
    private const double CompactRailWidth = 64;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyWidth(Bounds.Width);
        ApplyWidth(Bounds.Width);
    }

    private void ApplyWidth(double width)
    {
        var compact = width < CompactWidth;
        var rail = this.FindControl<Border>("Rail");
        var shell = this.FindControl<Grid>("Shell");
        if (rail is null || shell is null)
            return;

        if (compact == rail.Classes.Contains("compact"))
            return;

        if (compact)
            rail.Classes.Add("compact");
        else
            rail.Classes.Remove("compact");

        rail.Width = compact ? CompactRailWidth : RailWidth;
        shell.ColumnDefinitions[0].Width = new GridLength(compact ? CompactRailWidth : RailWidth);
    }
}
