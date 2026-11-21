using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;
using SteamRip.Core.Updates;

namespace SteamRip.App.ViewModels;

public partial class BrowseViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly bool _ready;

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>Nothing to show: either no catalog yet or a filter that matches nothing.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Everything the database holds, so the empty state can say how big the catalog is.</summary>
    [ObservableProperty]
    public partial int Total { get; set; }

    /// <summary>False before the first sync: an empty list then means "not yet", not "no match".</summary>
    public bool HasCatalog => Total > 0;

    public ObservableCollection<BrowseItem> Items { get; } = [];

    public IReadOnlyList<SortOption> Sorts { get; } =
    [
        new(PostSort.Updated, "Recently updated", "most recently updated"),
        new(PostSort.Popular, "Most popular", "most popular"),
        new(PostSort.Added, "Recently added", "most recently added"),
        new(PostSort.Name, "Name A–Z", "first by name"),
        new(PostSort.SizeDesc, "Largest first", "largest"),
        new(PostSort.SizeAsc, "Smallest first", "smallest"),
    ];

    [ObservableProperty]
    public partial SortOption Sort { get; set; }

    /// <summary>"All genres" (Id 0) followed by the site's categories, largest first.</summary>
    public ObservableCollection<Category> Genres { get; } = [];

    [ObservableProperty]
    // Nullable on purpose: the ComboBox writes null back while Genres is being refilled, and treating
    // that as "all genres" is what keeps the page alive instead of throwing on the next refresh.
    public partial Category? Genre { get; set; }

    private static readonly Category AllGenres = new(0, "All genres", 0);
    private const int Limit = 150;

    public BrowseViewModel(AppServices s)
    {
        _s = s;
        Sort = Sorts[0];
        Genre = AllGenres;
        RefreshGenres();
        _ready = true;
        Refresh();
    }

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnSortChanged(SortOption value)
    {
        if (_ready)
            Refresh();
    }

    partial void OnGenreChanged(Category? value)
    {
        if (_ready)
            Refresh();
    }

    /// <summary>Re-reads the category list after a sync; keeps the current choice when it still exists.</summary>
    public void RefreshGenres()
    {
        var current = Genre?.Id ?? 0;
        var fresh = _s.Db.GetCategories().Where(c => c.Name != "Uncategorized").ToList();
        if (
            Genres.Count == fresh.Count + 1
            && fresh.Select(c => c.Id).SequenceEqual(Genres.Skip(1).Select(c => c.Id))
        )
            return;
        Genres.Clear();
        Genres.Add(AllGenres);
        foreach (var c in fresh)
            Genres.Add(c);
        Genre = Genres.FirstOrDefault(c => c.Id == current) ?? AllGenres;
    }

    public void Refresh()
    {
        // Genre can be nulled by the ComboBox while Genres is being refilled; treating that as "all"
        // keeps the page usable instead of throwing out of every later refresh.
        var posts = _s.Db.SearchPosts(Query, Sort.Sort, Genre?.Id is > 0 ? Genre.Id : null, Limit);
        var installed = _s.Db.GetInstalls().ToDictionary(g => g.PostId);
        var jobs = _s.Updates.Jobs.ToDictionary(j => j.Post.Id);
        Items.Clear();
        foreach (var p in posts)
            Items.Add(
                new BrowseItem(
                    this,
                    p,
                    installed.ContainsKey(p.Id),
                    _s.Resolvers.Supported,
                    _s.Covers,
                    jobs.GetValueOrDefault(p.Id)
                )
            );
        var total = _s.Db.PostCount();
        Total = total;
        var genreId = Genre?.Id ?? 0;
        var filtered = !string.IsNullOrWhiteSpace(Query) || genreId != 0;
        var genreNote =
            Genres.Count == 1 && _s.Updates.Syncing ? " · genres appear when this sync finishes"
            : genreId != 0 && !_s.Db.HasPostCategories()
                ? " · genres are being filled in by the running sync"
            : Sort.Sort == PostSort.Popular && _s.Db.RankedPostCount() == 0
                ? " · popularity arrives with the next sync"
            : "";
        Summary =
            total == 0
                ? "No catalog yet — the first sync runs a few seconds after start, or press Sync now."
            : !filtered ? $"{total:N0} games · showing the {Items.Count} {Sort.Summary}{genreNote}"
            : Items.Count >= Limit
                ? $"Showing the {Limit} {Sort.Summary} matches of {total:N0}{genreNote}"
            : $"{Items.Count} match{(Items.Count == 1 ? "" : "es")} of {total:N0}{genreNote}";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCatalog));
    }

    /// <summary>The shell's toast line; the window subscribes so a click reports back.</summary>
    public event Action<string>? Notified;

    public void Install(BrowseItem item)
    {
        if (item.Installed)
        {
            Notified?.Invoke($"{item.Name} is already in your library");
            return;
        }
        _s.Updates.Enqueue(item.Post, isUpdate: false);
        _s.Log.LogInformation("User queued {Name} from the catalogue", item.Name);
        Notified?.Invoke($"Queued {item.Name} — see Queue");
        Refresh();
    }

    /// <summary>The empty state's one action: back to the whole catalog.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        Query = "";
        Sort = Sorts[0];
        Genre = Genres.Count > 0 ? Genres[0] : AllGenres;
        Refresh();
    }

    [RelayCommand]
    private async Task Sync() => await _s.Updates.SyncCatalogAsync();

    [RelayCommand]
    private async Task FullSync() => await _s.Updates.SyncCatalogAsync(full: true);
}

