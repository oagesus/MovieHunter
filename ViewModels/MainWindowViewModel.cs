using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MovieHunter.Models;
using MovieHunter.Services;

namespace MovieHunter.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AggregatedSearchService _search;
    private readonly YtDlpService _ytdlp;
    private readonly SearxngConfigClient _configClient;
    private readonly BsToService _bsto;
    private readonly TmdbClient _tmdb;
    private CancellationTokenSource? _cts;

    public SourcesSettings Sources { get; } = new();
    public RecentlyWatched Recent { get; } = new();
    public MyList MyList { get; } = new();
    public BsToPhantomLanguages BsToPhantomLanguages { get; } = new();
    public BsToService BsTo => _bsto;
    public TmdbClient Tmdb => _tmdb;
    public YtDlpService YtDlp => _ytdlp;

    public MainWindowViewModel()
    {
        var searchHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConfig.HttpTimeoutSeconds) };
        var ytdlpHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConfig.YtDlpTimeoutSeconds) };

        _bsto = new BsToService(ytdlpHttp);
        _tmdb = new TmdbClient(searchHttp);
        _search = new AggregatedSearchService(
            new SearxngClient(searchHttp),
            _tmdb,
            _bsto);
        _ytdlp = new YtDlpService(ytdlpHttp);
        _configClient = new SearxngConfigClient(searchHttp);

        Sources.Load();
        BsToPhantomLanguages.Load();
        Recent.Load();
        // Collapse historical bs.to language duplicates (German +
        // German Subbed of the same show being saved as separate
        // entries under earlier app builds). Idempotent — does
        // nothing once everything's already been merged.
        Recent.MergeLanguageDuplicates();
        MyList.Load();
        // Backfill My-list progress from Recently watched: covers entries
        // saved before position tracking existed and movies played from
        // other tabs that never updated the My-list copy.
        // One-shot repair: a prior bug saved bs.to series MyList entries
        // keyed by EPISODE URL with the EPISODE title. Walk through
        // those at startup and rekey them under the series URL with
        // the show name + LastEpisode metadata. Idempotent — does
        // nothing once everything is on the correct key. Must run
        // BEFORE the seed calls so the seeds find the now-correct
        // PageUrl when looking up the matching Recent entry.
        MyList.MigrateBstoEpisodeKeys(Recent);
        MyList.SeedProgressFromRecent(Recent);
        // Same idea for the per-series last-episode info — covers
        // entries saved before the LastEpisode* fields existed AND
        // entries added to My-list after the show was already being
        // watched (Add doesn't carry the episode info forward).
        MyList.SeedLastEpisodeFromRecent(Recent);
        _ = InitializeSourcesAsync();
        // Fire-and-forget TMDb poster backfill — see method comment. The
        // user may have entries saved before TMDb was enabled (or before
        // poster fetching existed at all), and this catches them up the
        // first time the app boots with a valid TMDb key.
        _ = BackfillBstoPostersAsync();
        // Re-run the backfill whenever the user toggles TMDb on or
        // updates the API key, so a key entered weeks after first save
        // immediately enriches existing Recent / MyList cards instead
        // of waiting for them to be searched again.
        Sources.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SourcesSettings.TmdbEnabled)
                || e.PropertyName == nameof(SourcesSettings.TmdbApiKey))
            {
                _ = BackfillBstoPostersAsync();
            }
        };
    }

    // Backfill is throttled in batches so we never burst past TMDb's
    // 50-req/s rate limit. Each SearchTvPosterAsync makes up to 2 calls
    // (German query first, English fallback), so a batch of 25 lookups
    // is at most ~50 actual API calls — fired in parallel within a
    // single batch, then a 1.1 s sleep before the next batch resets the
    // rolling rate window. Total backfill time for 200 entries is
    // ~9 s of background work, completely fire-and-forget.
    private const int MaxBackfillCallsPerRun = 200;
    private const int BackfillBatchSize = 25;
    private const int BackfillBatchDelayMs = 1100;
    // Set the moment a backfill starts so an overlapping call (settings
    // change while a startup backfill is still in flight) doesn't
    // double-fire the same lookups.
    private bool _backfillRunning;

    /// <summary>
    /// One-shot TMDb poster backfill for bs.to series in Recent / MyList
    /// that were saved without a thumbnail. Runs on app start and again
    /// when the user enables TMDb / changes the API key. No-ops when
    /// TMDb is disabled, the key is blank, or there's nothing to fill.
    /// Persists once at the end if anything actually changed.
    /// </summary>
    private async Task BackfillBstoPostersAsync()
    {
        if (_backfillRunning) return;
        if (!Sources.TmdbEnabled) return;
        if (string.IsNullOrWhiteSpace(Sources.TmdbApiKey)) return;
        _backfillRunning = true;
        try
        {
            // Group by series URL: same show may appear in both lists, so
            // a single TMDb lookup updates both entries. Stores the title
            // (first seen wins; in practice the title is the same in both)
            // plus optional refs to the Recent and MyList entries.
            var pending = new System.Collections.Generic.Dictionary<
                string,
                (string Title, RecentWatch? Rw, MyListEntry? M)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var rw in Recent.Items)
            {
                if (!IsBstoSeriesEntry(rw.PageUrl, rw.ThumbnailUrl)) continue;
                pending[rw.PageUrl] = (rw.Title, rw, null);
            }
            foreach (var m in MyList.Items)
            {
                if (!IsBstoSeriesEntry(m.PageUrl, m.ThumbnailUrl)) continue;
                if (pending.TryGetValue(m.PageUrl, out var existing))
                    pending[m.PageUrl] = (existing.Title, existing.Rw, m);
                else
                    pending[m.PageUrl] = (m.Title, null, m);
            }
            if (pending.Count == 0) return;

            var work = pending.Values.Take(MaxBackfillCallsPerRun).ToList();
            var anyChanges = false;

            // Batched fan-out so we never spike past TMDb's 50-req/s
            // limit. Within a batch, lookups run in parallel; between
            // batches we sleep BackfillBatchDelayMs to reset the
            // rolling rate window. Save() runs at the end so the
            // recent.json / myList.json files are written once total
            // even when there are many batches.
            for (var i = 0; i < work.Count; i += BackfillBatchSize)
            {
                var batchEnd = Math.Min(i + BackfillBatchSize, work.Count);
                var batch = new System.Collections.Generic.List<Task<bool>>(batchEnd - i);
                for (var j = i; j < batchEnd; j++)
                {
                    var entry = work[j];
                    batch.Add(LookupAndApplyAsync(entry));
                }
                var results = await Task.WhenAll(batch);
                if (results.Any(c => c)) anyChanges = true;
                if (batchEnd < work.Count)
                    await Task.Delay(BackfillBatchDelayMs);
            }

            if (anyChanges)
            {
                Recent.Save();
                MyList.Save();
            }
        }
        finally
        {
            _backfillRunning = false;
        }
    }

    /// <summary>
    /// Single-entry lookup helper for the backfill batches. Returns
    /// true when a poster was found and applied, false otherwise.
    /// </summary>
    private async Task<bool> LookupAndApplyAsync(
        (string Title, RecentWatch? Rw, MyListEntry? M) entry)
    {
        string? poster = null;
        try
        {
            poster = await _tmdb.SearchTvPosterAsync(
                Sources.TmdbApiKey, entry.Title, default);
        }
        catch { /* network / parse error → no poster */ }
        if (string.IsNullOrEmpty(poster)) return false;
        // Hop to the UI thread before mutating: ObservableProperty
        // setters fire PropertyChanged, and bound controls expect
        // those notifications on the dispatcher.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (entry.Rw is not null) entry.Rw.ThumbnailUrl = poster;
            if (entry.M is not null) entry.M.ThumbnailUrl = poster;
        });
        return true;
    }

    private static bool IsBstoSeriesEntry(string? pageUrl, string? thumbnailUrl)
    {
        if (string.IsNullOrEmpty(pageUrl)) return false;
        if (!string.IsNullOrEmpty(thumbnailUrl)) return false;
        return pageUrl.Contains("bs.to", StringComparison.OrdinalIgnoreCase);
    }

    private async Task InitializeSourcesAsync()
    {
        try
        {
            var live = await _configClient.GetVideoEnginesAsync(CancellationToken.None);
            // Manually inject bsto: it doesn't live in SearXNG (bs.to has
            // no usable HTTP-GET search) but the user still needs a
            // toggle for it in Sources. AggregatedSearchService routes
            // bsto-flagged queries directly to BsToService.
            var withBsto = new System.Collections.Generic.List<string>(live);
            if (!withBsto.Any(s => string.Equals(s, "bsto", StringComparison.OrdinalIgnoreCase)))
                withBsto.Add("bsto");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => Sources.MergeWithLive(withBsto));
        }
        catch { }
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _status = "Enter a title and click Search.";
    [ObservableProperty] private VideoResult? _selectedResult;
    // The Title field at the moment the last search ran — used to render
    // "<N> results for '<query>'." in the status bar even after the user
    // edits the Title field.
    [ObservableProperty] private string _lastSearchTitle = "";
    // Drives the transport-bar PiP ToggleButton's enter/exit glyph swap.
    // The View toggles this from EnterPipMode / ExitPipMode.
    [ObservableProperty] private bool _isPipActive;

    public ObservableCollection<VideoResult> Results { get; } = new();

    public event Action<StreamResult, VideoResult, long>? PlayRequested;

    [RelayCommand]
    private async Task SearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Results.Clear();
        IsSearching = true;
        LastSearchTitle = Title;
        Status = "Searching…";

        try
        {
            // Collect everything first so the UI shows the full list at once
            // rather than filling in progressively over the search duration.
            var batch = new System.Collections.Generic.List<VideoResult>();
            await foreach (var r in _search.SearchAsync(Title, Year, Sources, _cts.Token))
            {
                batch.Add(r);
            }
            foreach (var r in batch) Results.Add(r);

            var unresponsive = _search.LastUnresponsiveEngines;
            var summary = string.IsNullOrEmpty(LastSearchTitle)
                ? $"Found {Results.Count} search results."
                : $"Found {Results.Count} search results for '{LastSearchTitle}'.";
            Status = unresponsive.Count > 0
                ? $"{summary} Slow/failed: {string.Join(", ", unresponsive)}"
                : summary;
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    public async Task PlayResultAsync(VideoResult result, long startPosMs = 0)
    {
        Status = $"Extracting stream for: {result.Title}…";
        var stream = await _ytdlp.ExtractStreamUrlAsync(result.PageUrl, CancellationToken.None);
        if (stream is null || string.IsNullOrWhiteSpace(stream.Url))
        {
            Status = "Could not extract a direct stream. Try another result.";
            return;
        }

        // Just kicked off VLC — playback hasn't actually started yet.
        // The MainWindow's Playing event handler flips this to "Playing." once
        // frames are arriving.
        Status = "Loading…";
        PlayRequested?.Invoke(stream, result, startPosMs);
    }

    /// <summary>
    /// Variant of <see cref="PlayResultAsync"/> for cases where the
    /// caller has already resolved the hoster URL — bypasses yt-dlp's
    /// /extract on bs.to itself, but still hands the resulting hoster
    /// URL through yt-dlp so its existing per-hoster extractors (Voe,
    /// Doodstream, …) handle the stream pull. Used by the bs.to
    /// captcha overlay, which captures the hoster URL by intercepting
    /// the /ajax/embed.php response.
    /// </summary>
    public async Task PlayResolvedHosterAsync(VideoResult result, string hosterUrl, long startPosMs = 0)
    {
        if (string.IsNullOrWhiteSpace(hosterUrl))
        {
            Status = "Couldn't resolve a hoster URL.";
            return;
        }
        Status = $"Resolving {result.Title}…";
        var stream = await _ytdlp.ExtractStreamUrlAsync(hosterUrl, CancellationToken.None);
        if (stream is null || string.IsNullOrWhiteSpace(stream.Url))
        {
            Status = "Hoster reached but couldn't extract a stream.";
            return;
        }
        Status = "Loading…";
        PlayRequested?.Invoke(stream, result, startPosMs);
    }

    public Task PlayRecentAsync(RecentWatch recent) =>
        PlayResultAsync(recent.ToVideoResult(), recent.PositionMs);

    /// <summary>
    /// Plays a stream whose URL was already captured client-side
    /// (e.g. from the in-app WebView after Vidmoly's Turnstile gate
    /// passed). No /extract call — we already have the m3u8 and the
    /// matching Referer / User-Agent — so this is just the final
    /// PlayRequested fan-out plus a Status update for symmetry with
    /// the other Play* paths.
    /// </summary>
    public void PlayCapturedStream(VideoResult result, StreamResult stream, long startPosMs = 0)
    {
        Status = "Loading…";
        PlayRequested?.Invoke(stream, result, startPosMs);
    }
}
