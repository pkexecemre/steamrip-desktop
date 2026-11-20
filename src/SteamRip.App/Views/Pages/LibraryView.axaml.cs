using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace SteamRip.App.Views.Pages;

public partial class LibraryView : UserControl
{
    /// <summary>Rows arrive one after another when the page is shown; the first dozen, then no delay.</summary>
    private const int Staggered = 12;

    private const double StaggerMs = 45;

    private static readonly Animation RowIn = new()
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

    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RuleSweep.Play(Rule);
            if (_shown)
                return;
            _shown = true;
            // The rows are laid out before the window is on screen; play the entrance now, on what
            // is actually realized, so it is visible instead of happening off-screen.
            // The window is mapped a frame or two after layout: without this the wave plays to an empty
            // screen and the user only sees its end state.
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(180);
                PlayEntrance();
            });
        };
        if (this.FindControl<ListBox>("Shelf") is { } shelf)
            shelf.ContainerPrepared += OnRowPrepared;
    }

    private void PlayEntrance()
    {
        if (this.FindControl<ListBox>("Shelf") is not { } shelf)
            return;
        var index = 0;
        foreach (var row in shelf.GetRealizedContainers())
        {
            if (index >= Staggered)
                break;
            _ = AnimateRowAsync(row, index * StaggerMs);
            index++;
        }
    }

    private static async Task AnimateRowAsync(Control row, double delayMs)
    {
        try
        {
            row.Opacity = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs));
            await RowIn.RunAsync(row);
        }
        catch (Exception)
        {
            // an entrance is never worth a missing row
        }
        finally
        {
            row.Opacity = 1;
        }
    }

    private async void OnRowPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (!_shown)
            return;
        var row = e.Container;
        try
        {
            if (e.Index >= Staggered)
            {
                return;
            }
            row.Opacity = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(e.Index * StaggerMs));
            await RowIn.RunAsync(row);
        }
        catch (Exception)
        {
            // an entrance is never worth a missing row
        }
        finally
        {
            row.Opacity = 1;
        }
    }
}
