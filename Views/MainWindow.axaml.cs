using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// Window host. Owns the constructor (which wires the VLC subsystem,
// the keyboard router, the volume popup, and the per-control event
// hooks), the AppView state machine that swaps between Search /
// Recently watched / My list / Settings tabs, the window-chrome
// buttons (minimize / max-restore / close / drag), the custom
// edge-resize handling, and the lifecycle hooks that fire when the
// VM attaches or the window closes. The video subsystem itself,
// PiP, themes, settings panel, scrollbar, and card visuals all live
// in their own MainWindow.*.cs partials.
public partial class MainWindow : Window
{
    // _currentVideoResult is referenced by both the video subsystem
    // (start/stop/save-position) and the view-switching layer
    // (knowing whether to enter PiP on tab change), so it lives at
    // the top-level partial.
    private VideoResult? _currentVideoResult;
    // Optimistic-highlight target: the PageUrl the user just clicked in
    // the Recently watched panel. Drives "Currently playing" on the
    // tapped card during the brief stream-URL-extraction delay before
    // OnPlayRequested actually sets _currentVideoResult.
    private string? _pendingPlayingPageUrl;
    // Which view the user was on when playback started — Back_Click restores it.
    private enum AppView { Search, RecentlyWatched, MyList, Settings }
    private AppView _originView = AppView.Search;
    // Currently-active sidebar tab. Tracks where the user is so PiP restore
    // can return playback to the right place.
    private AppView _activeTab = AppView.Search;

