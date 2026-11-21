using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Install;
using SteamRip.Core.Launch;
using SteamRip.Core.Platform;
using SteamRip.Core.Updates;

namespace SteamRip.App.ViewModels;

public partial class LibraryViewModel(AppServices s) : ObservableObject
{
    public ObservableCollection<LibraryItem> Items { get; } = [];
    internal Services.CoverCache Covers => s.Covers;
    public IReadOnlyList<ProtonBuild> Protons { get; } =
    [new("App default", ""), .. ProtonLocator.Managed, .. ProtonLocator.Find()];
    internal LaunchOptions DefaultLaunch =>
        s.Settings.DefaultLaunch.MergeOver(LaunchOptions.AppDefaults);
    internal HostCapabilities? Capabilities => s.CapabilitiesNow;
    internal string DefaultProtonPath => ProtonLocator.Effective(s.Settings.DefaultProtonPath);
    internal string LibraryRoot => s.Settings.LibraryRoot;
    public static bool IsLinux { get; } = OperatingSystem.IsLinux();

    [ObservableProperty]
    public partial int UpdatesAvailable { get; set; }

    /// <summary>Only shown when there is at least one; reads as a count, not a sentence.</summary>
    partial void OnUpdatesAvailableChanged(int value) => OnPropertyChanged(nameof(UpdatesLabel));

    public string UpdatesLabel =>
        UpdatesAvailable == 1 ? "1 update" : $"{UpdatesAvailable} updates";

    /// <summary>One quiet line under the title: how much is installed and how long it has been played.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string? LastError { get; set; }

    /// <summary>Raised when a card asks for its launch options; the shell shows them in the side sheet.</summary>
    public event Action<LibraryItem>? OptionsRequested;

    internal void RequestOptions(LibraryItem item) => OptionsRequested?.Invoke(item);

    /// <summary>Why this game's last launch failed during startup, if it did; null once it is launched again.</summary>
    internal LaunchHint? LaunchHint(long postId) => s.Launcher.HintFor(postId);

    public void Refresh()
    {
        var installs = s.Db.GetInstalls();
        var jobs = s.Updates.Jobs;
        var updates = 0;
        var seen = new HashSet<long>();
        foreach (var g in installs)
        {
            seen.Add(g.PostId);
            var post = s.Db.GetPost(g.PostId);
            var job = jobs.FirstOrDefault(j =>
                j.Post.Id == g.PostId && j.Stage is not (JobStage.Done or JobStage.Cancelled)
            );
            var hasUpdate =
                post is not null
                && g.State == InstallState.Installed
                && UpdateDetector.IsUpdate(g, post);
            if (hasUpdate)
                updates++;
            var running = s.Launcher.IsRunning(g.PostId);
            var existing = Items.FirstOrDefault(i => i.Game.PostId == g.PostId);
            if (existing is null)
                Items.Add(new LibraryItem(this, g, post, job, hasUpdate, running));
            else
                existing.Update(g, post, job, hasUpdate, running);
        }
        foreach (var gone in Items.Where(i => !seen.Contains(i.Game.PostId)).ToList())
            Items.Remove(gone);
        UpdatesAvailable = updates;
        IsEmpty = Items.Count == 0;
        Summary =
            Items.Count == 0
                ? ""
                : $"{Items.Count} game{(Items.Count == 1 ? "" : "s")}, "
                    + $"{installs.Sum(g => g.PlaytimeSeconds) / 3600.0:0.#} h played"
                    + (updates > 0 ? $", {updates} waiting on you" : "");
    }

    public async Task PlayAsync(LibraryItem item)
    {
        LastError = null;
        try
        {
            await s.Updates.PlayAsync(item.Game.PostId);
        }
        catch (Exception e)
        {
            LastError = $"{item.Name}: {e.Message}";
            s.Log.LogWarning("Play {Name} failed: {Error}", item.Name, e.Message);
        }
        Refresh();
    }

    /// <summary>Launches one specific executable of an installed bundle and remembers it.</summary>
    public async Task PlayExeAsync(LibraryItem item, string relativePath)
    {
        LastError = null;
        try
        {
            await s.Updates.PlayAsync(item.Game.PostId, relativePath);
        }
        catch (Exception e)
        {
            LastError = $"{item.Name}: {e.Message}";
            s.Log.LogWarning(
                "Play {Name} ({Exe}) failed: {Error}",
                item.Name,
                relativePath,
                e.Message
            );
        }
        Refresh();
    }

