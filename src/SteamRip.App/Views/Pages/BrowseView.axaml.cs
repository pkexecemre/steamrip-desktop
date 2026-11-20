using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SteamRip.App.Views.Pages;

public partial class BrowseView : UserControl
{
    private const int Staggered = 16;
    private const double StaggerMs = 35;

    private static readonly Animation TileIn = new()
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

    private bool _shown;

    public BrowseView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RuleSweep.Play(Rule);
            if (_shown)
                return;
            _shown = true;
            Dispatcher.UIThread.Post(DelayedEntrance);
        };
        if (this.FindControl<ItemsControl>("Grid") is { } grid)
            grid.ContainerPrepared += OnTilePrepared;
    }

    /// <summary>The first screenful of covers arrives as a wave; the rest of the catalogue waits below.</summary>
    /// <summary>Waits a frame and a bit so the window is on screen, then plays the entrance.</summary>
    private async void DelayedEntrance()
    {
        try
        {
            await Task.Delay(180);
            PlayEntrance();
        }
        catch (Exception)
        {
            // a dropped entrance must never take the page with it
        }
    }

    private void PlayEntrance()
    {
        if (this.FindControl<ItemsControl>("Grid") is not { } grid)
            return;
        var index = 0;
        foreach (var tile in grid.GetRealizedContainers())
        {
            if (index >= Staggered)
                break;
            _ = AnimateTileAsync(tile, index * StaggerMs);
            index++;
        }
    }

    private static async Task AnimateTileAsync(Control tile, double delayMs)
    {
        try
        {
            tile.Opacity = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs));
            await TileIn.RunAsync(tile);
        }
        catch (Exception)
        {
            // a cover may not animate, but it must never stay invisible
        }
        finally
        {
            tile.Opacity = 1;
        }
    }

    private async void OnTilePrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (!_shown)
            return;
        var tile = e.Container;
        try
        {
            if (e.Index >= Staggered)
                return;
            tile.Opacity = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(e.Index * StaggerMs));
            await TileIn.RunAsync(tile);
        }
        catch (Exception)
        {
            // same rule as above
        }
        finally
        {
            tile.Opacity = 1;
        }
    }
}
