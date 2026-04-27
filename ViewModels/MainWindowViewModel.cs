using System;
using System.Collections.ObjectModel;
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
    private CancellationTokenSource? _cts;

    public SourcesSettings Sources { get; } = new();
    public RecentlyWatched Recent { get; } = new();

    public MainWindowViewModel()
    {
        var searchHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConfig.HttpTimeoutSeconds) };
        var ytdlpHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConfig.YtDlpTimeoutSeconds) };

        _search = new AggregatedSearchService(
            new SearxngClient(searchHttp),
            new TmdbClient(searchHttp));
        _ytdlp = new YtDlpService(ytdlpHttp);
        _configClient = new SearxngConfigClient(searchHttp);

        Sources.Load();
        Recent.Load();
        _ = InitializeSourcesAsync();
    }

    private async Task InitializeSourcesAsync()
    {
        try
        {
            var live = await _configClient.GetVideoEnginesAsync(CancellationToken.None);
            if (live.Count == 0) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => Sources.MergeWithLive(live));
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

    public Task PlayRecentAsync(RecentWatch recent) =>
        PlayResultAsync(recent.ToVideoResult(), recent.PositionMs);
}
