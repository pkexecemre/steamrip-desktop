using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;
using SteamRip.Core.Install;
using SteamRip.Core.Launch;
using SteamRip.Core.Platform;

namespace SteamRip.Core.Updates;

/// <summary>
/// The single background engine: periodic catalog sync, update detection for installed games, and a one-at-a-time job queue
/// (resolve → download → verify → install → record → notify). The UI only observes and enqueues.
/// </summary>
public sealed class UpdateService : IAsyncDisposable
{
    private readonly Database _db;
    private readonly Settings _settings;
    private readonly CatalogClient _catalog;
    private readonly ResolverChain _resolvers;
    private readonly Downloader _downloader;
    private readonly Installer _installer;
    private readonly INotifier _notifier;
    private readonly GameLauncher _launcher;
    private readonly SteamAppIdResolver? _steamApps;
    private readonly SteamGridDbClient? _steamGridDb;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<Job> _queue = new();
    private readonly ProtonDownloader? _protons;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _reschedule = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Job> _jobs = [];

    private Task? _worker,
        _scheduler;

    public UpdateService(
        Database db,
        Settings settings,
        CatalogClient catalog,
        ResolverChain resolvers,
        Downloader downloader,
        Installer installer,
        INotifier notifier,
        GameLauncher launcher,
        ILogger<UpdateService>? logger = null,
        ProtonDownloader? protons = null,
        SteamAppIdResolver? steamApps = null,
        SteamGridDbClient? steamGridDb = null
    )
    {
        _db = db;
        _steamApps = steamApps;
        _steamGridDb = steamGridDb;
        _protons = protons;
        _settings = settings;
        _catalog = catalog;
        _resolvers = resolvers;
        _downloader = downloader;
        _installer = installer;
        _notifier = notifier;
        _launcher = launcher;
        _log = logger ?? NullLogger<UpdateService>.Instance;
        _launcher.Exited += OnGameExited;
    }

