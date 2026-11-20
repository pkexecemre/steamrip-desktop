using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using SteamRip.App.ViewModels;

namespace SteamRip.App.Views;

public partial class MainWindow : Window, IDisposable
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

    /// <summary>The toast rises into place; short enough that a second one does not queue behind it.</summary>
    private static readonly Animation SlideUp = new()
    {
        Duration = TimeSpan.FromMilliseconds(180),
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
                        TransformOperations.Parse("translateY(10px)")
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

    /// <summary>The launch sheet comes in from the right edge instead of appearing on top of the page.</summary>
    private static readonly Animation SheetIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(220),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0d),
                Setters =
                {
                    new Setter(OpacityProperty, 0.4d),
                    new Setter(
                        RenderTransformProperty,
                        TransformOperations.Parse("translateX(32px)")
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
                        TransformOperations.Parse("translateX(0px)")
                    ),
                },
            },
        },
    };

    private DispatcherTimer? _pulse;

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
        switch (e.PropertyName)
        {
            case "Section" when this.FindControl<ContentControl>("Page") is { } page:
                _ = AnimateInAsync(page, PageIn);
                break;
            case "ToastVisible" when this.FindControl<Border>("Toast") is { IsVisible: true } toast:
                _ = AnimateInAsync(toast, SlideUp);
                break;
            case "IsEditing" when this.FindControl<Border>("Sheet") is { IsVisible: true } sheet:
                _ = AnimateInAsync(sheet, SheetIn);
                break;
            case "Syncing":
                UpdatePulse();
                break;
        }
    }

    private static async Task AnimateInAsync(Control page, Animation animation)
    {
        try
        {
            await animation.RunAsync(page);
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

    /// <summary>Stops the pulsing dot when the window goes away; the animation holds a clock otherwise.</summary>
    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _pulse?.Stop();
        _pulse = null;
        GC.SuppressFinalize(this);
    }

    private void UpdatePulse()
    {
        var dot = this.FindControl<Ellipse>("SyncDot");
        _pulse?.Stop();
        _pulse = null;
        if (dot is null)
            return;
        dot.Opacity = 1;
        if (_viewModel is not MainViewModel { Syncing: true })
            return;
        // A value change with a DoubleTransition is the supported way to pulse: looping animations
        // cannot use Run(), and an infinite one holds the clock forever.
        _pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        var bright = true;
        _pulse.Tick += (_, _) =>
        {
            bright = !bright;
            dot.Opacity = bright ? 1 : 0.3;
        };
        _pulse.Start();
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