    public void Stop(LibraryItem item)
    {
        s.Launcher.Stop(item.Game.PostId);
        Refresh();
    }

    public void Update(LibraryItem item)
    {
        if (item.Post is not null)
            s.Updates.Enqueue(item.Post, isUpdate: true);
        Refresh();
    }

    public void ToggleAuto(LibraryItem item)
    {
        s.Db.UpsertInstall(item.Game with { AutoUpdate = !item.Game.AutoUpdate });
        Refresh();
    }

    public async Task ToggleShortcut(LibraryItem item, ShortcutPlace place)
    {
        await s.Updates.SetShortcutAsync(
            item.Game.PostId,
            place,
            !s.Updates.HasShortcut(item.Game.PostId, place)
        );
        item.RefreshShortcuts();
    }

    public bool HasShortcut(LibraryItem item, ShortcutPlace place) =>
        s.Updates.HasShortcut(item.Game.PostId, place);

    /// <summary>Queues the game again after a failure; the row's own message tells the user to do this.</summary>
    public void Retry(LibraryItem item)
    {
        if (item.Post is not { } post)
            return;
        s.Updates.Enqueue(post, isUpdate: item.Game.State == InstallState.Installed);
        s.Log.LogInformation("User retried {Name} from the library", item.Name);
    }

    /// <summary>Stops the download or install this game has queued or running.</summary>
    public void Cancel(LibraryItem item)
    {
        if (item.RunningJob is not { } job)
            return;
        s.Updates.Cancel(job);
        s.Log.LogInformation("User cancelled {Name} from the library", item.Name);
    }

    public void Uninstall(LibraryItem item, bool deleteFiles)
    {
        s.Updates.Uninstall(item.Game.PostId, deleteFiles);
        Refresh();
    }

    public void OpenFolder(LibraryItem item) => OpenPath(item.ResolvedPath);

    /// <summary>Where the game's prefix lives: the new out-of-folder root, or a not-yet-migrated legacy one.</summary>
    private string ExistingPrefix(LibraryItem item)
    {
        var dir = PrefixManager.Dir(s.Updates.PrefixRoot, item.Game.Slug);
        if (Directory.Exists(dir))
            return dir;
        return PrefixManager.LegacyDir(item.ResolvedPath) ?? dir;
    }

    public void OpenPrefix(LibraryItem item)
    {
        var p = ExistingPrefix(item);
        if (Directory.Exists(p))
            OpenPath(p);
        else
            LastError = $"{item.Name}: no prefix yet — it is created on first launch.";
    }

    /// <summary>Moves the prefix aside (never deletes it) so the next launch bootstraps a clean one.</summary>
    public void ResetPrefix(LibraryItem item)
    {
        LastError = null;
        if (s.Launcher.IsRunning(item.Game.PostId))
        {
            LastError = $"{item.Name}: stop the game before resetting its prefix.";
            return;
        }
        var p = ExistingPrefix(item);
        if (!Directory.Exists(p))
        {
            LastError = $"{item.Name}: no prefix yet — nothing to reset.";
            return;
        }
        try
        {
            var aside = PrefixManager.Reset(p);
            s.Log.LogInformation(
                "{Name}: prefix reset; old prefix moved to {Aside}",
                item.Name,
                aside ?? "n/a"
            );
            _ = s.Notifier.NotifyAsync(
                item.Name,
                aside is null
                    ? "No prefix to reset."
                    : "Prefix moved aside; the next launch builds a clean one."
            );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = $"{item.Name}: could not reset the prefix: {e.Message}";
            s.Log.LogWarning("{Name}: prefix reset failed: {Error}", item.Name, e.Message);
        }
    }

    /// <summary>What the prefix is right now (size, Proton, launches), in the log and as a notification.</summary>
    public void PrefixInfo(LibraryItem item)
    {
        var status = PrefixManager.Inspect(ExistingPrefix(item));
        var line =
            $"{status.Summary} — {status.Path}"
            + (
                status.Record?.LastUsedUtc is { } used
                    ? $" (last used {used.ToLocalTime():yyyy-MM-dd HH:mm})"
                    : ""
            );
        s.Log.LogInformation("{Name}: prefix {Line}", item.Name, line);
        _ = s.Notifier.NotifyAsync(item.Name, line);
    }

