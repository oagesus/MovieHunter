using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieHunter.Services;

namespace MovieHunter.Views;

// Settings panel: NumericUpDown for results-per-source, TMDB API key
// validation, and the click-anywhere-to-defocus behavior on the panel
// background. The panel itself lives in MainWindow.axaml under
// SettingsPanel; these handlers are wired by name from there.
public partial class MainWindow
{
    // ── NumericUpDown / TMDB key field ───────────────────────────────
    private void ResultsPerSourceBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Enter commits the value (which happens automatically on blur)
        // and moves focus back to the panel so the field no longer looks
        // active.
        if (e.Key == Key.Enter)
        {
            SourcesFlyoutPanel?.Focus();
            e.Handled = true;
        }
    }

    private void ResultsPerSourceBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        // Clearing the field leaves Value = null, which the int-typed
        // binding target can't accept — triggering a red validation
        // border. Coerce back to the minimum so the field always holds
        // a valid value.
        if (sender is NumericUpDown nud && nud.Value is null)
            nud.Value = nud.Minimum;
    }

    private void TmdbKeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Enter just defocuses the field; validation only runs when the
        // user clicks the Check button.
        if (e.Key == Key.Enter)
        {
            SourcesFlyoutPanel?.Focus();
            e.Handled = true;
        }
    }

    // ── TMDB key validation ──────────────────────────────────────────
    private void ValidateTmdbKey_Click(object? sender, RoutedEventArgs e)
        => ValidateTmdbKey();

    // HTTP client reused across validation calls. Short timeout — we want
    // fast pass/fail feedback, not a hang.
    private readonly HttpClient _tmdbValidationHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    // Incrementing counter so late-arriving validation responses from
    // outdated keystrokes don't overwrite the current status.
    private int _tmdbValidationGeneration;

    private async void ValidateTmdbKey()
    {
        var key = TmdbKeyBox?.Text?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            SetTmdbKeyStatus("", null);
            return;
        }

        var generation = ++_tmdbValidationGeneration;
        SetTmdbKeyStatus("…", Brushes.Gray);

        bool ok;
        try
        {
            var client = new TmdbClient(_tmdbValidationHttp);
            ok = await client.ValidateKeyAsync(key, default);
        }
        catch { ok = false; }

        // Drop this result if another validation was kicked off in the meantime.
        if (generation != _tmdbValidationGeneration) return;

        if (ok)
            SetTmdbKeyStatus("✓", Brushes.LimeGreen);
        else
            SetTmdbKeyStatus("✗", Brushes.Tomato);
    }

    // Auto-revert timer: after a validation result is shown for ~3s, the
    // result label disappears and the Check button comes back.
    private DispatcherTimer? _tmdbResultRevertTimer;

    private void SetTmdbKeyStatus(string glyph, IBrush? brush)
    {
        // Legacy text-glyph indicator (still updated for any consumers
        // that rely on it; the visible UI is the Check button below).
        if (TmdbKeyStatusIcon is not null)
        {
            TmdbKeyStatusIcon.Text = glyph;
            if (brush is not null)
                TmdbKeyStatusIcon.Foreground = brush;
        }

        // Cancel any pending revert from a prior validation.
        _tmdbResultRevertTimer?.Stop();

        if (glyph == "✓" || glyph == "✗")
        {
            // Show result label in place of the Check button.
            if (TmdbCheckIconHost is not null) TmdbCheckIconHost.IsVisible = glyph == "✓";
            if (TmdbInvalidIconHost is not null) TmdbInvalidIconHost.IsVisible = glyph == "✗";
            if (TmdbResultText is not null)
            {
                TmdbResultText.Text = glyph == "✓" ? "Valid" : "Not valid";
                TmdbResultText.Foreground = glyph == "✓"
                    ? new SolidColorBrush(Color.FromRgb(0x3F, 0xAA, 0x63))
                    : new SolidColorBrush(Color.FromRgb(0xD9, 0x54, 0x4D));
            }
            if (TmdbResultLabel is not null) TmdbResultLabel.IsVisible = true;
            if (TmdbValidateBtn is not null) TmdbValidateBtn.IsVisible = false;

            _tmdbResultRevertTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _tmdbResultRevertTimer.Tick -= OnTmdbResultRevert;
            _tmdbResultRevertTimer.Tick += OnTmdbResultRevert;
            _tmdbResultRevertTimer.Start();
        }
        else
        {
            // Idle / loading / empty state — show the Check button.
            if (TmdbResultLabel is not null) TmdbResultLabel.IsVisible = false;
            if (TmdbValidateBtn is not null) TmdbValidateBtn.IsVisible = true;
        }
    }

    private void OnTmdbResultRevert(object? sender, EventArgs e)
    {
        _tmdbResultRevertTimer?.Stop();
        if (TmdbResultLabel is not null) TmdbResultLabel.IsVisible = false;
        if (TmdbValidateBtn is not null) TmdbValidateBtn.IsVisible = true;
    }

    // ── Background click → defocus ───────────────────────────────────
    // Click on empty space inside the settings card removes focus from
    // any text-entry control (NumericUpDown / TMDB TextBox) — without
    // this, the field keeps its caret/selection until you tab away.
    private void SettingsBackground_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && IsInsideEditableInput(v)) return;
        SourcesFlyoutPanel?.Focus();
    }

    private static bool IsInsideEditableInput(Visual origin)
    {
        Visual? cur = origin;
        while (cur is not null)
        {
            if (cur is TextBox or NumericUpDown) return true;
            cur = cur.GetVisualParent();
        }
        return false;
    }
}