/// <summary>One entry of the sort dropdown; <see cref="Summary"/> completes "showing the 150 …".</summary>
public sealed record SortOption(PostSort Sort, string Label, string Summary);

public sealed partial class BrowseItem(
    BrowseViewModel owner,
    CatalogPost post,
    bool installed,
    IReadOnlyCollection<Hoster> supported,
    Services.CoverCache covers,
    Job? job
) : ObservableObject
{
    private Bitmap? _cover;
    private bool _coverRequested;

    /// <summary>Fetched on first read, i.e. when the card is actually rendered.</summary>
    public Bitmap? Cover
    {
        get
        {
            if (_coverRequested)
                return _cover;
            _coverRequested = true;
            _ = LoadCoverAsync();
            return _cover;
        }
    }
    public string Initial => post.Name.Length > 0 ? post.Name[..1].ToUpperInvariant() : "?";

    private async Task LoadCoverAsync()
    {
        var bmp = await covers.GetAsync(post.SteamAppId, post.Name);
        if (bmp is null)
            return;
        _cover = bmp;
        OnPropertyChanged(nameof(Cover));
    }

    public CatalogPost Post => post;
    public string Name => post.Name;
    public string Version => UpdateDetector.Normalize(post.VersionKey) ?? "—";
    public string Size => post.SizeText ?? "";
    public string Updated => MainViewModel.Humanize(post.ModifiedUtc);
    public string Mirrors =>
        string.Join(
            ", ",
            post.Mirrors.Select(m => m.Host)
                .Distinct()
                .Select(h => h == Hoster.Unknown ? "other" : h.ToString())
        );
    public bool Installed => installed;
    public bool CanInstall => !installed && post.Mirrors.Any(m => supported.Contains(m.Host));
    public bool Unsupported => !installed && !CanInstall;

    /// <summary>A job for this game is queued or running: the card says so instead of looking idle.</summary>
    public bool Queued =>
        job?.Stage
            is JobStage.Queued
                or JobStage.Resolving
                or JobStage.Downloading
                or JobStage.Installing;

    public string QueueStatus => job?.Status ?? "";
    public double? JobFraction => job?.Fraction;
    public bool JobIndeterminate => Queued && job?.Fraction is null;

    /// <summary>What the card's one button says: the state first, the action last.</summary>
    public string ActionLabel =>
        Installed ? "in library"
        : Queued ? "queued"
        : CanInstall ? "Install"
        : "no mirror";

    /// <summary>The whole card is the button: install when possible, the site page otherwise.</summary>
    [RelayCommand]
    private void Activate()
    {
        if (CanInstall)
            owner.Install(this);
        else
            OpenPage();
    }

    [RelayCommand]
    private void Install() => owner.Install(this);

    [RelayCommand]
    private void OpenPage() => LibraryViewModel.OpenPath(post.Url);
}