    /// <summary>Once a day, re-read the site's /top-games/ ranking (first <see cref="TopGamesPages"/> pages). A failure here never fails the sync.</summary>
    private async Task RefreshPopularityAsync(CancellationToken ct)
    {
        const string key = "top_games_utc";
        if (
            _db.GetKv(key) is { } last
            && DateTime.TryParse(
                last,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var at
            )
            && DateTime.UtcNow - at < TimeSpan.FromHours(24)
            && _db.RankedPostCount() > 0
        )
            return;
        try
        {
            var ids = await _catalog.GetTopGamesAsync(TopGamesPages, ct);
            if (ids.Count == 0)
                return;
            _db.SetPopularity(ids);
            _db.SetKv(key, DateTime.UtcNow.ToString("o"));
            _log.LogInformation("Popularity ranking stored for {Count} posts", ids.Count);
            SyncChanged?.Invoke();
        }
        catch (Exception e)
            when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "Top games fetch failed: {Error}; keeping the previous ranking",
                e.Message
            );
        }
    }

    /// <summary>42 pages × 24 = the top ~1,000; about a minute with the politeness delay, once a day.</summary>
    private const int TopGamesPages = 42;

    // ---- play ----

    public async Task PlayAsync(long postId)
    {
        var g = _db.GetInstall(postId) ?? throw new LaunchException("Not installed.");
        if (g.State is not InstallState.Installed)
            throw new LaunchException($"{g.Name} is {g.State}; wait for it to finish.");
        var post = _db.GetPost(postId);
        var path = g.ResolvePath(_settings.LibraryRoot);
        var exe = g.ExeRelative;
        if (exe is null || !File.Exists(Path.Combine(path, exe)))
        {
            exe =
                ExeFinder.Best(path, g.Name)
                ?? throw new LaunchException($"No executable found in {path}.");
            _db.UpsertInstall(g with { ExeRelative = exe });
            _log.LogInformation("{Name}: auto-selected executable {Exe}", g.Name, exe);
        }

        var proton = ProtonLocator.Effective(g.ProtonPath ?? _settings.DefaultProtonPath);
        if (proton == ProtonLocator.DwProton)
        {
            if (_protons is null)
                throw new LaunchException("DW-Proton downloads are not available in this build.");
            proton = await _protons.EnsureDwProtonAsync(_cts.Token);
        }

        var steamAppId = _steamApps is null
            ? post?.SteamAppId
            : await _steamApps.ResolveAsync(post?.SteamAppId, post?.Name ?? g.Name, _cts.Token);
        var prefix = PrefixManager.Prepare(path, PrefixRoot, g.Slug, _log);
        await _launcher.LaunchAsync(
            new LaunchSpec(
                postId,
                g.Name,
                path,
                exe,
                proton,
                g.LaunchArgs,
                steamAppId,
                EffectiveLaunch(g),
                prefix,
                g.Slug
            )
        );
        _db.UpsertInstall((_db.GetInstall(postId) ?? g) with { LastPlayedUtc = DateTime.UtcNow });
        _db.LogEvent("play", g.Name, postId);
        JobsChanged?.Invoke();
    }

    /// <summary>Where this library's prefixes live: <c>&lt;library&gt;/.prefixes</c> unless the setting overrides it.</summary>
    public string PrefixRoot => PrefixManager.Root(_settings.LibraryRoot, _settings.PrefixRoot);

    /// <summary>The prefix a game uses today; a legacy in-folder one is moved out on first sight.</summary>
    public string PrefixFor(InstalledGame g) =>
        PrefixManager.Prepare(g.ResolvePath(_settings.LibraryRoot), PrefixRoot, g.Slug, _log);

    /// <summary>Game override → user defaults → app defaults, so nothing that matters is null.</summary>
    public LaunchOptions EffectiveLaunch(InstalledGame g) =>
        (g.Launch ?? new LaunchOptions())
            .MergeOver(_settings.DefaultLaunch)
            .MergeOver(LaunchOptions.AppDefaults);

    private void OnGameExited(long postId, TimeSpan played, int code)
    {
        var g = _db.GetInstall(postId);
        if (g is not null)
            _db.UpsertInstall(
                g with
                {
                    PlaytimeSeconds = g.PlaytimeSeconds + (long)played.TotalSeconds,
                }
            );
        JobsChanged?.Invoke();
    }

    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_jobs)
                return [.. _jobs];
        }
    }

    public Job? Current { get; private set; }
    public DateTime? LastSyncUtc { get; private set; }
    public string LastSyncStatus { get; private set; } = "Never synced";
    public bool Syncing { get; private set; }

    public event Action? JobsChanged;
    public event Action? SyncChanged;

    public void Start()
    {
        RecoverStaleInstalls();
        _log.LogInformation(
            "UpdateService starting: library {Library}, downloads {Downloads}, interval {Interval} min, auto-updates {Paused}, FlareSolverr {Solver}",
            _settings.LibraryRoot,
            _settings.DownloadsRoot,
            _settings.CheckIntervalMinutes,
            _settings.AutoUpdatesPaused ? "paused" : "on",
            _settings.FlareSolverrUrl
        );
        _worker ??= Task.Run(WorkerLoopAsync);
        _scheduler ??= Task.Run(SchedulerLoopAsync);
    }

    /// <summary>
    /// The job queue lives in memory, so a row left in Queued/Downloading/Installing is a leftover from a previous
    /// process: nothing is ever going to move it again, Play refuses it ("is Downloading; wait for it to finish") and
    /// auto-update skips it. Put those rows back to something true — Installed when the files are still there,
    /// NeedsAttention otherwise.
    /// </summary>
    private void RecoverStaleInstalls()
    {
        foreach (var g in _db.GetInstalls())
        {
            if (
                g.State
                is not (InstallState.Queued or InstallState.Downloading or InstallState.Installing)
            )
                continue;
            var path = g.ResolvePath(_settings.LibraryRoot);
            var hasGame =
                g.ExeRelative is { Length: > 0 } exe && File.Exists(Path.Combine(path, exe));
            var state = RecoveredState(hasGame);
            _db.SetInstallState(
                g.PostId,
                state,
                hasGame ? null : $"Interrupted while {g.State.ToString().ToLowerInvariant()}"
            );
            _db.LogEvent(
                "recover",
                $"{g.Name}: {g.State} with no job after restart → {state}",
                g.PostId
            );
            _log.LogWarning(
                "{Name}: state was {Was} with no job after a restart; marked {Now} (files {Files})",
                g.Name,
                g.State,
                state,
                hasGame ? "present" : "missing"
            );
        }
    }

    /// <summary>A stale in-flight row keeps the game if its executable is still on disk, else it needs a person.</summary>
    public static InstallState RecoveredState(bool hasGame) =>
        hasGame ? InstallState.Installed : InstallState.NeedsAttention;

    /// <summary>
    /// Which rows the hourly check may queue by itself. NeedsAttention means a person has to act (no working mirror,
    /// a torrent-only post with torrents off); Failed means the last attempt failed for a reason that may not go away
    /// (a permanently broken mirror, an unwritable folder). Re-queueing either every hour just repeats a multi-gigabyte
    /// download and the notification, so both wait for the Retry button instead.
    /// </summary>
    public static bool AutoQueues(InstallState state) =>
        state
            is not (
                InstallState.Downloading
                or InstallState.Installing
                or InstallState.Queued
                or InstallState.NeedsAttention
                or InstallState.Failed
            );

    // ---- catalog ----

    public async Task<int> SyncCatalogAsync(bool full = false, CancellationToken ct = default)
    {
        if (Syncing)
        {
            _log.LogDebug("Sync requested while already syncing; ignored");
            return 0;
        }

        Syncing = true;
        SyncChanged?.Invoke();
        try
        {
            // Until one full pass has finished, every sync is a full one (a 429 or crash mid-way must not leave holes in the catalog).
            var complete = _db.GetKv("catalog_complete") == "1";
            var since = full || !complete ? null : _db.NewestModified();
            if (since is null || _db.GetCategories().Count == 0)
            {
                _db.UpsertCategories(await _catalog.GetCategoriesAsync(ct));
                SyncChanged?.Invoke(); // the genre list can show up before the (long) full pass ends
            }

            var batch = new List<CatalogPost>();
            var newPosts = new List<CatalogPost>();
            var count = 0;
            await foreach (var p in _catalog.GetModifiedSinceAsync(since, ct))
            {
                if (_db.GetPost(p.Id) is null)
                    newPosts.Add(p);
                batch.Add(p);
                count++;
                if (batch.Count < 100)
                    continue;
                _db.UpsertPosts(batch);
                batch.Clear();
            }

            if (batch.Count > 0)
                _db.UpsertPosts(batch);
            if (since is null)
                _db.SetKv("catalog_complete", "1");
            LastSyncUtc = DateTime.UtcNow;
            LastSyncStatus = $"{count} changed post(s), {_db.PostCount()} total";
            _db.LogEvent("sync", LastSyncStatus);
            _log.LogInformation(
                "Catalog sync finished: {Status}; {New} new post(s)",
                LastSyncStatus,
                newPosts.Count
            );

            if (_settings.NotifyNewGames && since is not null && newPosts.Count > 0)
                await _notifier.NotifyAsync(
                    "New on SteamRIP",
                    string.Join("\n", newPosts.Take(5).Select(p => p.Name))
                        + (newPosts.Count > 5 ? $"\n…and {newPosts.Count - 5} more" : "")
                );

            CheckForUpdates();
            await RefreshPopularityAsync(ct);
            return count;
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own 60s timeout arrives as a TaskCanceledException; treating it as our own
            // shutdown used to stop the scheduler for the rest of the process.
            LastSyncStatus = "Sync failed: " + e.Message;
            _db.LogEvent("sync-error", e.Message);
            _log.LogError(e, "Catalog sync timed out");
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            LastSyncStatus = "Sync failed: " + e.Message;
            _db.LogEvent("sync-error", e.Message);
            _log.LogError(e, "Catalog sync failed");
            return 0;
        }
        finally
        {
            Syncing = false;
            SyncChanged?.Invoke();
        }
    }

    /// <summary>Compares each installed game with its current post; enqueues updates unless auto-updates are paused or the game opted out.</summary>
    public IReadOnlyList<(InstalledGame Game, CatalogPost Post)> CheckForUpdates()
    {
        var pending = new List<(InstalledGame, CatalogPost)>();
        foreach (var g in _db.GetInstalls())
        {
            if (!AutoQueues(g.State))
                continue;
            var post = _db.GetPost(g.PostId);
            if (post is null || !UpdateDetector.IsUpdate(g, post))
                continue;
            pending.Add((g, post));
            _log.LogInformation(
                "Update available for {Name}: {Installed} → {Site} (auto-update {Auto}, paused {Paused})",
                g.Name,
                g.InstalledVersion,
                post.VersionKey,
                g.AutoUpdate,
                _settings.AutoUpdatesPaused
            );
            if (g.AutoUpdate && !_settings.AutoUpdatesPaused)
                Enqueue(post, isUpdate: true);
        }

        return pending;
    }

    // ---- queue ----

    public Job Enqueue(CatalogPost post, bool isUpdate, ResolvedDownload? preResolved = null)
    {
        lock (_jobs)
        {
            var existing = _jobs.FirstOrDefault(j =>
                j.Post.Id == post.Id
                && j.Stage
                    is JobStage.Queued
                        or JobStage.Resolving
                        or JobStage.Downloading
                        or JobStage.Installing
            );
            if (existing is not null)
                return existing;
            _jobs.RemoveAll(j => j.Post.Id == post.Id); // drop a stale failed/attention entry for the same game
            var job = new Job(post, isUpdate) { PreResolved = preResolved };
            _jobs.Add(job);
            _queue.Enqueue(job);
            _log.LogInformation(
                "Queued {Kind} of {Name} [{Version}]{Pre}",
                isUpdate ? "update" : "install",
                post.Name,
                post.VersionKey,
                preResolved is null ? "" : " with a pre-resolved link"
            );
            var g = _db.GetInstall(post.Id);
            _db.UpsertInstall(
                g is null
                    ? new InstalledGame(
                        post.Id,
                        post.Slug,
                        post.Name,
                        null,
                        "",
                        DateTime.UtcNow,
                        true,
                        InstallState.Queued,
                        null
                    )
                    : g with
                    {
                        State = InstallState.Queued,
                        LastError = null,
                    }
            );
            _wake.Release();
            JobsChanged?.Invoke();
            return job;
        }
    }

    public void Cancel(Job job)
    {
        _log.LogInformation(
            "Cancel requested for {Name} (stage {Stage})",
            job.Post.Name,
            job.Stage
        );
        job.Cts.Cancel();
        if (job.Stage != JobStage.Queued)
            return;
        job.Set(JobStage.Cancelled, "Cancelled");
        RestoreStateAfterFailure(job.Post.Id, null);
        JobsChanged?.Invoke();
    }

    public void ClearFinished()
    {
        lock (_jobs)
            _jobs.RemoveAll(j =>
                j.Stage
                    is JobStage.Done
                        or JobStage.Failed
                        or JobStage.Cancelled
                        or JobStage.NeedsAttention
            );
        JobsChanged?.Invoke();
    }

    public void Uninstall(long postId, bool deleteFiles)
    {
        var g = _db.GetInstall(postId);
        if (g is null)
            return;
        if (_launcher.IsRunning(postId))
        {
            _launcher.Stop(postId);
            // Wine processes outlive the umu wrapper; give them a moment so the folder is not deleted
            // while a child is still writing into it.
            try
            {
                _launcher
                    .WaitForExitAsync(postId, CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(20));
            }
            catch (AggregateException)
            {
                // already gone
            }
        }
        var path = g.ResolvePath(_settings.LibraryRoot);
        _log.LogInformation(
            "Uninstall {Name}: {Path} (delete files: {Delete})",
            g.Name,
            path,
            deleteFiles
        );
        var prefix = PrefixManager.Dir(PrefixRoot, g.Slug);
        if (deleteFiles && Directory.Exists(path))
        {
            // Save the prefix before the folder goes away unless the user asked for it to go too.
            if (!_settings.DeletePrefixOnUninstall)
            {
                try
                {
                    PrefixFor(g);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(
                        "{Name}: could not rescue the prefix: {Error}",
                        g.Name,
                        e.Message
                    );
                }
            }
            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(
                    "{Name}: could not delete {Path}: {Error}",
                    g.Name,
                    path,
                    e.Message
                );
            }
        }
        if (Directory.Exists(prefix))
        {
            if (_settings.DeletePrefixOnUninstall)
            {
                _log.LogInformation("{Name}: removing prefix {Prefix}", g.Name, prefix);
                try
                {
                    Directory.Delete(prefix, true);
                    // Reset leaves .bak-* siblings next to the prefix; nothing else ever deletes them, so an
                    // uninstall that removes the prefix must take them too.
                    PrefixManager.PruneAside(prefix, 0);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(
                        "{Name}: could not remove {Prefix}: {Error}",
                        g.Name,
                        prefix,
                        e.Message
                    );
                }
            }
            else
                _log.LogInformation(
                    "{Name}: prefix kept at {Prefix} (saves live there; 'delete prefix on uninstall' removes it)",
                    g.Name,
                    prefix
                );
        }
        _db.RemoveInstall(postId);
        try
        {
            Shortcuts.RemoveAll(postId, g.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("{Name}: could not remove shortcuts: {Error}", g.Name, e.Message);
        }
        _db.LogEvent("uninstall", g.Name, postId);
        JobsChanged?.Invoke();
    }

    // ---- shortcuts ----

    public bool HasShortcut(long postId, ShortcutPlace place) =>
        _db.GetInstall(postId) is { } g && Shortcuts.Exists(postId, g.Name, place);

    /// <summary>Creates or removes one game's shortcut; failures are logged, never thrown.</summary>
    public void SetShortcut(long postId, ShortcutPlace place, bool present) =>
        _ = SetShortcutAsync(postId, place, present);

    /// <summary>Same as <see cref="SetShortcut"/> but awaitable (install path resolves the cover id first).</summary>
    public async Task SetShortcutAsync(long postId, ShortcutPlace place, bool present)
    {
        if (_db.GetInstall(postId) is not { } g)
            return;
        var post = _db.GetPost(postId);
        try
        {
            if (present)
            {
                var id = _steamApps is null
                    ? post?.SteamAppId
                    : await _steamApps.ResolveAsync(
                        post?.SteamAppId,
                        post?.Name ?? g.Name,
                        _cts.Token
                    );
                if (id is > 0 && _steamGridDb is not null)
                    await _steamGridDb.EnsureIconFileAsync(
                        id.Value,
                        Path.Combine(Settings.DefaultDir, "icons"),
                        _cts.Token
                    );
                Shortcuts.Create(postId, g.Name, Shortcuts.GameIcon(id), place);
            }
            else
                Shortcuts.Remove(postId, g.Name, place);
            _log.LogInformation(
                "{Name}: {Action} {Place} shortcut {Path}",
                g.Name,
                present ? "created" : "removed",
                place,
                Shortcuts.PathFor(postId, g.Name, place)
            );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                "{Name}: could not {What}: {Error}",
                g.Name,
                (present ? "create " : "remove ") + place + " shortcut",
                e.Message
            );
        }
    }

    /// <summary>Settings toggle: apply to every installed game at once.</summary>
    public void SetShortcutsForAll(ShortcutPlace place, bool present)
    {
        foreach (var g in _db.GetInstalls().Where(g => g.State == InstallState.Installed))
            SetShortcut(g.PostId, place, present);
    }

    private async Task WorkerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_queue.TryDequeue(out var job))
            {
                if (job.Cts.IsCancellationRequested)
                    continue;
                Current = job;
                JobsChanged?.Invoke();
                // RunJobAsync already catches its own failures, but its handlers write to the database and
                // the log: a throw from there would escape this loop, fault the worker task and leave every
                // later job Queued forever with nothing in the UI to say why.
                try
                {
                    await RunJobAsync(job);
                }
                catch (Exception e)
                {
                    _log.LogError(
                        e,
                        "=== Job crashed outside its own handlers: {Name}",
                        job.Post.Name
                    );
                    job.Set(JobStage.Failed, e.Message, error: e.ToString());
                }
                finally
                {
                    Current = null;
                    JobsChanged?.Invoke();
                }
            }
        }
    }

    /// <summary>Ends the scheduler's current wait so a changed check interval takes effect now.</summary>
    public void NudgeScheduler()
    {
        while (_reschedule.Wait(0)) { }
        try
        {
            _reschedule.Release();
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private async Task RunJobAsync(Job job)
    {
        var post = job.Post;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token,
            job.Cts.Token
        );
        var ct = linked.Token;
        var previous = _db.GetInstall(post.Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation(
            "=== Job start: {Kind} {Name} [{Version}] (post {Id})",
            job.IsUpdate ? "update" : "install",
            post.Name,
            post.VersionKey,
            post.Id
        );
        try
        {
            // Refresh the post so we use the latest mirrors.
            post = _db.GetPost(post.Id) ?? post;

            var custom = string.IsNullOrWhiteSpace(previous?.InstallPath)
                ? null
                : previous.InstallPath;
            var opt = new InstallOptions(
                _settings.LibraryRoot,
                _settings.KeepBackups,
                custom,
                PrefixManager.Dir(PrefixRoot, post.Slug),
                post.VersionKey
            );
            var archivePath = await DownloadWithRetryAsync(job, post, ct);
            InstallResult result;
            try
            {
                result = await InstallFromArchiveAsync(job, post, archivePath, opt, previous, ct);
            }
            catch (InstallException) when (!ct.IsCancellationRequested)
            {
                // The bytes themselves are bad (truncated, or the mirror served something that is not the
                // archive). Throw them away and fetch once more instead of retrying the same file forever.
                _log.LogWarning(
                    "{Name}: {Archive} did not pass its test; discarding it and downloading again (once)",
                    post.Name,
                    Path.GetFileName(archivePath)
                );
                DiscardArchive(archivePath);
                archivePath = await DownloadWithRetryAsync(job, post, ct);
                result = await InstallFromArchiveAsync(job, post, archivePath, opt, previous, ct);
            }
            if (_settings.DeleteArchiveAfterInstall)
                DiscardArchive(archivePath);

            var exe =
                previous?.ExeRelative is { } prevExe
                && File.Exists(Path.Combine(result.InstallPath, prevExe))
                    ? prevExe
                    : ExeFinder.Best(result.InstallPath, post.Name);
            _log.LogInformation("{Name}: executable {Exe}", post.Name, exe ?? "none found");
            _db.UpsertInstall(
                new InstalledGame(
                    post.Id,
                    post.Slug,
                    post.Name,
                    UpdateDetector.Normalize(post.VersionKey),
                    previous?.InstallPath ?? "",
                    DateTime.UtcNow,
                    previous?.AutoUpdate ?? true,
                    InstallState.Installed,
                    null,
                    exe,
                    previous?.ProtonPath,
                    previous?.LaunchArgs,
                    previous?.LastPlayedUtc,
                    previous?.PlaytimeSeconds ?? 0,
                    previous?.Launch
                )
            );
            if (_settings.ShortcutMenu)
                await SetShortcutAsync(post.Id, ShortcutPlace.Menu, true);
            if (_settings.ShortcutDesktop)
                await SetShortcutAsync(post.Id, ShortcutPlace.Desktop, true);
            var what = job.IsUpdate
                ? $"Updated to {post.VersionKey}"
                : $"Installed {post.VersionKey}";
            _db.LogEvent(
                job.IsUpdate ? "update" : "install",
                $"{post.Name}: {what}"
                    + (
                        result.BackupPath is not null
                            ? $" (saves backed up to {result.BackupPath})"
                            : ""
                    ),
                post.Id
            );
            job.Set(JobStage.Done, what, 1);
            _log.LogInformation(
                "=== Job done: {Name} {What} in {Seconds:0.0}s → {Path}",
                post.Name,
                what,
                sw.Elapsed.TotalSeconds,
                result.InstallPath
            );
            await _notifier.NotifyAsync(
                post.Name,
                what
                    + (
                        result.BackedUpSaves.Count > 0
                            ? $" — {result.BackedUpSaves.Count} save folder(s) backed up"
                            : ""
                    )
            );
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            job.Set(JobStage.Cancelled, "Cancelled");
            _log.LogInformation(
                "=== Job cancelled: {Name} after {Seconds:0.0}s",
                post.Name,
                sw.Elapsed.TotalSeconds
            );
            RestoreStateAfterFailure(post.Id, null, previous);
        }
        catch (NeedsAttentionException e)
        {
            job.Set(JobStage.NeedsAttention, e.Message, error: e.Message);
            _db.SetInstallState(post.Id, InstallState.NeedsAttention, e.Message);
            _db.LogEvent("attention", $"{post.Name}: {e.Message}", post.Id);
            _log.LogWarning("=== Job needs attention: {Name}: {Message}", post.Name, e.Message);
            await _notifier.NotifyAsync(post.Name + " needs you", e.Message);
        }
        catch (Exception e)
        {
            job.Set(JobStage.Failed, e.Message, error: e.ToString());
            RestoreStateAfterFailure(post.Id, e.Message, previous);
            _db.LogEvent("error", $"{post.Name}: {e.Message}", post.Id);
            _log.LogError(
                e,
                "=== Job failed: {Name} after {Seconds:0.0}s",
                post.Name,
                sw.Elapsed.TotalSeconds
            );
            await _notifier.NotifyAsync(post.Name + " failed", e.Message);
        }
    }

    /// <summary>
    /// The name the fetched archive is written as inside the slug directory. The guard is
    /// <c>Path.HasExtension</c>, which is true for a dotted name like <c>Game 1.2</c>:
    /// such a name is kept as it is and never gets a <c>.rar</c> appended (and the installer's
    /// unrar-only branch therefore never sees it). Recorded, not fixed.
    /// </summary>
    public static string ArchiveFileName(string? fileName, string slug)
    {
        var name = Installer.SafeName(fileName ?? slug + ".rar");
        return Path.HasExtension(name) ? name : name + ".rar";
    }

    /// <summary>
    /// The name the fetched <c>.torrent</c> metadata is written as: only the last segment of the
    /// hoster's file name is used, and the extension is enforced case-insensitively.
    /// </summary>
    public static string TorrentFileName(string? fileName, string slug)
    {
        var name = Installer.SafeName(Path.GetFileName(fileName ?? slug + ".torrent"));
        return name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".torrent";
    }

    private async Task<string> DownloadWithRetryAsync(
        Job job,
        CatalogPost post,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(_settings.DownloadsRoot);
        // A complete, verified archive from an interrupted install can be reused without resolving a link again.
        var slugDir = Path.Combine(_settings.DownloadsRoot, post.Slug);
        if (Directory.Exists(slugDir))
        {
            // Only a file that finished downloading AND passed the archive test is reusable. Anything else is
            // leftover junk — the .torrent a mirror served instead of the archive, a truncated download — and
            // reusing it is how a job ends up failing on the same bytes on every retry.
            var verified = Directory
                .EnumerateFiles(slugDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(file => ArchiveMarker.IsVerified(file, post.VersionKey));
            if (verified is not null)
            {
                _log.LogInformation("Reusing verified archive {Dest}", verified);
                return verified;
            }
            PruneStaleDownloads(slugDir);
        }

        var attempts = 0;
        while (true)
        {
            attempts++;
            job.Set(
                JobStage.Resolving,
                attempts == 1
                    ? "Resolving download link…"
                    : $"Resolving download link (attempt {attempts})…"
            );
            _db.SetInstallState(post.Id, InstallState.Downloading);
            ResolvedDownload? resolved;
            IReadOnlyList<string> errors = [];
            if (attempts == 1 && job.PreResolved is not null)
                resolved = job.PreResolved;
            else
                (resolved, errors) = await _resolvers.ResolveAsync(post.Mirrors, ct);
            if (resolved is null)
                throw new NeedsAttentionException(
                    "No mirror could be resolved:\n" + string.Join("\n", errors)
                );

            var fileName = ArchiveFileName(resolved.FileName, post.Slug);
            var dest = Path.Combine(slugDir, fileName);

            CheckDiskSpace(resolved.Size, _settings.DownloadsRoot, _settings.LibraryRoot);
            if (Downloader.IsTorrent(resolved))
                return await DownloadTorrentAsync(job, post, resolved, slugDir, ct);

            job.Set(
                JobStage.Downloading,
                $"Downloading from {resolved.Host} via {_downloader.EngineName}…",
                0
            );
            try
            {
                return await _downloader.DownloadAsync(
                    resolved,
                    dest,
                    new Progress<DownloadProgress>(job.Progress),
                    ct
                );
            }
            catch (DownloadException e) when (e.RetryResolve && attempts < 3)
            {
                _log.LogWarning(
                    "{Name}: {Error}; re-resolving (attempt {Attempt}/3)",
                    post.Name,
                    e.Message,
                    attempts + 1
                );
            }
            catch (HttpRequestException e) when (attempts < 3)
            {
                _log.LogWarning(
                    e,
                    "{Name}: network error; retrying in 15 s (attempt {Attempt}/3)",
                    post.Name,
                    attempts + 1
                );
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (IOException e) when (attempts < 3)
            {
                _log.LogWarning(
                    e,
                    "{Name}: I/O error; retrying in 15 s (attempt {Attempt}/3)",
                    post.Name,
                    attempts + 1
                );
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
    }

    /// <summary>
    /// Some hosters publish a .torrent instead of the archive (MegaDB and BuzzHeavier do it for the big titles).
    /// Fetch the metadata, then let aria2 pull the payload over BitTorrent — the web seed in the file usually means
    /// it starts immediately without waiting for peers — and hand back the archive it produced.
    /// </summary>
    private async Task<string> DownloadTorrentAsync(
        Job job,
        CatalogPost post,
        ResolvedDownload resolved,
        string slugDir,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(slugDir);
        var torrentName = TorrentFileName(resolved.FileName, post.Slug);
        var torrentFile = Path.Combine(slugDir, torrentName);
        job.Set(JobStage.Downloading, $"Fetching torrent from {resolved.Host}…", 0);
        _log.LogInformation(
            "{Name}: {Host} mirror is a torrent; fetching {Torrent}",
            post.Name,
            resolved.Host,
            torrentFile
        );
        await _downloader.DownloadAsync(
            resolved with
            {
                FileName = torrentName,
                Md5 = null,
                Sha256 = null,
            },
            torrentFile,
            null,
            ct
        );
        if (!ArchiveTool.IsTorrent(torrentFile))
            throw new NeedsAttentionException(
                $"{resolved.Host} was supposed to serve a torrent but sent something else"
            );

        job.Set(JobStage.Downloading, "Downloading torrent payload…", 0);
        var payload = await _downloader.TorrentAsync(
            torrentFile,
            slugDir,
            new Progress<DownloadProgress>(job.Progress),
            ct
        );
        var archive = PickArchive(payload.Files);
        if (archive is null)
            throw new NeedsAttentionException(
                "That torrent contains "
                    + payload.Files.Count
                    + " files ("
                    + string.Join(", ", payload.Files.Take(4).Select(Path.GetFileName))
                    + "…) and none of them is a RAR/7z/ZIP archive. Try another mirror."
            );
        _log.LogInformation(
            "{Name}: torrent produced {Archive} ({Size})",
            post.Name,
            Path.GetFileName(archive),
            Job.Fmt(new FileInfo(archive).Length)
        );
        return archive;
    }

    /// <summary>The archive among a torrent's files: split sets resolve to their first volume, and the biggest wins.</summary>
    public static string? PickArchive(IReadOnlyList<string> files) =>
        PickArchive(files, File.Exists, f => new FileInfo(f).Length, ArchiveTool.LooksLikeArchive);

    /// <summary>
    /// The same rule with file existence, length and archive sniffing injected, so the decision
    /// can be recorded and ported without touching the disk: missing files and anything that is
    /// not a RAR/7z/ZIP are skipped, split sets collapse to their first volume, and the biggest
    /// usable candidate wins (ties keep input order, as the stable sort did).
    /// </summary>
    public static string? PickArchive(
        IReadOnlyList<string> files,
        Func<string, bool> exists,
        Func<string, long> length,
        Func<string, bool> looksLikeArchive
    )
    {
        var candidates = files
            .Where(f => exists(f) && looksLikeArchive(f))
            .Select(f => ArchiveTool.FirstPart(f, exists))
            .ToList();
        return candidates.Count == 0 ? null : candidates.OrderByDescending(length).First();
    }

    /// <summary>Deletes the small junk a failed attempt leaves behind (HTML error pages), never a resumable payload.</summary>
    private void PruneStaleDownloads(string slugDir)
    {
        foreach (var file in Directory.EnumerateFiles(slugDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (ArchiveMarker.IsVerified(file))
                    continue;
                if (
                    file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                var info = new FileInfo(file);
                var kind = ArchiveTool.SniffKind(file);
                if (info.Length > 1 << 20 || (kind is not null and not "html"))
                    continue;
                _log.LogInformation(
                    "Download dir: dropping leftover {File} ({Kind}, {Bytes} bytes)",
                    Path.GetFileName(file),
                    kind ?? "unknown",
                    info.Length
                );
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug("Could not prune {File}: {Error}", file, e.Message);
            }
        }
    }

    /// <summary>Removes an archive the installer rejected, plus its marker and aria2 control file, so the retry re-downloads.</summary>
    private void DiscardArchive(string archivePath)
    {
        ArchiveMarker.Clear(archivePath);
        foreach (var path in new[] { archivePath, archivePath + ".aria2" })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Could not delete {Path}: {Error}", path, e.Message);
            }
        }
    }

    /// <summary>Waits for a running game, then runs the installer against the archive (test → extract → swap).</summary>
    private async Task<InstallResult> InstallFromArchiveAsync(
        Job job,
        CatalogPost post,
        string archivePath,
        InstallOptions opt,
        InstalledGame? previous,
        CancellationToken ct
    )
    {
        if (_launcher.IsRunning(post.Id))
        {
            _log.LogInformation(
                "{Name} is running; waiting for it to exit before installing",
                post.Name
            );
            job.Set(
                JobStage.Installing,
                "Downloaded. Waiting for the game to close before installing…"
            );
            await _launcher.WaitForExitAsync(post.Id, ct);
        }

        job.Set(JobStage.Installing, "Installing…");
        _db.SetInstallState(post.Id, InstallState.Installing);
        return await _installer.InstallAsync(
            archivePath,
            post.Slug,
            post.SteamAppId,
            previous?.InstalledVersion,
            opt,
            new Progress<(string Stage, int Percent)>(p =>
                job.Set(JobStage.Installing, $"{p.Stage} {p.Percent}%", p.Percent / 100.0)
            ),
            ct
        );
    }

    private void CheckDiskSpace(long? archiveSize, string downloadsRoot, string libraryRoot)
    {
        if (archiveSize is not { } size)
            return;
        Directory.CreateDirectory(downloadsRoot);
        Directory.CreateDirectory(libraryRoot);
        var dl = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(downloadsRoot))!);
        var lib = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(libraryRoot))!);
        // archive + extracted (~1.3x) if same drive; be conservative.
        var need = dl.Name == lib.Name ? (long)(size * 2.4) : size;
        _log.LogDebug(
            "Disk check: need ~{Need} on {Drive}, free {Free}",
            Job.Fmt(need),
            dl.Name,
            Job.Fmt(dl.AvailableFreeSpace)
        );
        if (dl.AvailableFreeSpace < need)
            throw new NeedsAttentionException(
                $"Not enough disk space: need ~{Job.Fmt(need)}, have {Job.Fmt(dl.AvailableFreeSpace)} on {dl.Name}"
            );
    }

    private void RestoreStateAfterFailure(
        long postId,
        string? error,
        InstalledGame? previous = null
    )
    {
        previous ??= _db.GetInstall(postId);
        if (previous is null)
            return;
        if (
            previous.InstalledVersion is null
            && !Directory.Exists(previous.ResolvePath(_settings.LibraryRoot))
        )
        {
            if (error is null)
                _db.RemoveInstall(postId);
            else
                _db.SetInstallState(postId, InstallState.Failed, error);
        }
        else
            _db.SetInstallState(
                postId,
                error is null ? InstallState.Installed : InstallState.Failed,
                error
            );
    }

    private async Task SchedulerLoopAsync()
    {
        // First sync shortly after start; then on the configured interval.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
        }
        catch
        {
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await SyncCatalogAsync(ct: _cts.Token);
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                _log.LogError(
                    "Scheduler tick was cancelled by a request timeout, not by shutdown; continuing"
                );
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Scheduler tick failed");
            }

            _log.LogDebug(
                "Next catalog check in {Minutes} min",
                Math.Max(5, _settings.CheckIntervalMinutes)
            );
            try
            {
                // A semaphore instead of Task.Delay: the settings page can end the wait when the interval
                // changes, otherwise a user who lowers it to 10 min waits out the rest of the hour first.
                await _reschedule.WaitAsync(
                    TimeSpan.FromMinutes(Math.Max(5, _settings.CheckIntervalMinutes)),
                    _cts.Token
                );
            }
            catch
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _log.LogInformation("UpdateService stopping");
        _launcher.Exited -= OnGameExited;
        await _cts.CancelAsync();
        foreach (var j in Jobs)
            await j.Cts.CancelAsync();
        if (_worker is not null)
            try
            {
                await _worker;
            }
            catch
            {
                // the worker loop ends with OperationCanceledException by design
            }

        if (_scheduler is not null)
            try
            {
                await _scheduler;
            }
            catch
            {
                // same as above
            }
    }
}

/// <summary>The job cannot proceed without the user (no mirror resolvable, disk full, challenge needs a real browser).</summary>
public sealed class NeedsAttentionException(string message) : Exception(message);
