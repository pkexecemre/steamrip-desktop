using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace SteamRip.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Below this the rail drops its labels and keeps the glyphs and counts only.</summary>
    private const double CompactWidth = 900;

    private const double RailWidth = 216;
    private const double CompactRailWidth = 64;

    /// <summary>A section slides up and settles; only navigation runs it, never the first layout.</summary>
    private static readonly Animation PageIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(240),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0d),
                Setters =
                {
                    new Setter(OpacityProperty, 0d),
                    new Setter(
                        RenderTransformProperty,
                        TransformOperations.Parse("translateY(12px)")
                    ),
                },
            },
            new KeyFrame
            {
                Cue = new Cue(1d),
                Setters =
                {
                    new Setter(OpacityProperty, 1d),
                    new Setter(
                        RenderTransformProperty,
                        TransformOperations.Parse("translateY(0px)")
                    ),
                },
            },
        },
    };

    private INotifyPropertyChanged? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyWidth(Bounds.Width);
        DataContextChanged += (_, _) => WatchSection();
        WatchSection();
        ApplyWidth(Bounds.Width);
    }

    private void WatchSection()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as INotifyPropertyChanged;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the section switch animates; bindings on the page fire the same event constantly.
        if (e.PropertyName != "Section" || this.FindControl<ContentControl>("Page") is not { } page)
            return;
        _ = AnimateInAsync(page);
    }

    private static async Task AnimateInAsync(Control page)
    {
        try
        {
            await PageIn.RunAsync(page);
        }
        catch (Exception)
        {
            // an animation is never worth a blank window
        }
        finally
        {
            // Whatever the clock did, the page must end visible and in place.
            page.Opacity = 1;
            page.RenderTransform = TransformOperations.Parse("none");
        }
    }

    private void ApplyWidth(double width)
    {
        var compact = width < CompactWidth;
        var rail = this.FindControl<Border>("Rail");
        var shell = this.FindControl<Grid>("Shell");
        var sheet = this.FindControl<Border>("Sheet");
        if (rail is null || shell is null)
            return;

        // The sheet never takes more than ~86% of the window, so it stays usable on a small one.
        if (sheet is not null)
            sheet.Width = Math.Clamp(width * 0.86, 360, 560);

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
