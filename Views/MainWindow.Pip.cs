using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// Picture-in-picture: while a video is playing and the user navigates
// to a tab without a video stage (or to a different layout), the video
// shrinks into PipFrame — a draggable, resizable mini-player anchored
// bottom-right by default. The full-size video chrome (gradients, back
// arrow, title, transport bar) is hidden during PiP; the mini-transport
// inside PipFrame mirrors the player state via SetPlayPauseIcons /
// SetSliderFromPlayer.
public partial class MainWindow
{
    // ── State / constants ──────────────────────────────────────────────
    private bool _isPipMode;
    private bool _pipDragging;
    private Point _pipDragStart;
    private Thickness _pipMarginAtDragStart;
    private bool _pipResizing;
    private Point _pipResizeStart;
    private Size _pipSizeAtResizeStart;
    private Thickness _pipMarginAtResizeStart;
    private string _pipResizeCorner = "BR";
    private DispatcherTimer? _pipHideUiTimer;
    private const double PipPaddingFromEdges = 16;
    private const double PipWidth = 320;
    private const double PipHeight = 180;
    private const double PipAspect = PipWidth / PipHeight;

    // ── Enter / exit ───────────────────────────────────────────────────
    private void EnterPipMode()
    {
        if (_isPipMode || _currentVideoResult is null) return;
        // Drop out of fullscreen first so the PiP frame floats over a
        // normal-windowed app instead of leaving the OS in fullscreen
        // with a tiny mini-player on top of an otherwise blank screen.
        // Covers every entry path: PiP toolbar button + sidebar tab
        // clicks that auto-PiP a playing video.
        if (WindowState == WindowState.FullScreen) ToggleFullscreen();
        _isPipMode = true;
        if (DataContext is MainWindowViewModel vmPip) vmPip.IsPipActive = true;

        // Hide the full-size video chrome — only the PiP frame is visible.
        TopGradient.IsVisible = false;
        BottomGradient.IsVisible = false;
        BackButton.IsVisible = false;
        PipButton.IsVisible = false;
        CurrentMovieTitle.IsVisible = false;
        TransportBarContainer.IsVisible = false;

        // Always start PiP at the default minimum size — don't carry the
        // previous session's resize across opens.
        PipFrame.Width = PipWidth;
        PipFrame.Height = PipHeight;

        // Position bottom-right with edge padding. Defer to next layout
        // pass if PipHost hasn't been measured yet.
        PipFrame.IsVisible = true;
        PositionPipBottomRight();

        PulsePipUi();
    }

    private void ExitPipMode()
    {
        if (!_isPipMode) return;
        _isPipMode = false;
        if (DataContext is MainWindowViewModel vmPip) vmPip.IsPipActive = false;
        PipFrame.IsVisible = false;
        SetPipChromeVisible(false);
        _pipHideUiTimer?.Stop();

        // Bring the full-size chrome back if a video is still loaded.
        if (_currentVideoResult is not null)
        {
            TopGradient.IsVisible = true;
            BottomGradient.IsVisible = true;
            BackButton.IsVisible = true;
            PipButton.IsVisible = true;
            CurrentMovieTitle.IsVisible = true;
            TransportBarContainer.IsVisible = true;
        }
    }

    private void PositionPipBottomRight()
    {
        var hostW = PipHost.Bounds.Width;
        var hostH = PipHost.Bounds.Height;
        if (hostW <= 0 || hostH <= 0)
        {
            // Layout pass hasn't happened yet — try again after layout.
            Dispatcher.UIThread.Post(PositionPipBottomRight, DispatcherPriority.Loaded);
            return;
        }
        var left = Math.Max(PipPaddingFromEdges, hostW - PipWidth - PipPaddingFromEdges);
        var top = Math.Max(PipPaddingFromEdges, hostH - PipHeight - PipPaddingFromEdges);
        PipFrame.Margin = new Thickness(left, top, 0, 0);
    }