    public MainWindow()
    {
        InitializeComponent();

        // Theme RadioButton state is synced in OnDataContextChanged,
        // once the VM (and its Sources.Theme) is available.

        Core.Initialize();
        _libvlc = new LibVLC(
            "--http-reconnect",
            "--network-caching=500",
            "--audio-desync=0",
            "--no-audio-time-stretch",
            "--clock-jitter=0",
            "--clock-synchro=0",
            "--aout=mmdevice");
        _player = new MediaPlayer(_libvlc) { Volume = 100 };

        SetupSoftwareRendering();

        _hideUiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideUiTimer.Tick += (_, _) => HideFullscreenUi();

        // If no frame arrives within 25s of Play(), assume the stream is dead
        // and show a clear error instead of leaving a black "Playing" screen.
        // 25s (was 12s) covers the cold-start chain on bs.to-resolved
        // hoster streams: DNS resolution + TLS handshake + master m3u8
        // fetch + variant selection + first segment download. The retry
        // case (user pressed Play again after a previous error) hits
        // cached DNS/TLS/segment state and starts fast, but the first
        // attempt after a captcha solve can legitimately need 15-20s.
        _playbackWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        _playbackWatchdog.Tick += OnPlaybackWatchdogTick;

        WirePlayerEvents();

        PositionSlider.ValueChanged += OnPositionSliderChanged;
        PositionSlider.IsEnabled = false;
        PipPositionSlider.ValueChanged += OnPositionSliderChanged;
        PipPositionSlider.IsEnabled = false;
        // Pointer press / release on the slider as drag-detection.
        // Avoiding Thumb.DragStarted/DragCompleted because those only
        // fire on direct thumb grabs — a click on the track followed
        // by a drag never triggered DragStarted, leaving the dragging
        // flag false and letting TimeChanged yank the slider back to
        // the player's pre-seek position mid-drag. PointerPressed
        // catches every interaction (thumb grab, track click, click-
        // and-drag), and PointerCaptureLost backs up Released in case
        // the user drags off-window before letting go. Tunneling
        // (RoutingStrategies.Tunnel | Bubble) so the handler runs even
        // when a child of the slider (like the inner Thumb) handles
        // the event.
        PositionSlider.AddHandler(
            InputElement.PointerPressedEvent,
            OnPositionSliderPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel
                | Avalonia.Interactivity.RoutingStrategies.Bubble);
        PositionSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            OnPositionSliderPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel
                | Avalonia.Interactivity.RoutingStrategies.Bubble);
        PositionSlider.AddHandler(
            InputElement.PointerCaptureLostEvent,
            OnPositionSliderPointerCaptureLost,
            Avalonia.Interactivity.RoutingStrategies.Tunnel
                | Avalonia.Interactivity.RoutingStrategies.Bubble);
        PipPositionSlider.AddHandler(
            InputElement.PointerPressedEvent,
            OnPositionSliderPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel
                | Avalonia.Interactivity.RoutingStrategies.Bubble);
        PipPositionSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            OnPositionSliderPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel
                | Avalonia.Interactivity.RoutingStrategies.Bubble);
        PipPositionSlider.AddHandler(
            InputElement.PointerCaptureLostEvent,
            OnPositionSliderPointerCaptureLost,
            Avalonia.Interactivity.RoutingStrategies.Tunnel
                | Avalonia.Interactivity.RoutingStrategies.Bubble);

        // Track window resizes so the floating PiP frame keeps its
        // distance from the bottom-right edge instead of staying glued
        // to its old top-left coordinate (which clipped on shrink and
        // drifted away from the corner on grow).
        PipHost.SizeChanged += OnPipHostSizeChanged;

        // Close any open dropdowns when the window resizes — Avalonia's
        // popups are positioned against their target at open time and
        // don't reposition on a size change, so they'd otherwise float
        // detached from the (now-moved) ComboBox button. Closing is the
        // simplest cure: the user re-opens, popup re-anchors.
        SizeChanged += OnWindowSizeChangedForPopups;

        // Highlight the initial sidebar tab (Search by default).
        UpdateActiveNavTab();

        VideoOverlayRoot.PointerMoved += OnOverlayPointerMoved;
        VideoOverlayRoot.PointerEntered += OnOverlayPointerMoved;

        // Single click on video → play/pause (delayed ~250ms so a double
        // click can suppress it). Double click → toggle fullscreen.
        _singleTapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _singleTapTimer.Tick += OnSingleTapTick;
        VideoOverlayRoot.Tapped += OnVideoTapped;
        VideoOverlayRoot.DoubleTapped += OnVideoDoubleTapped;

        // Netflix-style volume popup: opens on mute-button hover, stays
        // open while the pointer is over either the button or the popup
        // content, closes when both lose the pointer. A short timer
        // bridges the tiny gap the pointer traverses between them.
        _volumePopupHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _volumePopupHideTimer.Tick += (_, _) =>
        {
            _volumePopupHideTimer.Stop();
            // Never close while the slider thumb is being dragged —
            // otherwise the popup disappears mid-drag and subsequent
            // clicks land on the video (toggling play/pause).
            if (_volumeDragging) return;
            if (_pointerOverMute || _pointerOverVolumePopup) return;
            VolumePopup.IsOpen = false;
            // Drop the sticky hover scale on the mute icon now that
            // its popup is gone.
            MuteBtn?.Classes.Set("popup-open", false);
            // Cross-check against the other transport-bar hover popups
            // so closing the volume popup doesn't re-show the scrub bar
            // while episodes / next-episode is still up.
            if (!IsAnyTransportPopupOpen())
                TimelineRow.IsVisible = true;
        };

        // Drag tracking on the volume slider — used to keep the popup
        // open while the user is holding the thumb, even if the pointer
        // leaves the popup bounds during the drag. The visual "press"
        // tint that used to flash on off-track clicks is suppressed
        // via scoped Slider.Resources (see VolumeSlider's XAML), which
        // alias the *Pressed brushes back to their non-pressed colours.
        // That keeps the full thumb-grab area clickable for dragging
        // (no hit-band filter to break the drag) while not visually
        // changing the slider when the click happens to land in the
        // forgiving side-margin of the slider's bounds.
        VolumeSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(VolumeSlider).Properties.IsLeftButtonPressed)
            {
                _volumeDragging = true;
                _volumePopupHideTimer.Stop();
            }
        }, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _volumeDragging = false;
            // Drag finished — persist the final value now. Volume_Changed
            // skips saving during the drag to avoid thrashing the JSON
            // file, so this is the one save per drag gesture.
            if (DataContext is MainWindowViewModel vm)
                vm.Sources.Volume = (int)VolumeSlider.Value;
            // If the pointer ended up off both the button and popup
            // during the drag, schedule a close now that the drag is
            // done. Otherwise the popup just stays open.
            if (!_pointerOverMute && !_pointerOverVolumePopup)
            {
                _volumePopupHideTimer.Stop();
                _volumePopupHideTimer.Start();
            }
        }, RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;
        // Tunnel (preview) so we intercept arrow keys and space BEFORE any
        // focused Slider can handle them. handledEventsToo=true in case the
        // focused control already marked the event handled.
        AddHandler(KeyDownEvent, OnPreviewKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // Blur search TextBoxes when the user clicks outside of them.
        AddHandler(PointerPressedEvent, OnRootPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // Track press / release inside the in-video Episodes popup so a
        // ScrollBar-thumb drag inside it doesn't close the popup mid-
        // drag. Same Tunnel-on-the-host pattern the VolumeSlider uses
        // for _volumeDragging.
        HookEpisodesPopupDragHandlers();
        PointerMoved += OnWindowPointerMoved;
        Closing  += OnClosing;
    }

    // Tracks the VM we've subscribed to. DataContextChanged can in
    // principle fire twice with the same instance (e.g. during a hot-
    // reload, or if a future change re-assigns DataContext); without
    // this guard each spurious fire would attach another set of
    // PlayRequested + CollectionChanged closures, leaving the old ones
    // alive and double-firing every event afterwards.
    private MainWindowViewModel? _hookedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (ReferenceEquals(_hookedVm, vm)) return;
            _hookedVm = vm;
            vm.PlayRequested += OnPlayRequested;

            // Restore saved volume (triggers Volume_Changed which updates
            // the player volume and mute icon).
            VolumeSlider.Value = vm.Sources.Volume;

            // Empty-state hint in the recently-watched overlay — visible
            // only while the list is empty.
            void RefreshRecentEmpty() =>
                RecentEmptyLabel.IsVisible = vm.Recent.Items.Count == 0;
            RefreshRecentEmpty();
            // Subscribe to PropertyChanged on every RecentWatch already
            // loaded (the list is populated before this hook fires) so
            // in-place episode updates from UpdatePositionAndSave reach
            // OnRecentWatchPropertyChanged and refresh the search-row
            // label. The CollectionChanged hook below handles items
            // added or removed after this point.
            foreach (var rw in vm.Recent.Items)
                rw.PropertyChanged += OnRecentWatchPropertyChanged;
            vm.Recent.Items.CollectionChanged += (_, args) =>
            {
                // Hook / unhook PropertyChanged for items entering /
                // leaving the collection so the listener set stays in
                // sync with the live items.
                if (args.NewItems is not null)
                    foreach (RecentWatch rw in args.NewItems)
                        rw.PropertyChanged += OnRecentWatchPropertyChanged;
                if (args.OldItems is not null)
                    foreach (RecentWatch rw in args.OldItems)
                        rw.PropertyChanged -= OnRecentWatchPropertyChanged;
                RefreshRecentEmpty();
                // Re-apply the "currently playing" highlight: SaveCurrentPosition
                // (called on pause / seek) re-inserts the card at the top,
                // which destroys and recreates its visual container — so the
                // highlight on the new container needs to be set fresh.
                RefreshRecentPlayingHighlight();
                // A play just upserted a Recent entry — push the new
                // last-watched episode subtitle onto any matching search
                // result so the bottom-aligned label appears immediately.
                RefreshAllLastWatchedFromRecent();
            };

            // Same pattern for the search results panel — show the empty
            // hint while the user hasn't searched / no results came back.
            void RefreshSearchEmpty() =>
                SearchResultsEmptyLabel.IsVisible = vm.Results.Count == 0;
            RefreshSearchEmpty();
            vm.Results.CollectionChanged += (_, _) =>
            {
                RefreshSearchEmpty();
                // New search results need their saved-state populated
                // from MyList so the chip renders the right glyph.
                RefreshAllIsInMyListFromMyList();
                // Same idea for the "currently playing" highlight: a
                // fresh search may pull up the active video; the
                // matching row should light up immediately, not wait
                // for the next playback state change.
                SyncSearchResultsCurrentlyPlaying();
                // Surface the last-watched episode ("S02E05 · Title")
                // bottom-aligned on series rows the user has watched
                // before, so the resume target is visible at a glance
                // without opening the picker.
                RefreshAllLastWatchedFromRecent();
            };

            // Sync poster-chip state once on load and again whenever the
            // saved list changes (so chips on Recent / Search update
            // even if they're not the surface that triggered the toggle).
            RefreshAllIsInMyListFromMyList();
            vm.MyList.Items.CollectionChanged += (_, _) =>
            {
                RefreshAllIsInMyListFromMyList();
                if (MyListEmptyLabel is not null)
                    MyListEmptyLabel.IsVisible = vm.MyList.Items.Count == 0;
                // MoveToTopAndSave (called on play start) removes and
                // re-inserts the entry, which destroys and recreates
                // its visual container — the "Currently playing" overlay
                // on the new container needs to be re-applied or it
                // disappears the moment the card floats to the top.
                // Mirrors the equivalent hook on the Recent collection.
                RefreshRecentPlayingHighlight();
            };

            switch (vm.Sources.Theme)
            {
                case "Dracula": DraculaThemeRadio.IsChecked = true; break;
                case "Netflix": NetflixThemeRadio.IsChecked = true; break;
                case "PrimeVideo": PrimeVideoThemeRadio.IsChecked = true; break;
                case "DisneyPlus": DisneyPlusThemeRadio.IsChecked = true; break;
                case "Catppuccin": CatppuccinThemeRadio.IsChecked = true; break;
                case "LightMint": LightMintThemeRadio.IsChecked = true; break;
                case "LightApricot": LightApricotThemeRadio.IsChecked = true; break;
                // Includes "LightLavender" and any legacy / unrecognized
                // value (System / Light / Dark from previous saves).
                default: LightLavenderThemeRadio.IsChecked = true; break;
            }
        }
    }

    // ── Window chrome buttons ────────────────────────────────────────
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaxRestore_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindow_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── Sidebar nav handlers ──────────────────────────────────────────
    // Three views share the main area: Search (default — search row +
    // results panel + video stage), Recently watched (just the video
    // stage with the recently-watched overlay), and Settings (embedded
    // settings panel replaces the content grid). Helpers below toggle
    // visibility.

    private void NavSearch_Click(object? sender, RoutedEventArgs e)
        => SwitchToTab(AppView.Search);

    private void NavRecent_Click(object? sender, RoutedEventArgs e)
        => SwitchToTab(AppView.RecentlyWatched);

    private void NavMyList_Click(object? sender, RoutedEventArgs e)
        => SwitchToTab(AppView.MyList);

    private void NavSettings_Click(object? sender, RoutedEventArgs e)
        => SwitchToTab(AppView.Settings);

    // Common dispatch for the four sidebar nav buttons: flip the active
    // tab, repaint the rail's highlight, drop the playing video into PiP
    // (since the new tab won't have a full-size video stage), mount the
    // matching panel, and reset the per-tab status footer text.
    private void SwitchToTab(AppView tab)
    {
        _activeTab = tab;
        UpdateActiveNavTab();
        if (_currentVideoResult is not null) EnterPipMode();
        ShowViewFor(tab);
        ResetStatusForActiveTab();
    }

    // Picks the right per-tab default for the bottom status bar. On the
    // Search tab it surfaces the last search summary if there were
    // results; otherwise the prompt to enter a title.
    private void ResetStatusForActiveTab()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.Status = _activeTab switch
        {
            AppView.RecentlyWatched => "Click a movie to continue watching.",
            AppView.MyList => "Click a movie to watch from your list.",
            AppView.Settings => "Settings save automatically.",
            _ => vm.Results.Count > 0 && !string.IsNullOrEmpty(vm.LastSearchTitle)
                ? $"Found {vm.Results.Count} search results for '{vm.LastSearchTitle}'."
                : "Enter a title and click Search.",
        };
    }

    // Toggles the .active CSS-style class on the four sidebar nav
    // buttons so the icon stroke flips to the accent color on the
    // currently-active tab — visual cue for "where am I".
    private void UpdateActiveNavTab()
    {
        SetActive(NavSearchBtn, _activeTab == AppView.Search);
        SetActive(NavRecentBtn, _activeTab == AppView.RecentlyWatched);
        SetActive(NavMyListBtn, _activeTab == AppView.MyList);
        SetActive(SettingsBtn, _activeTab == AppView.Settings);
    }

    private static void SetActive(Control c, bool active)
    {
        if (active)
        {
            if (!c.Classes.Contains("active")) c.Classes.Add("active");
        }
        else
        {
            c.Classes.Remove("active");
        }
    }

    // ── View-switching helpers ───────────────────────────────────────
    private void ShowSearchView()
    {
        // Search tab without active playback: search bar at the top,
        // ResultsPanel takes the full content width. The video stage
        // (Column 1) is collapsed entirely — it only ever appears
        // during full-screen playback (ShowPlaybackView).
        SearchBar.IsVisible = true;
        ContentGrid.IsVisible = true;
        ContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
        ResultsPanel.IsVisible = true;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
    }

    private void ShowRecentlyWatchedView()
    {
        // Hide everything else; the recently-watched card panel takes
        // over the main content area entirely.
        SearchBar.IsVisible = false;
        ContentGrid.IsVisible = false;
        RecentlyWatchedPanel.IsVisible = true;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        // Re-apply playing highlight in case cards just materialised.
        RefreshRecentPlayingHighlight();
    }

    private void ShowMyListView()
    {
        // Saved-for-later card grid. Same shell as RecentlyWatchedPanel
        // but bound to MyList.Items.
        SearchBar.IsVisible = false;
        ContentGrid.IsVisible = false;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = true;
        SettingsPanel.IsVisible = false;
        if (DataContext is MainWindowViewModel vm)
            MyListEmptyLabel.IsVisible = vm.MyList.Items.Count == 0;
        // Re-apply playing highlight — cards in MyListPanel only
        // materialise when the panel becomes visible, so the overlay
        // has to be re-applied after the layout pass that mounts them.
        // (Mirrors ShowRecentlyWatchedView.)
        RefreshRecentPlayingHighlight();
    }

    private void ShowSettingsView()
    {
        // Replace the entire content grid (search + results + video)
        // with the embedded Settings panel.
        SearchBar.IsVisible = false;
        ContentGrid.IsVisible = false;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
    }

    // Layout for the video stage during playback. Always full-width video
    // (search bar + results panel hidden), regardless of which tab the
    // user came from — playback feels uniform across tabs.
    private void ShowPlaybackView()
    {
        ContentGrid.IsVisible = true;
        RecentlyWatchedPanel.IsVisible = false;
        MyListPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        SearchBar.IsVisible = false;
        ContentGrid.ColumnDefinitions[0].Width = new GridLength(0);
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        ResultsPanel.IsVisible = false;
    }

    // Dispatches to the matching ShowXView for an AppView. Used from
    // both Back_Click (return to origin tab after playback ends) and
    // TogglePip_Click (mount the right tab behind the freshly-entered
    // PiP frame). Search is the fallback for unknown values.
    private void ShowViewFor(AppView view)
    {
        switch (view)
        {
            case AppView.RecentlyWatched: ShowRecentlyWatchedView(); break;
            case AppView.MyList: ShowMyListView(); break;
            case AppView.Settings: ShowSettingsView(); break;
            default: ShowSearchView(); break;
        }
    }

    // Inverse of ShowViewFor: looks at which top-level panel is
    // currently visible and reports the matching AppView. Used by
    // OnPlayRequested to capture the user's origin tab so Back_Click
    // can return there. Search is the fallback when nothing matches
    // (e.g. the search bar + content grid layout, before any tab
    // panel has been mounted).
    private AppView CurrentViewFromPanels()
    {
        if (RecentlyWatchedPanel.IsVisible) return AppView.RecentlyWatched;
        if (MyListPanel.IsVisible) return AppView.MyList;
        if (SettingsPanel.IsVisible) return AppView.Settings;
        return AppView.Search;
    }

    // ── Window resize via custom edges ───────────────────────────────
    // WindowDecorations="None" removes the OS resize frame, so we detect
    // edge clicks ourselves and forward them to BeginResizeDrag. Only
    // when windowed (not maximized / fullscreen).
    private const int ResizeBorder = 6;

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (GetResizeEdge(e.GetPosition(this)) is { } edge)
            {
                BeginResizeDrag(edge, e);
                e.Handled = true;
                return;
            }
        }

        // If Title or Year TextBox currently has focus and the user clicks
        // somewhere outside of it, move focus to the video area so the
        // TextBox is deselected (no caret, no highlight).
        if (FocusManager?.GetFocusedElement() is not TextBox box) return;
        if (box != TitleBox && box != YearBox) return;
        if (e.Source is Control clicked && IsDescendantOf(clicked, box)) return;

        VideoOverlayRoot?.Focus();
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (WindowState != WindowState.Normal) return;

        Cursor = GetResizeEdge(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South
                => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West or WindowEdge.East
                => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest or WindowEdge.SouthEast
                => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast or WindowEdge.SouthWest
                => new Cursor(StandardCursorType.TopRightCorner),
            _ => Cursor.Default,
        };
    }

    private WindowEdge? GetResizeEdge(Point pos)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var b = ResizeBorder;

        var onLeft = pos.X >= 0 && pos.X < b;
        var onRight = pos.X > w - b && pos.X <= w;
        var onTop = pos.Y >= 0 && pos.Y < b;
        var onBottom = pos.Y > h - b && pos.Y <= h;

        if (onTop && onLeft) return WindowEdge.NorthWest;
        if (onTop && onRight) return WindowEdge.NorthEast;
        if (onBottom && onLeft) return WindowEdge.SouthWest;
        if (onBottom && onRight) return WindowEdge.SouthEast;
        if (onTop) return WindowEdge.North;
        if (onBottom) return WindowEdge.South;
        if (onLeft) return WindowEdge.West;
        if (onRight) return WindowEdge.East;
        return null;
    }

    private static bool IsDescendantOf(Control? child, Control? ancestor)
    {
        if (ancestor is null) return false;
        for (var c = child; c is not null; c = c.Parent as Control)
        {
            if (ReferenceEquals(c, ancestor)) return true;
        }
        return false;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _hideUiTimer.Stop();
        // Save the current playback position before stopping the player —
        // Stop() clears Time to 0 so a later save would overwrite with
        // zero.
        SaveCurrentPosition();
        _player.Stop();
        _currentMedia?.Dispose();
        _player.Dispose();
        _libvlc.Dispose();
    }
}
