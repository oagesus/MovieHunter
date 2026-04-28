using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MovieHunter.Views;

// Custom scrollbar interaction. The scrollbar template (defined in
// MainWindow.axaml) wires PointerPressed / PointerMoved / PointerReleased
// on the inner Grid to these handlers, giving the scrollbar two
// behaviors that Avalonia's default template doesn't support:
//   • Click anywhere on the track lane → jump the thumb (centered on
//     the click), then start a drag from there.
//   • Click within the thumb's current extent (or its hit-test gutter)
//     → no jump; drag begins from the existing position with a 1:1
//     mapping between cursor movement and thumb movement.
public partial class MainWindow
{
    private ScrollBar? _trackDragScrollBar;
    private Track? _trackDragTrack;
    private Thumb? _trackDragThumb;
    // Distance between the initial click coordinate and the thumb's
    // top edge (in track-local coords). Drag updates compute the new
    // thumb top as cursor − this offset, so the drag tracks 1:1 just
    // like Avalonia's native thumb drag.
    private double _trackDragOffsetFromThumbTop;

    private void OnScrollBarTrackClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control source) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        var scrollBar = source.FindAncestorOfType<ScrollBar>();
        if (scrollBar is null) return;

        Track? track = null;
        Thumb? thumb = null;
        foreach (var d in scrollBar.GetVisualDescendants())
        {
            if (track is null && d is Track t) track = t;
            else if (thumb is null && d is Thumb th) thumb = th;
            if (track is not null && thumb is not null) break;
        }
        if (track is null || thumb is null) return;

        var isVertical = scrollBar.Orientation == Avalonia.Layout.Orientation.Vertical;
        var trackSize = isVertical ? track.Bounds.Height : track.Bounds.Width;
        var thumbSize = isVertical ? thumb.Bounds.Height : thumb.Bounds.Width;
        var usable = trackSize - thumbSize;
        if (usable <= 0) return;

        var range = scrollBar.Maximum - scrollBar.Minimum;
        var posInTrack = e.GetPosition(track);
        var coord = isVertical ? posInTrack.Y : posInTrack.X;

        // Where is the thumb sitting right now (top edge in track-local
        // coords)? We need this to decide whether the click landed
        // within the thumb's vertical/horizontal extent.
        var currentFraction = range > 0
            ? (scrollBar.Value - scrollBar.Minimum) / range
            : 0;
        var thumbTop = currentFraction * usable;
        var thumbBottom = thumbTop + thumbSize;

        // Click on (or visually next to) the thumb at the same Y/X →
        // do NOT jump. Mirrors native thumb-drag behavior: the thumb
        // stays put and starts a drag from its current position.
        // Click outside the thumb's current extent → jump so the
        // thumb's center lands on the click, then drag from there.
        if (coord < thumbTop || coord > thumbBottom)
        {
            var desiredTop = coord - thumbSize / 2.0;
            var newFraction = Math.Clamp(desiredTop / usable, 0.0, 1.0);
            scrollBar.Value = scrollBar.Minimum + newFraction * range;
            thumbTop = newFraction * usable;
        }

        _trackDragOffsetFromThumbTop = coord - thumbTop;
        _trackDragScrollBar = scrollBar;
        _trackDragTrack = track;
        _trackDragThumb = thumb;
        thumb.Classes.Add("scrolling");
        e.Pointer.Capture(source);
        e.Handled = true;
    }

    private void OnScrollBarTrackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_trackDragScrollBar is null
            || _trackDragTrack is null
            || _trackDragThumb is null) return;

        var isVertical = _trackDragScrollBar.Orientation == Avalonia.Layout.Orientation.Vertical;
        var trackSize = isVertical ? _trackDragTrack.Bounds.Height : _trackDragTrack.Bounds.Width;
        var thumbSize = isVertical ? _trackDragThumb.Bounds.Height : _trackDragThumb.Bounds.Width;
        var usable = trackSize - thumbSize;
        if (usable <= 0) return;

        var posInTrack = e.GetPosition(_trackDragTrack);
        var coord = isVertical ? posInTrack.Y : posInTrack.X;

        var newThumbTop = coord - _trackDragOffsetFromThumbTop;
        var fraction = Math.Clamp(newThumbTop / usable, 0.0, 1.0);
        var range = _trackDragScrollBar.Maximum - _trackDragScrollBar.Minimum;
        _trackDragScrollBar.Value = _trackDragScrollBar.Minimum + fraction * range;
        e.Handled = true;
    }

    private void OnScrollBarTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_trackDragScrollBar is null) return;
        _trackDragThumb?.Classes.Remove("scrolling");
        _trackDragThumb = null;
        _trackDragTrack = null;
        _trackDragScrollBar = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