    public void OpenPage(LibraryItem item)
    {
        if (item.Post is not null)
            OpenPath(item.Post.Url);
    }

    public void OpenWinetricks(LibraryItem item)
    {
        LastError = null;
        try
        {
            s.Launcher.OpenWinetricks(item.ResolvedPath, ProtonFor(item), ExistingPrefix(item));
        }
        catch (Exception e)
        {
            LastError = $"{item.Name}: {e.Message}";
            s.Log.LogWarning("Winetricks {Name} failed: {Error}", item.Name, e.Message);
            _ = s.Notifier.NotifyAsync(item.Name, e.Message);
        }
    }

    public async Task RunExeInPrefixAsync(LibraryItem item)
    {
        LastError = null;
        try
        {
            var window = (
                Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            )?.MainWindow;
            if (window is null)
                return;
            var start = RedistInstaller.FindCommonRedist(item.ResolvedPath) ?? item.ResolvedPath;
            IStorageFolder? folder = null;
            if (Directory.Exists(start))
                folder = await window.StorageProvider.TryGetFolderFromPathAsync(start);
            var files = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = $"Run in {item.Name} prefix",
                    AllowMultiple = false,
                    SuggestedStartLocation = folder,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Windows installers")
                        {
                            Patterns = ["*.exe", "*.msi"],
                        },
                    ],
                }
            );
            if (files.Count == 0)
                return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                return;
            s.Log.LogInformation("{Name}: run in prefix {Exe}", item.Name, path);
            await s.Notifier.NotifyAsync(item.Name, $"Running {Path.GetFileName(path)}…");
            var prefix = s.Updates.PrefixFor(item.Game);
            int code;
            using (PrefixLease.Acquire(prefix))
                code = await s.Launcher.RunInPrefixAsync(
                    item.ResolvedPath,
                    ProtonFor(item),
                    path,
                    prefixDir: prefix
                );
            s.Log.LogInformation(
                "{Name}: {Exe} exited {Code}",
                item.Name,
                Path.GetFileName(path),
                code
            );
            if (code != 0)
            {
                LastError = $"{item.Name}: {Path.GetFileName(path)} exited {code}";
                await s.Notifier.NotifyAsync(item.Name, $"{Path.GetFileName(path)} exited {code}");
            }
        }
        catch (Exception e)
        {
            LastError = $"{item.Name}: {e.Message}";
            s.Log.LogWarning("Run exe {Name} failed: {Error}", item.Name, e.Message);
            await s.Notifier.NotifyAsync(item.Name, e.Message);
        }
    }

    private string ProtonFor(LibraryItem item) =>
        ProtonLocator.Effective(item.Game.ProtonPath ?? s.Settings.DefaultProtonPath);

    public void SetExe(LibraryItem item, string rel)
    {
        s.Db.UpsertInstall(item.Game with { ExeRelative = rel });
        s.Log.LogInformation("{Name}: executable set to {Exe}", item.Name, rel);
        Refresh();
    }

    public void SetInstallPath(LibraryItem item, string? path)
    {
        var custom = string.IsNullOrWhiteSpace(path) ? "" : path.Trim();
        if (custom == item.Game.InstallPath)
            return;
        s.Db.UpsertInstall(item.Game with { InstallPath = custom });
        s.Log.LogInformation(
            "{Name}: install folder set to {Path}",
            item.Name,
            string.IsNullOrEmpty(custom)
                ? $"default ({Path.Combine(LibraryRoot, item.Game.Slug)})"
                : custom
        );
        Refresh();
    }

    public void SetProton(LibraryItem item, ProtonBuild? p)
    {
        s.Db.UpsertInstall(
            item.Game with
            {
                ProtonPath = string.IsNullOrEmpty(p?.Path) ? null : p.Path,
            }
        );
        s.Log.LogInformation("{Name}: Proton set to {Proton}", item.Name, p?.Name ?? "default");
        Refresh();
    }

    public void SetArgs(LibraryItem item, string? args)
    {
        s.Db.UpsertInstall(
            item.Game with
            {
                LaunchArgs = string.IsNullOrWhiteSpace(args) ? null : args.Trim(),
            }
        );
        Refresh();
    }

    public void SetLaunch(LibraryItem item, LaunchOptions o)
    {
        var stored = o.IsEmpty ? null : o;
        if (stored == item.Game.Launch)
            return;
        s.Db.UpsertInstall(item.Game with { Launch = stored });
        s.Log.LogInformation(
            "{Name}: launch tweaks set to {Launch}",
            item.Name,
            stored?.ToString() ?? "inherit"
        );
        Refresh();
    }

    public static void OpenPath(string target)
    {
        try
        {
            // UseShellExecute cannot set Environment on Unix; spawn the opener ourselves and drop the
            // throwaway XDG_* from make run-dev / F5 so Firefox/Dolphin use the real user profile.
            if (OperatingSystem.IsLinux())
            {
                Process.Start(
                    WithoutThrowawayXdg(
                        new ProcessStartInfo("xdg-open", target) { UseShellExecute = false }
                    )
                );
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(
                    WithoutThrowawayXdg(
                        new ProcessStartInfo("open", target) { UseShellExecute = false }
                    )
                );
                return;
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            if (OperatingSystem.IsLinux())
                Process.Start(
                    WithoutThrowawayXdg(
                        new ProcessStartInfo("xdg-open", target) { UseShellExecute = false }
                    )
                );
        }
    }

    private static ProcessStartInfo WithoutThrowawayXdg(ProcessStartInfo psi)
    {
        psi.Environment.Remove("XDG_CONFIG_HOME");
        psi.Environment.Remove("XDG_DATA_HOME");
        return psi;
    }
}

