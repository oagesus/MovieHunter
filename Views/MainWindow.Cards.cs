using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// Recently watched + My list card visuals and click handling. Shared
// between both panels because each card's data template uses the same
// Button.recent-card class — the Tag on the Button is either a
// RecentWatch or a MyListEntry, and the helpers below pivot on type.
public partial class MainWindow
{
    // ── Card click handlers ──────────────────────────────────────────
    // Search-result row's Play button: starts (or resumes) playback for
    // the result whose Tag points at the clicked button.
    private void PlayResult_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoResult vr }
            && DataContext is MainWindowViewModel vm)
        {
            // Same movie as the one currently in PiP? Restore the
            // full-size player and resume instead of reloading the
            // stream — same shortcut Recently watched / My list use.
            if (TryRestorePipForPageUrl(vr.PageUrl, sender, e)) return;
            // Float existing entries to the top of both lists right
            // now so the user sees the reorder before the ~500 ms
            // stream-URL extraction completes. No-op for entries that
            // aren't in either list yet — they'll be inserted by
            // OnPlayRequested as part of normal play-start handling.
            vm.Recent.MoveToTopAndSave(vr);
            vm.MyList.MoveToTopAndSave(vr);
            // Resume from saved position if we've watched this before,
            // otherwise start from zero.
            var startMs = vm.Recent.Find(vr.PageUrl)?.PositionMs ?? 0;
            vm.SelectedResult = vr;
            _ = vm.PlayResultAsync(vr, startMs);
        }
    }

    private void RecentWatch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentWatch rw }
            && DataContext is MainWindowViewModel vm)
        {
            // If the user clicked the card for the movie that's already
            // playing (in PiP mode while they're browsing Recently watched),
            // don't restart it — just bring the video back to full size.
            if (TryRestorePipForPageUrl(rw.PageUrl, sender, e)) return;
            // Optimistic highlight: light up the clicked card with
            // "Currently playing" right away while the stream URL is
            // being extracted in the background.
            _pendingPlayingPageUrl = string.IsNullOrEmpty(rw.PageUrl) ? null : rw.PageUrl;
            // Float to top immediately so the card moves before the
            // ~500 ms stream-URL extraction → OnPlayRequested →
            // UpsertAndSave round-trip would otherwise take to do it.
            // Also float the matching My-list entry, since both lists
            // sort by most-recent play.
            var vr = rw.ToVideoResult();
            vm.Recent.MoveToTopAndSave(vr);
            vm.MyList.MoveToTopAndSave(vr);
            ApplyRecentPlayingHighlightNow();
            _ = vm.PlayRecentAsync(rw);
        }
        // Stop the routed Click from bubbling to the parent recent-card
        // when the inline play chip is the source — without this the
        // card's own RecentWatch_Click would fire a second time.
        e.Handled = true;
    }

    // Click on a card in the My-List panel — same flow as the
    // recently-watched click, except we have a MyListEntry to convert.
    // Picks the higher of the entry's own saved position and any
    // matching Recently-watched entry's position so playback always
    // resumes from the latest known offset, even if the user added the
    // movie to My-list AFTER they started watching it.
    private void MyListEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MyListEntry entry }
            || DataContext is not MainWindowViewModel vm) return;
        if (TryRestorePipForPageUrl(entry.PageUrl, sender, e)) return;
        _pendingPlayingPageUrl = string.IsNullOrEmpty(entry.PageUrl) ? null : entry.PageUrl;
        // Float to top in both lists immediately — same reasoning as
        // RecentWatch_Click: don't wait for the ~500 ms stream-URL
        // round-trip to reorder the grid.
        var vr = entry.ToVideoResult();
        vm.MyList.MoveToTopAndSave(vr);
        vm.Recent.MoveToTopAndSave(vr);
        ApplyRecentPlayingHighlightNow();
        var resumeMs = Math.Max(entry.PositionMs, vm.Recent.Find(entry.PageUrl)?.PositionMs ?? 0);
        _ = vm.PlayResultAsync(vr, resumeMs);
        e.Handled = true;
    }

    // The chip on a recently-watched / search-result card — toggles
    // membership in MyList and syncs IsInMyList across every visible
    // surface so all chips referring to the same PageUrl flip together.
    private void ToggleMyList_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainWindowViewModel vm) return;
        VideoResult? source = btn.Tag switch
        {
            VideoResult v => v,
            RecentWatch rw => rw.ToVideoResult(),
            MyListEntry m => m.ToVideoResult(),
            _ => null,
        };
        if (source is null || string.IsNullOrEmpty(source.PageUrl)) return;
        // If this movie is already in Recently watched, seed the new
        // My-list entry with the saved position so its progress bar (and
        // resume-from-saved-time) work immediately.
        var existingProgress = vm.Recent.Find(source.PageUrl);
        // Pin the currently-playing movie at index 0 so a fresh
        // addition slots in after it. Without this, adding a movie
        // while another one is playing demotes the playing one to
        // index 1 — the "currently watching" entry should stay on top.
        var nowSaved = vm.MyList.Toggle(
            source,
            existingProgress?.PositionMs ?? 0,
            existingProgress?.LengthMs ?? 0,
            _currentVideoResult?.PageUrl);
        SyncIsInMyList(source.PageUrl, nowSaved);
        e.Handled = true;
    }

    // The chip in the My-list panel — same as ToggleMyList_Click but
    // always removes (the panel only shows saved items).
    private void MyListRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MyListEntry entry }
            || DataContext is not MainWindowViewModel vm) return;
        if (string.IsNullOrEmpty(entry.PageUrl)) return;
        vm.MyList.Remove(entry.PageUrl);
        SyncIsInMyList(entry.PageUrl, false);
        if (MyListEmptyLabel is not null)
            MyListEmptyLabel.IsVisible = vm.MyList.Items.Count == 0;
        e.Handled = true;
    }

    // Mirror MyList membership onto Recent + search Results so all
    // chips referring to the same movie reflect the new state.
    private void SyncIsInMyList(string pageUrl, bool isSaved)
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrEmpty(pageUrl)) return;
        foreach (var rw in vm.Recent.Items)
            if (rw.PageUrl == pageUrl) rw.IsInMyList = isSaved;
        foreach (var vr in vm.Results)
            if (vr.PageUrl == pageUrl) vr.IsInMyList = isSaved;
    }

    // Bulk-sync after load — runs once OnDataContextChanged, and again
    // when MyList.Items changes, so existing Recent + Results entries
    // pick up the saved-state on app start.
    private void RefreshAllIsInMyListFromMyList()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        foreach (var rw in vm.Recent.Items)
            rw.IsInMyList = vm.MyList.Contains(rw.PageUrl);
        foreach (var vr in vm.Results)
            vr.IsInMyList = vm.MyList.Contains(vr.PageUrl);
    }

    // ── Card hover / playing visual state ────────────────────────────
    // Title-strip text inside a card consumes its own pointer presses so
    // a click on the title/source doesn't bubble up to the parent card
    // and start playback. Clicks on the inline action chips are
    // unaffected (their own buttons handle them first).
    private void TitleStrip_PointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void RecentCard_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Button btn) return;
        ApplyRecentCardVisuals(btn, hovered: true);
    }

    private void RecentCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Button btn) return;
        // Don't strip the highlight on the playing card — its hover state
        // is "always on" so users can spot it at a glance without hovering.
        ApplyRecentCardVisuals(btn, hovered: false);
    }

    // Shared visual update for a recent card. The "currently-playing"
    // card keeps its accent border and shows the inline badge regardless
    // of pointer position; other cards follow normal hover-on / hover-off
    // rules for the accent border alone.
    private void ApplyRecentCardVisuals(Button card, bool hovered)
    {
        var isPlaying = IsRecentCardPlaying(card);
        var show = hovered || isPlaying;

        if (FindBorderByClass(card, "poster") is Border poster)
            poster.BorderBrush = show
                ? ResolveAccentBrush(card)
                : Brushes.Transparent;

        // Scrim darkens the poster image on hover OR when playing —
        // gives every hovered card the same "active" look as the
        // currently-playing one.
        if (FindBorderByClass(card, "playing-scrim") is Border scrim)
            scrim.IsVisible = show;

        // Badge shows in both states: "Currently playing" stays visible
        // while the card represents the active video; on hover (when
        // not playing) it flips to "Continue playing" so the user knows
        // a click will resume from the saved position.
        if (FindByClass(card, "playing-badge") is TextBlock badge)
        {
            badge.IsVisible = show;
            badge.Text = isPlaying ? "Currently playing" : "Continue playing";
        }
    }

    // True if this card represents the movie currently being played.
    // Recognises both Recently watched (RecentWatch) and My list
    // (MyListEntry) tags; pending click takes precedence so a freshly
    // tapped card lights up immediately, even before the player has
    // actually started.
    private bool IsRecentCardPlaying(Button card)
    {
        var pageUrl = card.Tag switch
        {
            RecentWatch rw => rw.PageUrl,
            MyListEntry me => me.PageUrl,
            _ => null,
        };
        if (string.IsNullOrEmpty(pageUrl)) return false;
        var activeUrl = _pendingPlayingPageUrl ?? _currentVideoResult?.PageUrl;
        // Normalised compare — same rule used by PageUrlEquals so tiny
        // case / trailing-slash variants between the saved entry and
        // the active video result still highlight the right card.
        return PageUrlEquals(pageUrl, activeUrl);
    }

    // Sweeps every materialised recent-card across BOTH the Recently
    // watched and My list panels and re-applies its visual state
    // synchronously. Use this when the cards are already in the visual
    // tree (e.g. tearing down the highlight on close).
    private void ApplyRecentPlayingHighlightNow()
    {
        SweepRecentCards(RecentlyWatchedPanel);
        SweepRecentCards(MyListPanel);
    }

    private void SweepRecentCards(Control? root)
    {
        if (root is null) return;
        foreach (var d in root.GetVisualDescendants())
        {
            if (d is Button btn && btn.Classes.Contains("recent-card"))
                ApplyRecentCardVisuals(btn, hovered: false);
        }
    }

    // Deferred wrapper — needed when the call follows a CollectionChanged
    // (a new container needs a layout pass to materialise before we can
    // find it in the visual tree).
    private void RefreshRecentPlayingHighlight()
        => Dispatcher.UIThread.Post(ApplyRecentPlayingHighlightNow);

    // ── Visual-tree helpers used by the card visuals ─────────────────
    private static Border? FindBorderByClass(Control root, string className)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is Border b && b.Classes.Contains(className)) return b;
        return null;
    }

    // Generic helper — finds the first descendant Control that has the
    // given class. Used by the recent/my-list card visuals to locate
    // the playing-badge regardless of its concrete control type.
    private static Control? FindByClass(Control root, string className)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is Control c && c.Classes.Contains(className)) return c;
        return null;
    }

    // FluentTheme's accent resource naming has changed across versions
    // (SystemAccentColorBrush vs. AccentFillColorDefaultBrush vs. just
    // deriving a brush from SystemAccentColor). Try each in order, using
    // the control's actual ThemeVariant so variant-specific overrides
    // (Dracula / Netflix / etc.) resolve correctly. Fall back to a
    // hardcoded color so the hover is never invisible.
    private static IBrush ResolveAccentBrush(Control origin)
    {
        var variant = origin.ActualThemeVariant;
        string[] brushKeys = { "SystemAccentColorBrush", "AccentFillColorDefaultBrush" };
        foreach (var key in brushKeys)
        {
            if (origin.TryFindResource(key, variant, out var v) && v is IBrush b)
                return b;
        }
        string[] colorKeys = { "SystemAccentColor", "AccentFillColorDefault" };
        foreach (var key in colorKeys)
        {
            if (origin.TryFindResource(key, variant, out var v) && v is Color c)
                return new SolidColorBrush(c);
        }
        return new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14));
    }

    // Search-result row's "open in browser" button — launches the
    // result's page URL in the default browser. Quietly swallows
    // failures (browser unavailable, malformed URL etc.).
    private void OpenSource_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoResult vr } && !string.IsNullOrWhiteSpace(vr.PageUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vr.PageUrl,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // swallow; browser launch failures are not fatal
            }
        }
    }
}
