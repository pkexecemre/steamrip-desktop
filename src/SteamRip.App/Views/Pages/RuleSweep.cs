using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;

namespace SteamRip.App.Views.Pages;

/// <summary>
/// The gradient rule under a page title grows in from the left every time the page is shown.
/// One sweeping element per page, nothing else moves: the page itself already slides up.
/// </summary>
public static class RuleSweep
{
    private static readonly Animation Grow = new()
    {
        Duration = TimeSpan.FromMilliseconds(320),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0d),
                Setters =
                {
                    new Setter(Layoutable.WidthProperty, 0d),
                    new Setter(Visual.OpacityProperty, 0d),
                },
            },
            new KeyFrame
            {
                Cue = new Cue(1d),
                Setters =
                {
                    new Setter(Layoutable.WidthProperty, 52d),
                    new Setter(Visual.OpacityProperty, 1d),
                },
            },
        },
    };

    public static void Play(Border? rule)
    {
        if (rule is null)
            return;
        _ = GrowAsync(rule);
    }

    private static async Task GrowAsync(Border rule)
    {
        try
        {
            await Grow.RunAsync(rule);
        }
        catch (Exception)
        {
            // a rule is never worth a broken page
        }
        finally
        {
            rule.Width = 52;
            rule.Opacity = 1;
        }
    }
}