public sealed partial class LibraryItem : ObservableObject
{
    private readonly LibraryViewModel _owner;
    private Job? _job;

    public LibraryItem(
        LibraryViewModel owner,
        InstalledGame game,
        CatalogPost? post,
        Job? job,
        bool hasUpdate,
        bool running
    )
    {
        _owner = owner;
        Game = game;
        Post = post;
        _job = job;
        HasUpdate = hasUpdate;
        IsRunning = running;
        LaunchArgs = game.LaunchArgs ?? "";
        CustomInstallPath = game.InstallPath;
        Launch = new LaunchOptionsEditor(
            () => _owner.DefaultLaunch,
            () => _owner.Capabilities,
            () => Game.ProtonPath ?? _owner.DefaultProtonPath
        );
        Launch.Load(game.Launch);
        Launch.Changed += o => _owner.SetLaunch(this, o);
        RefreshExes();
    }

    /// <summary>Per-game tweaks; null fields inherit the Settings defaults.</summary>
    public LaunchOptionsEditor Launch { get; }

    public void Update(
        InstalledGame game,
        CatalogPost? post,
        Job? job,
        bool hasUpdate,
        bool running
    )
    {
        var exeChanged =
            game.ExeRelative != Game.ExeRelative
            || game.InstallPath != Game.InstallPath
            || game.InstalledVersion != Game.InstalledVersion;
        Game = game;
        Post = post;
        _job = job;
        HasUpdate = hasUpdate;
        IsRunning = running;
        if (exeChanged)
            RefreshExes();
        if (CustomInstallPath != game.InstallPath)
            CustomInstallPath = game.InstallPath;
        if (LaunchArgs != (game.LaunchArgs ?? ""))
            LaunchArgs = game.LaunchArgs ?? "";
        if (
            game.Launch != Launch.ToOptions()
            && !(game.Launch is null && Launch.ToOptions().IsEmpty)
        )
            Launch.Load(game.Launch);
        else
            Launch.Refresh();
        OnPropertyChanged(string.Empty);
    }