    // PipHost resized (window grew, shrank, sidebar tab changed, etc.) —
    // reposition PipFrame so it keeps the SAME distance from the
    // bottom-right edge it had before the resize. Without this, the
    // frame stays anchored to its old absolute top-left margin: shrink
    // the window and the frame clips off the edge; grow it and the
    // frame drifts away from the corner.
    private void OnPipHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_isPipMode) return;
        if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;
        var frameW = PipFrame.Bounds.Width;
        var frameH = PipFrame.Bounds.Height;
        if (frameW <= 0 || frameH <= 0) return;

        // Distance from the right / bottom edges before the resize.
        var distFromRight = e.PreviousSize.Width - PipFrame.Margin.Left - frameW;
        var distFromBottom = e.PreviousSize.Height - PipFrame.Margin.Top - frameH;

        var newLeft = e.NewSize.Width - distFromRight - frameW;
        var newTop = e.NewSize.Height - distFromBottom - frameH;

        // Clamp so the frame stays fully within bounds with the standard
        // edge padding — protects against the new size being smaller
        // than the previous distance-from-corner.
        var maxLeft = Math.Max(PipPaddingFromEdges,
            e.NewSize.Width - frameW - PipPaddingFromEdges);
        var maxTop = Math.Max(PipPaddingFromEdges,
            e.NewSize.Height - frameH - PipPaddingFromEdges);
        newLeft = Math.Clamp(newLeft, PipPaddingFromEdges, maxLeft);
        newTop = Math.Clamp(newTop, PipPaddingFromEdges, maxTop);

        PipFrame.Margin = new Thickness(newLeft, newTop, 0, 0);
    }

    // ── Toggle / restore / close ───────────────────────────────────────
    // Single PiP toggle wired to the transport-bar ToggleButton. If
    // playback is in PiP, restore the full-size player; otherwise drop
    // into PiP and show whichever tab is currently active behind it.
    private void TogglePip_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentVideoResult is null) return;
        if (_isPipMode)
        {
            ExitPipMode();
            ShowPlaybackView();
            return;
        }
        EnterPipMode();
        ShowViewFor(_activeTab);
    }

    private void PipRestore_Click(object? sender, RoutedEventArgs e)
    {
        ExitPipMode();
        ShowPlaybackView();
    }

    private void PipPlayPause_Click(object? sender, RoutedEventArgs e)
        => PlayPause_Click(sender, e);

    // Tear down playback entirely from the PiP — same effect as the
    // full-size BackButton, but anchored to the user's *current* tab,
    // not wherever the video originally started. Without this, closing
    // PiP from Recently watched while the video had started in Search
    // would briefly switch the layout back to Search → visible flicker.
    private void PipClose_Click(object? sender, RoutedEventArgs e)
    {
        // Stay on the tab the user is currently viewing.
        _originView = _activeTab;
        ExitPipMode();
        Dispatcher.UIThread.Post(() => Back_Click(sender, e));
    }

    // Shared "is this card already in PiP playback?" branch used by
    // both RecentWatch_Click and MyListEntry_Click. Returns true if
    // PiP was restored (caller should bail out instead of restarting
    // playback). URL match is case-insensitive and ignores a trailing
    // slash so trivial variants between the saved entry and the active
    // video result still resolve to the same item.
    private bool TryRestorePipForPageUrl(string? pageUrl, object? sender, RoutedEventArgs e)
    {
        if (!_isPipMode
            || _currentVideoResult is null
            || !PageUrlEquals(_currentVideoResult.PageUrl, pageUrl)) return false;
        PipRestore_Click(sender, e);
        // The user clicked "play" on a card matching the active PiP
        // movie — they expect playback to resume, not just the mini
        // player to expand into the full-size view while still paused.
        // Only nudge a paused player; if it's already playing, ended,
        // or buffering, let the existing state stand.
        if (_player.State == VLCState.Paused) _player.Play();
        e.Handled = true;
        return true;
    }

    private static bool PageUrlEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    // ── PiP drag handling ──────────────────────────────────────────────
    // PointerPressed on empty PiP space starts a drag (clicks on the
    // restore button / play / pause / slider must NOT start a drag, so
    // we walk the source's ancestors and bail on any interactive child).
    private void PipFrame_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && IsInsidePipControl(v)) return;
        if (!e.GetCurrentPoint(PipFrame).Properties.IsLeftButtonPressed) return;
        _pipDragging = true;
        _pipDragStart = e.GetPosition(PipHost);
        _pipMarginAtDragStart = PipFrame.Margin;
        e.Pointer.Capture(PipFrame);
    }

    private void PipFrame_PointerMoved(object? sender, PointerEventArgs e)
    {
        PulsePipUi();
        if (!_pipDragging) return;

        var current = e.GetPosition(PipHost);
        var dx = current.X - _pipDragStart.X;
        var dy = current.Y - _pipDragStart.Y;

        var hostW = PipHost.Bounds.Width;
        var hostH = PipHost.Bounds.Height;
        var maxLeft = Math.Max(PipPaddingFromEdges,
            hostW - PipFrame.Bounds.Width - PipPaddingFromEdges);
        var maxTop = Math.Max(PipPaddingFromEdges,
            hostH - PipFrame.Bounds.Height - PipPaddingFromEdges);

        var newLeft = Math.Clamp(_pipMarginAtDragStart.Left + dx, PipPaddingFromEdges, maxLeft);
        var newTop = Math.Clamp(_pipMarginAtDragStart.Top + dy, PipPaddingFromEdges, maxTop);
        PipFrame.Margin = new Thickness(newLeft, newTop, 0, 0);
    }

    private void PipFrame_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pipDragging) return;
        _pipDragging = false;
        e.Pointer.Capture(null);
    }

    // ── PiP resize ─────────────────────────────────────────────────────
    // Eight grips (four corners + four edges) share these handlers. Each
    // grip's Tag identifies which side(s) it controls; the OPPOSITE edge
    // stays anchored, so the frame grows / shrinks toward the dragged
    // edge. Width and height resize independently — corners change both
    // axes, edges only one — letting the user squash or stretch the
    // PiP frame freely.
    private void PipResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip) return;
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        _pipResizing = true;
        _pipResizeStart = e.GetPosition(PipHost);
        _pipSizeAtResizeStart = new Size(PipFrame.Bounds.Width, PipFrame.Bounds.Height);
        _pipMarginAtResizeStart = PipFrame.Margin;
        _pipResizeCorner = (grip.Tag as string) ?? "BR";
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void PipResizeGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pipResizing) return;
        var current = e.GetPosition(PipHost);
        var dx = current.X - _pipResizeStart.X;
        var dy = current.Y - _pipResizeStart.Y;

        // Per-axis sign by grip tag. +1 = grow with positive delta (right
        // / bottom side), -1 = grow with negative delta (left / top side
        // — opposite edge stays anchored), 0 = axis is locked (edge grip
        // that doesn't touch this axis).
        int xSign = _pipResizeCorner switch
        {
            "TR" or "BR" or "R" => 1,
            "TL" or "BL" or "L" => -1,
            _ => 0,
        };
        int ySign = _pipResizeCorner switch
        {
            "BR" or "BL" or "B" => 1,
            "TR" or "TL" or "T" => -1,
            _ => 0,
        };

        var newWidth = _pipSizeAtResizeStart.Width + (xSign == 0 ? 0 : dx * xSign);
        var newHeight = _pipSizeAtResizeStart.Height + (ySign == 0 ? 0 : dy * ySign);

        // Max bounds depend on which edge stays fixed. Right-side grip ⇒
        // left edge is anchored, so the frame can grow to the right edge
        // of PipHost. Left-side grip ⇒ right edge is anchored. Locked
        // axis keeps the starting size as both min and max.
        double maxWidth = xSign switch
        {
            1 => Math.Max(PipWidth,
                PipHost.Bounds.Width - _pipMarginAtResizeStart.Left - PipPaddingFromEdges),
            -1 => Math.Max(PipWidth,
                _pipMarginAtResizeStart.Left + _pipSizeAtResizeStart.Width - PipPaddingFromEdges),
            _ => _pipSizeAtResizeStart.Width,
        };
        double maxHeight = ySign switch
        {
            1 => Math.Max(PipHeight,
                PipHost.Bounds.Height - _pipMarginAtResizeStart.Top - PipPaddingFromEdges),
            -1 => Math.Max(PipHeight,
                _pipMarginAtResizeStart.Top + _pipSizeAtResizeStart.Height - PipPaddingFromEdges),
            _ => _pipSizeAtResizeStart.Height,
        };

        var minWidth = xSign == 0 ? _pipSizeAtResizeStart.Width : PipWidth;
        var minHeight = ySign == 0 ? _pipSizeAtResizeStart.Height : PipHeight;
        newWidth = Math.Clamp(newWidth, minWidth, maxWidth);
        newHeight = Math.Clamp(newHeight, minHeight, maxHeight);

        // Anchor the opposite edge: Margin.Left/Top only changes for grips
        // on the left / top side (sign == -1).
        var newLeft = _pipMarginAtResizeStart.Left;
        var newTop = _pipMarginAtResizeStart.Top;
        if (xSign == -1)
        {
            var rightEdge = _pipMarginAtResizeStart.Left + _pipSizeAtResizeStart.Width;
            newLeft = rightEdge - newWidth;
        }
        if (ySign == -1)
        {
            var bottomEdge = _pipMarginAtResizeStart.Top + _pipSizeAtResizeStart.Height;
            newTop = bottomEdge - newHeight;
        }

        PipFrame.Width = newWidth;
        PipFrame.Height = newHeight;
        PipFrame.Margin = new Thickness(newLeft, newTop, 0, 0);
        PulsePipUi();
        e.Handled = true;
    }

    private void PipResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pipResizing) return;
        _pipResizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Hover / chrome auto-hide ───────────────────────────────────────
    private void PipFrame_PointerEntered(object? sender, PointerEventArgs e)
        => PulsePipUi();

    private void PipFrame_PointerExited(object? sender, PointerEventArgs e)
    {
        // Don't hide chrome mid-drag — the grip would disappear under the
        // cursor and the resize would silently end.
        if (_pipResizing) return;
        // Hide controls immediately when the pointer leaves so the PiP
        // frame goes back to a clean video-only look without waiting for
        // the 3-second timeout.
        _pipHideUiTimer?.Stop();
        SetPipChromeVisible(false);
    }

    // Walk the source's ancestors up to PipFrame and bail on any
    // interactive child (Button / Slider / resize grip). Used by the
    // PipFrame_PointerPressed handler so clicks on the close / restore
    // buttons, the play / pause button, the position slider, or any
    // resize grip do NOT start a frame drag.
    private bool IsInsidePipControl(Visual origin)
    {
        Visual? cur = origin;
        while (cur is not null && cur != PipFrame)
        {
            if (cur is Button || cur is Slider) return true;
            if (cur == PipResizeGripTL || cur == PipResizeGripTR
                || cur == PipResizeGripBL || cur == PipResizeGripBR) return true;
            cur = cur.GetVisualParent();
        }
        return false;
    }

    private void SetPipResizeGripsVisible(bool visible)
    {
        PipResizeGripTL.IsVisible = visible;
        PipResizeGripTR.IsVisible = visible;
        PipResizeGripBL.IsVisible = visible;
        PipResizeGripBR.IsVisible = visible;
        PipResizeGripT.IsVisible = visible;
        PipResizeGripB.IsVisible = visible;
        PipResizeGripL.IsVisible = visible;
        PipResizeGripR.IsVisible = visible;
    }

    // Show / hide the full PiP chrome cluster (top buttons + bottom
    // transport + the two darkening gradients + the resize grips).
    // Toggled together by enter/exit, the auto-hide pulse, the timer
    // tick, and the pointer-leave handler — all four sites used to
    // open-code the same five IsVisible writes plus a SetPipResize-
    // GripsVisible call.
    private void SetPipChromeVisible(bool visible)
    {
        PipRestoreBtn.IsVisible = visible;
        PipCloseBtn.IsVisible = visible;
        PipTransport.IsVisible = visible;
        PipTopGradient.IsVisible = visible;
        PipBottomGradient.IsVisible = visible;
        SetPipResizeGripsVisible(visible);
    }

    // Auto-hide UI: show the restore button + mini transport while there's
    // pointer activity, hide them ~3s after the pointer goes idle.
    private void PulsePipUi()
    {
        if (!_isPipMode) return;
        SetPipChromeVisible(true);
        _pipHideUiTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pipHideUiTimer.Tick -= OnPipHideUiTick;
        _pipHideUiTimer.Tick += OnPipHideUiTick;
        _pipHideUiTimer.Stop();
        _pipHideUiTimer.Start();
    }

    private void OnPipHideUiTick(object? sender, EventArgs e)
    {
        _pipHideUiTimer?.Stop();
        if (!_isPipMode) return;
        SetPipChromeVisible(false);
    }
}