    private void RefreshExes()
    {
        // Called from Refresh() on the dispatcher right after every install/update. Walking a AAA game
        // folder (depth 4, tens of thousands of entries) there froze the window exactly when the card should
        // have turned into "Installed", so the walk runs off the UI thread and the list lands back on it.
        var root = ResolvedPath;
        var name = Game.Name;
        var current = Game.ExeRelative;
        _ = Task.Run(() => ExeFinder.Candidates(root, name).Select(c => c.RelativePath).ToList())
            .ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                        return;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        ApplyExes(task.Result, current)
                    );
                },
                TaskScheduler.Default
            );
    }

    private void ApplyExes(IReadOnlyList<string> found, string? current)
    {
        Exes.Clear();
        ExeChoices.Clear();
        foreach (var relative in found)
        {
            Exes.Add(relative);
            ExeChoices.Add(new ExeChoice(this, relative));
        }
        OnPropertyChanged(nameof(HasExeChoices));
        if (current is { } cur && !Exes.Contains(cur))
            Exes.Insert(0, cur);
        _selectedExe = Game.ExeRelative;
        _selectedProton =
            _owner.Protons.FirstOrDefault(p => p.Path == (Game.ProtonPath ?? ""))
            ?? _owner.Protons[0];
        OnPropertyChanged(nameof(SelectedExe));
        OnPropertyChanged(nameof(SelectedProton));
    }

    /// <summary>Every executable worth launching: a trilogy packs three games into one install.</summary>
    public ObservableCollection<ExeChoice> ExeChoices { get; } = [];

    /// <summary>
    /// A bundle looks like this: several executables, each in its own game folder. One folder with two
    /// exes is a game plus its crash handler, and does not deserve a menu.
    /// </summary>
    public bool HasExeChoices =>
        Game.State == InstallState.Installed
        && ExeChoices.Count > 1
        && ExeChoices
            .Select(c =>
                c.RelativePath.Contains(Path.DirectorySeparatorChar)
                    ? c.RelativePath[..c.RelativePath.IndexOf(Path.DirectorySeparatorChar)]
                    : ""
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;

    internal Task PlayExeAsync(string relative) => _owner.PlayExeAsync(this, relative);

    public InstalledGame Game { get; private set; }
    public CatalogPost? Post { get; private set; }

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
    public string Initial => Game.Name.Length > 0 ? Game.Name[..1].ToUpperInvariant() : "?";

    private async Task LoadCoverAsync()
    {
        var bmp = await _owner.Covers.GetAsync(Post?.SteamAppId, Post?.Name ?? Game.Name);
        if (bmp is null)
            return;
        _cover = bmp;
        OnPropertyChanged(nameof(Cover));
    }

    public string Name => Game.Name;
    public bool IsLinux => LibraryViewModel.IsLinux;
    public string ResolvedPath => Game.ResolvePath(_owner.LibraryRoot);
    public string DefaultInstallPath => Path.Combine(_owner.LibraryRoot, Game.Slug);
    public string InstalledVersion => Game.InstalledVersion ?? "—";
    public string SiteVersion => UpdateDetector.Normalize(Post?.VersionKey) ?? "—";
    public bool HasUpdate { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>The job this game is running, if any: the row shows its status and can stop it.</summary>
    public Job? RunningJob => _job;

    /// <summary>How far the running job is, for the thin bar under the row; null while it is indeterminate.</summary>
    public double? JobFraction => _job?.Fraction;

    /// <summary>True while the job has no measurable progress yet (resolving, or aria2 warming up).</summary>
    public bool JobIndeterminate => _job is not null && IsBusy && _job.Fraction is null;

    public bool IsBusy =>
        _job?.Stage
            is JobStage.Queued
                or JobStage.Resolving
                or JobStage.Downloading
                or JobStage.Installing;
    public bool CanPlay => !IsRunning && !IsBusy && Game.State == InstallState.Installed;
    public bool IsIdle => !IsBusy;
    public bool NeedsAttention => Game.State is InstallState.Failed or InstallState.NeedsAttention;
    public string AutoLabel => Game.AutoUpdate ? "Auto-update on" : "Auto-update off";
    public string DesktopShortcutLabel =>
        _owner.HasShortcut(this, ShortcutPlace.Desktop)
            ? "Remove desktop shortcut"
            : "Add desktop shortcut";
    public string MenuShortcutLabel =>
        _owner.HasShortcut(this, ShortcutPlace.Menu) ? "Remove from app menu" : "Add to app menu";

    internal void RefreshShortcuts()
    {
        OnPropertyChanged(nameof(DesktopShortcutLabel));
        OnPropertyChanged(nameof(MenuShortcutLabel));
    }

    public string Playtime =>
        Game.PlaytimeSeconds switch
        {
            0 => "",
            < 3600 => $"{Game.PlaytimeSeconds / 60} min played",
            var s => $"{s / 3600.0:0.0} h played",
        };
    public string StatusLine =>
        IsRunning ? "Running"
        : _job is not null && IsBusy ? _job.Status
        : NeedsAttention ? Game.LastError?.Split('\n')[0] ?? "Needs attention"
        // A launch that died during startup explains itself here until the next Play; the log has the paragraph.
        : _owner.LaunchHint(Game.PostId) is { } hint ? hint.Card
        : HasUpdate
            ? "Update available"
                + (Game.AutoUpdate ? " · installs automatically" : " · auto-update off")
        : Game.State == InstallState.Installed
            ? Game.LastPlayedUtc is { } lp ? $"Last played {MainViewModel.Humanize(lp)}"
                : $"Installed {MainViewModel.Humanize(Game.InstalledUtc)} · never played"
        : Game.State.ToString();
    public string SizeText => Post?.SizeText ?? "";
    public string ExeLine => Game.ExeRelative ?? "No executable chosen";

    public ObservableCollection<string> Exes { get; } = [];
    public IReadOnlyList<ProtonBuild> Protons => _owner.Protons;

    private string? _selectedExe;
    public string? SelectedExe
    {
        get => _selectedExe;
        set
        {
            if (value is null || value == _selectedExe)
                return;
            _selectedExe = value;
            OnPropertyChanged();
            _owner.SetExe(this, value);
        }
    }

    private ProtonBuild? _selectedProton;
    public ProtonBuild? SelectedProton
    {
        get => _selectedProton;
        set
        {
            if (value is null || value == _selectedProton)
                return;
            _selectedProton = value;
            OnPropertyChanged();
            _owner.SetProton(this, value);
        }
    }

    [ObservableProperty]
    public partial string LaunchArgs { get; set; }

    partial void OnLaunchArgsChanged(string value)
    {
        if (value != (Game.LaunchArgs ?? ""))
            _owner.SetArgs(this, value);
    }

    /// <summary>Empty = under the Settings library folder; otherwise an absolute custom install directory.</summary>
    [ObservableProperty]
    public partial string CustomInstallPath { get; set; }

    partial void OnCustomInstallPathChanged(string value)
    {
        if (value != Game.InstallPath)
            _owner.SetInstallPath(this, value);
    }

    [RelayCommand]
    private Task Play() => _owner.PlayAsync(this);

    [RelayCommand]
    private Task PlayExe(string? relative) => _owner.PlayExeAsync(this, relative ?? "");

    [RelayCommand]
    private void Stop() => _owner.Stop(this);

    [RelayCommand]
    private void Options() => _owner.RequestOptions(this);

    [RelayCommand]
    private void UpdateNow() => _owner.Update(this);

    [RelayCommand]
    private void ToggleAuto() => _owner.ToggleAuto(this);

    [RelayCommand]
    private Task ToggleDesktopShortcut() => _owner.ToggleShortcut(this, ShortcutPlace.Desktop);

    [RelayCommand]
    private Task ToggleMenuShortcut() => _owner.ToggleShortcut(this, ShortcutPlace.Menu);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenFolder(this);

    [RelayCommand]
    private void OpenPrefix() => _owner.OpenPrefix(this);

    [RelayCommand]
    private void PrefixInfo() => _owner.PrefixInfo(this);

    [RelayCommand]
    private void ResetPrefix() => _owner.ResetPrefix(this);

    [RelayCommand]
    private void OpenPage() => _owner.OpenPage(this);

    [RelayCommand]
    private void Winetricks() => _owner.OpenWinetricks(this);

    [RelayCommand]
    private Task RunExe() => _owner.RunExeInPrefixAsync(this);

    [RelayCommand]
    private void Cancel() => _owner.Cancel(this);

    /// <summary>Needs a person: the last attempt stopped, so the row offers the retry directly.</summary>
    public bool CanRetry => NeedsAttention && Post is not null;

    [RelayCommand]
    private void Retry() => _owner.Retry(this);

    [RelayCommand]
    private void Forget() => _owner.Uninstall(this, deleteFiles: false);

    [RelayCommand]
    private void Delete() => _owner.Uninstall(this, deleteFiles: true);
}

/// <summary>One executable a bundle can launch, with the command that starts it.</summary>
public sealed partial class ExeChoice(LibraryItem owner, string relativePath) : ObservableObject
{
    public string RelativePath => relativePath;

    /// <summary>File name, and the folder when two candidates share one (the trilogy has no such luck).</summary>
    public string Label =>
        Path.GetFileNameWithoutExtension(relativePath) is { Length: > 0 } stem
            ? stem + "  ·  " + relativePath
            : relativePath;

    [RelayCommand]
    private Task Play() => owner.PlayExeAsync(relativePath);
}
