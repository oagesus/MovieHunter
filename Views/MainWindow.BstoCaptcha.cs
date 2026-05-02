using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MovieHunter.Models;
using MovieHunter.Services;
using MovieHunter.ViewModels;

namespace MovieHunter.Views;

// bs.to playback works around Google's bot-detection of our headless
// Chrome by hosting an Avalonia NativeWebView (WebView2 / WKWebView /
// WebKitGTK depending on OS) right inside the app and letting the user
// solve the captcha themselves — Google sees a real human session, so
// the audio challenge that was being refused for our docker scraper
// gets served (or auto-passes invisibly).
//
// Flow when the user clicks an episode in the picker:
//   1. Open this overlay with the episode URL loaded in the WebView.
//   2. The user clicks the watch button on the bs.to page; if Google
//      challenges, they solve it.
//   3. bs.to's JS POSTs to /ajax/embed.php with the captcha token.
//      Our injected JS monkey-patches XMLHttpRequest so we forward
//      the response (which contains the hoster URL) back to C# via
//      `invokeCSharpAction(...)` — the helper Avalonia.Controls.WebView
//      injects into every page, which in turn fires WebMessageReceived
//      on the NativeWebView control.
//   4. We close the overlay and hand the hoster URL straight to
//      vm.PlayResolvedHosterAsync — same playback path as a movie,
//      just bypassing yt-dlp's /extract on bs.to itself (since the
//      hoster URL is already resolved).
public partial class MainWindow
{
    // The captcha overlay's WebView is reused for two consecutive flows
    // when the bs.to-resolved hoster turns out to be Vidmoly:
    //   1. BstoCaptcha — load bs.to, solve reCAPTCHA, capture hoster URL
    //      from /ajax/embed.php (the original purpose of this overlay).
    //   2. VidmolyTurnstile — load the Vidmoly /embed- page in the SAME
    //      WebView, let Cloudflare Turnstile pass in real-browser
    //      context, and capture the m3u8 from the JWPlayer setup. Added
    //      after Vidmoly started serving every /embed- behind a
    //      Turnstile gate that no Python-side extractor can pass.
    // The mode controls which JS bridge gets injected on each
    // NavigationCompleted and how WebMessages are interpreted.
    private enum CaptchaOverlayMode { BstoCaptcha, VidmolyTurnstile }
    private CaptchaOverlayMode _overlayMode = CaptchaOverlayMode.BstoCaptcha;

    // The episode VideoResult that triggered the overlay. Used to
    // construct the play call once the hoster URL arrives.
    private VideoResult? _bstoCaptchaEpisode;
    private long _bstoCaptchaResumeMs;
    // Set to true once we've successfully launched playback (either
    // via PlayResolvedHosterAsync after a non-Vidmoly bs.to flow, or
    // via PlayCapturedStream after a Vidmoly Turnstile flow). Lets
    // close/backdrop handlers tell "user cancelled" from "we're tearing
    // down after success".
    private bool _bstoCaptchaResolved;
    // Origin used as Referer when VLC plays the captured m3u8. Set
    // when we navigate the WebView to the Vidmoly embed URL — m3u8
    // segments live on a separate CDN (vmeas.cloud) and that CDN
    // typically requires the embed origin as Referer.
    private string? _vidmolyEmbedOrigin;

    /// <summary>
    /// Public entry point — call from the episode-picker click handler
    /// instead of going through PlayResultAsync directly.
    /// </summary>
    // Fallback: if neither hoster_url nor captcha_visible message
    // arrives within this window, drop the cover anyway so the user
    // can click the play button on bs.to manually. Covers the case
    // where auto-click failed silently.
    private DispatcherTimer? _bstoCaptchaFallbackTimer;

    // Deferred reveal: bs.to's invisible reCAPTCHA inserts the bframe
    // iframe at a sized rect for a few hundred ms while it runs the
    // challenge, then auto-passes silently — but our JS observer
    // fires `captcha_visible` the moment it sees the sized iframe.
    // Revealing immediately briefly flashes bs.to's chrome on the
    // user's screen before hoster_url arrives and the Vidmoly cover
    // comes back, which reads as a spinner-disappear-reappear blink
    // right before the "Resolving Vidmoly…" status. Deferring the
    // reveal by half a second lets the auto-pass complete inside
    // the timer; HandleBstoHosterResolved cancels the pending reveal
    // so the cover never goes away. Only fires when the captcha is
    // GENUINELY interactive (user has to click), which takes way
    // longer than 500 ms to resolve anyway.
    private DispatcherTimer? _bstoCaptchaPendingReveal;

    private void OpenBstoCaptchaOverlay(VideoResult episode, long resumeMs)
    {
        _bstoCaptchaEpisode = episode;
        _bstoCaptchaResumeMs = resumeMs;
        _bstoCaptchaResolved = false;
        _overlayMode = CaptchaOverlayMode.BstoCaptcha;
        _vidmolyEmbedOrigin = null;

        // Title block: show name on the first line, season/episode +
        // episode name on a smaller, dimmer second line. For non-series
        // results (movies, just in case the overlay is reused) the
        // subtitle line collapses out of layout.
        if (!string.IsNullOrEmpty(episode.SeriesTitle))
        {
            BstoCaptchaTitle.Text = episode.SeriesTitle;
            BstoCaptchaSubtitle.Text = BuildEpisodeSubtitle(episode);
            BstoCaptchaSubtitle.IsVisible = !string.IsNullOrEmpty(BstoCaptchaSubtitle.Text);
        }
        else
        {
            BstoCaptchaTitle.Text = episode.Title;
            BstoCaptchaSubtitle.IsVisible = false;
        }

        // Wrapper Margin stays at the offscreen default (-100000,0,0,0
        // from XAML); we just open the outer overlay. As soon as the
        // overlay becomes visible, the wrapper lays out at offscreen
        // Margin, WebView2's HWND is created out there, and any cold-
        // start paint cycles happen invisibly. Reveal-on-ready will
        // be a pure Margin change. CloseBstoCaptchaOverlay resets
        // Margin defensively in case it was left at 0.
        BstoCaptchaWebViewWrapper.Margin = new Thickness(-100000, 0, 0, 0);
        BstoCaptchaLoadingCover.IsVisible = true;
        BstoCaptchaSpinner.Classes.Set("spinning", true);
        BstoCaptchaOverlay.IsVisible = true;

        BstoCaptchaWebView.WebMessageReceived += OnBstoCaptchaWebMessage;
        BstoCaptchaWebView.NavigationCompleted += OnBstoCaptchaNavigated;

        BstoCaptchaWebView.Source = new Uri(episode.PageUrl);

        // Fallback reveal: if the JS bridge never sends a captcha
        // appearance / m3u8 / hoster signal, drop the cover after a
        // short grace period so the user can interact with the page
        // manually. 3 s is enough for any healthy bs.to or Vidmoly
        // round-trip on a working network — past that something has
        // genuinely gone wrong and an interactive page is better
        // than a perpetual spinner. mh-active applies on captcha
        // appearance via the existing watcher.
        _bstoCaptchaFallbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _bstoCaptchaFallbackTimer.Interval = TimeSpan.FromSeconds(3);
        _bstoCaptchaFallbackTimer.Tick -= OnBstoCaptchaFallbackTick;
        _bstoCaptchaFallbackTimer.Tick += OnBstoCaptchaFallbackTick;
        _bstoCaptchaFallbackTimer.Stop();
        _bstoCaptchaFallbackTimer.Start();
    }

    private void OnBstoCaptchaFallbackTick(object? sender, EventArgs e)
    {
        _bstoCaptchaFallbackTimer?.Stop();
        if (!BstoCaptchaOverlay.IsVisible || _bstoCaptchaResolved) return;
        // JS bridge never confirmed ready — slide the WebView onscreen
        // anyway so the user can at least see / interact with bs.to.
        // We deliberately leave the .spinning class set: removing it
        // here only to re-add it later (e.g. on Vidmoly transition)
        // restarts Avalonia's INFINITE rotation animation from angle
        // 0, which reads as a spinner-disappear-reappear stutter.
        // The class is cleared once when the overlay closes.
        if (BstoCaptchaLoadingCover.IsVisible)
        {
            BstoCaptchaWebViewWrapper.Margin = new Thickness(0);
            BstoCaptchaLoadingCover.IsVisible = false;
        }
    }

    // Reveal is now a pure Margin change — the WebView2 HWND has
    // been laid out and painting offscreen since the overlay opened,
    // so its frame buffer already holds the mh-active backdrop +
    // in-page spinner (or even the captcha widget by this point).
    // Sliding it from offscreen Margin to 0 just presents that frame
    // — no HWND creation, no first-paint cycle, no cover→WebView
    // gap. Cover hides simultaneously. We deliberately leave the
    // .spinning class set: bs.to's invisible reCAPTCHA can flicker
    // captcha_visible → hoster_url → Vidmoly transition in a few
    // hundred ms, and removing+re-adding the class restarts the
    // INFINITE rotation animation from angle 0. Keeping it set lets
    // the spinner stay at a continuous angle when the cover comes
    // back. The class is cleared once on overlay close.
    private void RevealBstoCaptchaWebView()
    {
        _bstoCaptchaFallbackTimer?.Stop();
        BstoCaptchaWebViewWrapper.Margin = new Thickness(0);
        BstoCaptchaLoadingCover.IsVisible = false;
    }

    private async void OnBstoCaptchaNavigated(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!BstoCaptchaOverlay.IsVisible) return;
        if (!e.IsSuccess) return;

        // Inject a small JS bridge tailored to the current mode:
        //   BstoCaptcha — patches XMLHttpRequest, auto-fires reCAPTCHA,
        //     forwards the hoster URL from /ajax/embed.php.
        //   VidmolyTurnstile — watches for the JWPlayer setup on the
        //     Vidmoly embed page (which renders only after Turnstile
        //     passes) and forwards the m3u8 + Referer back to C#.
        try
        {
            string script;
            if (_overlayMode == CaptchaOverlayMode.VidmolyTurnstile)
            {
                // Same theme/accent resolution as bs.to so the in-page
                // backdrop + spinner match the outer popup card under
                // every theme (LightLavender, Dracula, Netflix, …).
                var (vidmolyBg, _) = ResolveBstoBackdrop();
                var vidmolyAccent = ResolveBstoAccentColor();
                script = InjectedJsVidmoly
                    .Replace("__BACKDROP_COLOR__", vidmolyBg)
                    .Replace("__ACCENT_COLOR__", vidmolyAccent);
            }
            else
            {
                // Resolve the popup's actual background color so the
                // in-WebView backdrop matches the outer popup card —
                // purple under LightLavender, dark under Netflix/Dracula,
                // whatever the active theme uses.
                var (bgColor, captchaTheme) = ResolveBstoBackdrop();
                var accentColor = ResolveBstoAccentColor();
                script = InjectedJs
                    .Replace("__BACKDROP_COLOR__", bgColor)
                    .Replace("__CAPTCHA_THEME__", captchaTheme)
                    .Replace("__ACCENT_COLOR__", accentColor);
            }
            await BstoCaptchaWebView.InvokeScript(script);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[bsto-webview] script injection failed: {ex}");
        }
    }

    // Resolve `SolidBackgroundFillColorQuarternaryBrush` (the brush
    // the popup card uses) to a hex color string for CSS injection.
    // Has to fall back to the application scope: theme overrides
    // (Dracula, LightLavender, …) merge into Application.Resources,
    // not into the window's own resources — `this.TryFindResource`
    // alone hits FluentTheme's baseline gray under those themes,
    // which doesn't match the popup card's DynamicResource lookup.
    private string ResolveBstoBackdropColor() => ResolveBstoBackdrop().color;

    // Returns the popup card color AND the matching reCAPTCHA theme
    // ('light' / 'dark') for it. Theme picks via perceived luminance:
    // Google's challenge frame is the one part of the captcha we
    // genuinely can't restyle from outside (cross-origin iframe — even
    // WebView2 blocks script injection there per WebView2Feedback
    // #821), but reCAPTCHA's built-in `theme: dark` option recolors
    // the whole frame including the modal backdrop around the widget,
    // so matching it to our popup is the cleanest cross-origin fix.
    private (string color, string captchaTheme) ResolveBstoBackdrop()
    {
        const string key = "SolidBackgroundFillColorQuarternaryBrush";
        var variant = this.ActualThemeVariant;

        Color? resolved = null;
        if (this.TryFindResource(key, variant, out var res) && res is ISolidColorBrush b1)
            resolved = b1.Color;
        else if (Application.Current?.TryFindResource(key, variant, out var res2) == true
                 && res2 is ISolidColorBrush b2)
            resolved = b2.Color;

        var c = resolved ?? Color.FromRgb(0x14, 0x14, 0x1a);
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        // Rec. 601 luminance — splits crisply on the boundary between
        // our light themes (LightLavender et al, near-white surfaces)
        // and dark themes (Dracula et al, ~#44475A surfaces).
        var luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return (hex, luma < 0.5 ? "dark" : "light");
    }

    // Builds the "S1E01 · | Episode 001 | Wer ist Naruto?" subtitle
    // line. The episode title is shown verbatim — bs.to's scraping
    // sometimes includes a "| Episode NNN |" prefix in the title text,
    // and the user prefers to see it kept since it's the canonical
    // bs.to label.
    private static string BuildEpisodeSubtitle(VideoResult episode)
    {
        string? prefix = episode.SeasonNumber is int s && episode.EpisodeNumber is int e
            ? $"S{s:00}E{e:00}"
            : null;
        var name = (episode.Title ?? string.Empty).Trim();
        if (prefix is null) return name;
        return string.IsNullOrEmpty(name) ? prefix : $"{prefix} · {name}";
    }

    // Same lookup as the backdrop, but for the system accent brush.
    // The Avalonia loading cover's spinner uses this brush directly
    // (via DynamicResource); the in-page spinner needs the same
    // resolved color so the cover→in-page handoff is invisible.
    private string ResolveBstoAccentColor()
    {
        const string key = "SystemAccentColorBrush";
        var variant = this.ActualThemeVariant;
        Color? resolved = null;
        if (this.TryFindResource(key, variant, out var res) && res is ISolidColorBrush b1)
            resolved = b1.Color;
        else if (Application.Current?.TryFindResource(key, variant, out var res2) == true
                 && res2 is ISolidColorBrush b2)
            resolved = b2.Color;
        var c = resolved ?? Color.FromRgb(0x9F, 0x8A, 0xE6);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void OnBstoCaptchaWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        // Body is the string that JS passed to invokeCSharpAction(...).
        // Our injected JS sends a JSON envelope `{type: "...", url: "..."}`.
        try
        {
            var raw = e.Body;
            if (string.IsNullOrEmpty(raw)) return;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            var msgType = type.GetString();

            if (msgType == "hoster_url"
                && root.TryGetProperty("url", out var urlEl)
                && urlEl.GetString() is { } hoster
                && !string.IsNullOrWhiteSpace(hoster))
            {
                Dispatcher.UIThread.Post(() => HandleBstoHosterResolved(hoster));
            }
            else if (msgType == "m3u8_url"
                && _overlayMode == CaptchaOverlayMode.VidmolyTurnstile
                && root.TryGetProperty("url", out var m3u8El)
                && m3u8El.GetString() is { } m3u8
                && !string.IsNullOrWhiteSpace(m3u8))
            {
                _bstoCaptchaResolved = true;
                Dispatcher.UIThread.Post(() => HandleVidmolyM3u8Resolved(m3u8));
            }
            else if (msgType == "vidmoly_ready"
                && _overlayMode == CaptchaOverlayMode.VidmolyTurnstile)
            {
                // The in-page CSS backdrop + spinner are mounted, so
                // sliding the WebView onscreen now hands the loading
                // visual off to Chromium's render thread — no more
                // Avalonia spinner stutter, no more airspace flicker.
                // Pure Margin change, exactly like bs.to's reveal.
                Dispatcher.UIThread.Post(RevealBstoCaptchaWebView);
            }
            else if (msgType == "vidmoly_navigating"
                && _overlayMode == CaptchaOverlayMode.VidmolyTurnstile)
            {
                // Cloudflare gate is about to do
                // `window.location.href = redirectTo` — push the
                // WebView offscreen and bring back the Avalonia cover
                // so the user doesn't see the bare new page (JWPlayer
                // chrome / Vidmoly's post-captcha state) render before
                // our CSS theme + spinner re-inject on the next
                // NavigationCompleted. The next vidmoly_ready slides
                // it back onscreen.
                Dispatcher.UIThread.Post(HideForVidmolyRedirect);
            }
            else if (msgType == "captcha_visible")
            {
                // bs.to flow: defer the reveal by 500 ms. Invisible
                // reCAPTCHA briefly sizes the bframe iframe during
                // its silent challenge, which trips our observer the
                // same way a real challenge does — but auto-pass
                // completes inside ~100 ms, so revealing immediately
                // flashes bs.to's chrome for a few frames before
                // hoster_url comes in and the Vidmoly cover replaces
                // it. The deferred fire is cancelled by
                // HandleBstoHosterResolved when hoster_url arrives,
                // so an auto-passed captcha never reveals at all.
                //
                // Vidmoly flow: DON'T reveal. The Turnstile widget is
                // visible from the moment the gate page loads (managed
                // mode), so revealing here would flash Cloudflare's
                // dark "Security Check" page on screen before Turnstile
                // auto-passes. Keep the themed loading cover on
                // throughout — the m3u8 capture closes the overlay
                // directly, with no reveal step in between. The 3 s
                // fallback timer still drops the cover for the rare
                // case where Turnstile actually requires a click.
                if (_overlayMode == CaptchaOverlayMode.BstoCaptcha)
                {
                    Dispatcher.UIThread.Post(ScheduleDeferredBstoReveal);
                }
            }
            else if (msgType == "log"
                && root.TryGetProperty("text", out var t))
            {
                System.Diagnostics.Debug.WriteLine($"[bsto-webview] {t.GetString()}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[bsto-webview] message parse failed: {ex}");
        }
    }

    // 500 ms deferred reveal — see _bstoCaptchaPendingReveal field doc.
    // Re-arming on each captcha_visible call (unlikely but possible if
    // bs.to's bframe re-sizes) just resets the same 500 ms window;
    // cancellation in HandleBstoHosterResolved still wins.
    private void ScheduleDeferredBstoReveal()
    {
        if (!BstoCaptchaOverlay.IsVisible) return;
        if (_overlayMode != CaptchaOverlayMode.BstoCaptcha) return;
        _bstoCaptchaPendingReveal ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _bstoCaptchaPendingReveal.Tick -= OnBstoCaptchaPendingRevealTick;
        _bstoCaptchaPendingReveal.Tick += OnBstoCaptchaPendingRevealTick;
        _bstoCaptchaPendingReveal.Stop();
        _bstoCaptchaPendingReveal.Start();
    }

    private void OnBstoCaptchaPendingRevealTick(object? sender, EventArgs e)
    {
        _bstoCaptchaPendingReveal?.Stop();
        if (!BstoCaptchaOverlay.IsVisible) return;
        if (_bstoCaptchaResolved) return;
        if (_overlayMode != CaptchaOverlayMode.BstoCaptcha) return;
        // 500 ms passed without hoster_url — captcha is genuinely
        // interactive, reveal so the user can solve it.
        RevealBstoCaptchaWebView();
    }

    private void HandleBstoHosterResolved(string hosterUrl)
    {
        // Cancel any pending reveal — hoster_url arriving means the
        // captcha (if there even was one) auto-passed, so revealing
        // would only flash bs.to's chrome between here and the
        // Vidmoly cover taking over (or the overlay closing for
        // non-Vidmoly hosts).
        _bstoCaptchaPendingReveal?.Stop();

        var episode = _bstoCaptchaEpisode;
        var resumeMs = _bstoCaptchaResumeMs;

        // Vidmoly added a Cloudflare Turnstile gate on every /embed-
        // mirror that no Python-side extractor can pass. Instead of
        // closing the overlay and handing the URL to /extract (which
        // would fail with "Hoster reached but couldn't extract a
        // stream"), we KEEP the overlay open, transition the WebView
        // to the Vidmoly embed URL, let Turnstile pass in real-browser
        // context (WebView2 = full Chromium), and capture the m3u8
        // from the JWPlayer setup once the actual embed page renders.
        if (IsVidmolyHosterUrl(hosterUrl))
        {
            _ = BeginVidmolyTurnstileFlowAsync(hosterUrl);
            return;
        }

        // Non-Vidmoly hoster — keep the overlay open with the cover +
        // spinner visible while /extract runs (1-3 s on Voe /
        // Doodstream / etc.). Closing immediately after captcha
        // solve and waiting for /extract behind a bare underlying
        // tab made the popup look like it was dismissing the user
        // back to where they came from. Symmetric with the Vidmoly
        // flow: cover stays up until playback is actually about to
        // start.
        _ = BeginGenericResolveFlowAsync(episode, hosterUrl, resumeMs);
    }

    // Holds the cover up while PlayResolvedHosterAsync awaits
    // /extract for non-Vidmoly hosters (Voe, Doodstream, …). Closes
    // the overlay AFTER the await — by then PlayRequested has
    // already fired (success path) or status reflects the failure,
    // and either way the gap to first VLC frame is small enough
    // that the user perceives a continuous "loading → playing"
    // transition.
    private async Task BeginGenericResolveFlowAsync(VideoResult? episode, string hosterUrl, long resumeMs)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Captcha is past — kill the bs.to-phase fallback timer so
        // it can't fire mid-extract and rip the cover off behind us.
        _bstoCaptchaFallbackTimer?.Stop();

        // Push the WebView offscreen and re-show the cover. Spinner
        // class stays set (untouched since open) so the rotation
        // animation just keeps going from its current angle — no
        // restart-from-0 stutter.
        BstoCaptchaWebViewWrapper.Margin = new Thickness(-100000, 0, 0, 0);
        BstoCaptchaLoadingCover.IsVisible = true;
        vm.Status = $"Resolving {episode?.Title ?? "stream"}…";

        if (episode is not null)
        {
            // PlayResolvedHosterAsync: awaits /extract, fires
            // PlayRequested on success (synchronous fan-out before
            // its own return). When this await unblocks, VLC has
            // already been told to start loading.
            await vm.PlayResolvedHosterAsync(episode, hosterUrl, resumeMs);
        }

        // Mark resolved BEFORE close so the close path doesn't
        // overwrite vm.Status with "Cancelled." — the status either
        // shows the extract failure message (if it failed) or the
        // "Loading…" set by PlayResolvedHosterAsync (success).
        _bstoCaptchaResolved = true;
        CloseBstoCaptchaOverlay();
    }

    // Matches every Vidmoly mirror domain we've seen — .to .me .net
    // .biz .cc .sx and the family — so the chained Turnstile flow
    // kicks in regardless of which mirror bs.to picked. vmoly.* is a
    // legacy alt domain occasionally returned in older episodes.
    private static bool IsVidmolyHosterUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return host.StartsWith("vidmoly.") || host.StartsWith("vmoly.");
    }

    // Kicks off the Vidmoly Turnstile flow: resolves the wrapper URL
    // (e.g. /w/<id> on .me) to its underlying /embed-<id>.html (which
    // can live on a different mirror like .biz — the SSR JSON is the
    // only authoritative source for which one), then navigates the
    // WebView there. Pushes the WebView offscreen during navigation
    // and re-shows the loading cover so the user doesn't see bs.to's
    // page contents fade-out and the embed page fade-in — same UX as
    // the initial bs.to navigation.
    private async Task BeginVidmolyTurnstileFlowAsync(string vidmolyUrl)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // STOP the bs.to-phase fallback timer FIRST, before any await.
        // Otherwise it can fire during ResolveVidmolyEmbedUrlAsync and
        // run RevealBstoCaptchaWebView mid-flight — sliding the
        // wrapper to Margin=0, hiding the cover, and stopping the
        // spin animation. By the time the await resumes the cover is
        // already off and the spinner has briefly stopped + restarted,
        // which reads to the user as a "flicker" through the middle
        // of the loading sequence.
        _bstoCaptchaFallbackTimer?.Stop();

        // Push the WebView offscreen so the user doesn't see bs.to's
        // page contents fade-out and the embed page fade-in. Cover
        // back to visible. The .spinning class is left untouched —
        // it's been set since OpenBstoCaptchaOverlay and stays set
        // until CloseBstoCaptchaOverlay, so the rotation animation
        // runs continuously across reveal/restore cycles. Toggling
        // it here would restart the animation from angle 0 and
        // produce a 1-frame spinner-snap stutter.
        BstoCaptchaWebViewWrapper.Margin = new Thickness(-100000, 0, 0, 0);
        BstoCaptchaLoadingCover.IsVisible = true;
        vm.Status = "Resolving Vidmoly…";

        var embedUrl = await vm.YtDlp.ResolveVidmolyEmbedUrlAsync(vidmolyUrl, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(embedUrl)
            || !Uri.TryCreate(embedUrl, UriKind.Absolute, out var embedUri))
        {
            // Couldn't resolve — fall back to the original /extract
            // path so the user sees the existing failure status, not
            // a silent hang. Match the bs.to flow exactly from here.
            _bstoCaptchaResolved = true;
            CloseBstoCaptchaOverlay();
            if (_bstoCaptchaEpisode is { } episode)
                _ = vm.PlayResolvedHosterAsync(episode, vidmolyUrl, _bstoCaptchaResumeMs);
            return;
        }

        _overlayMode = CaptchaOverlayMode.VidmolyTurnstile;
        _vidmolyEmbedOrigin = $"{embedUri.Scheme}://{embedUri.Host}/";

        // Re-arm the fallback reveal — Turnstile usually auto-passes
        // invisibly (in which case the m3u8 just appears and we close
        // the overlay), but if it shows an interactive checkbox the
        // user needs to see, the timer drops the cover so they can
        // click. 5 s budget here vs the bs.to phase's 3 s because
        // Cloudflare's challenge can take a few seconds longer to
        // complete than bs.to's reCAPTCHA, and prematurely revealing
        // a still-loading Turnstile gate page is worse UX than
        // waiting an extra second or two for the auto-pass.
        _bstoCaptchaFallbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _bstoCaptchaFallbackTimer.Interval = TimeSpan.FromSeconds(5);
        _bstoCaptchaFallbackTimer.Stop();
        _bstoCaptchaFallbackTimer.Start();

        BstoCaptchaWebView.Source = embedUri;
    }

    // Re-hides the WebView during the Cloudflare → JWPlayer redirect.
    // Idempotent: if a stray duplicate vidmoly_navigating arrives
    // (e.g. beforeunload AND /challenge-verify both fire) the second
    // call is a no-op — the wrapper is already offscreen and the
    // cover is already visible.
    private void HideForVidmolyRedirect()
    {
        if (!BstoCaptchaOverlay.IsVisible) return;
        if (_overlayMode != CaptchaOverlayMode.VidmolyTurnstile) return;
        if (_bstoCaptchaResolved) return;
        BstoCaptchaWebViewWrapper.Margin = new Thickness(-100000, 0, 0, 0);
        BstoCaptchaLoadingCover.IsVisible = true;
    }

    private void HandleVidmolyM3u8Resolved(string m3u8Url)
    {
        var episode = _bstoCaptchaEpisode;
        var resumeMs = _bstoCaptchaResumeMs;
        var referer = _vidmolyEmbedOrigin;
        CloseBstoCaptchaOverlay();
        if (episode is null || DataContext is not MainWindowViewModel vm) return;

        // m3u8 came directly from the JWPlayer setup inside our
        // WebView, so we already have everything VLC needs. Skip
        // /extract entirely and fan out to PlayRequested via
        // PlayCapturedStream. The CDN (vmeas.cloud) checks Referer,
        // so we pass the embed origin along with the URL.
        var stream = new StreamResult(
            Url: m3u8Url,
            AudioUrl: null,
            HttpUserAgent: null,
            HttpReferer: referer);
        vm.PlayCapturedStream(episode, stream, resumeMs);
    }

    private void BstoCaptchaOverlay_BackdropClicked(object? sender, PointerPressedEventArgs e)
    {
        // Same backdrop-cancel pattern as the theme + episode pickers.
        CloseBstoCaptchaOverlay();
    }

    private void BstoCaptchaCard_Pressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private void BstoCaptchaClose_Click(object? sender, RoutedEventArgs e)
        => CloseBstoCaptchaOverlay();

    private void CloseBstoCaptchaOverlay()
    {
        // Re-entry guard: a successful captcha solve can fire `hoster_url`
        // twice (XHR-patch path on bs.to's own embed.php POST, plus the
        // direct `fireDirectCaptcha` jQuery.ajax path). Both queue
        // HandleBstoHosterResolved → CloseBstoCaptchaOverlay on the UI
        // thread. The first close clears `_bstoCaptchaResolved` to false
        // at the bottom; without this guard, the second close would then
        // take the cancel branch below and overwrite the playback
        // "Loading…" status with "Cancelled." for a visible flash.
        if (!BstoCaptchaOverlay.IsVisible) return;
        _bstoCaptchaFallbackTimer?.Stop();
        _bstoCaptchaPendingReveal?.Stop();
        BstoCaptchaWebView.WebMessageReceived -= OnBstoCaptchaWebMessage;
        BstoCaptchaWebView.NavigationCompleted -= OnBstoCaptchaNavigated;
        // Navigate to about:blank so the page stops running JS / playing
        // any audio captcha sound. Setting Source to null isn't supported.
        try { BstoCaptchaWebView.Source = new Uri("about:blank"); } catch { }
        // Re-push wrapper offscreen and re-show the cover so the next
        // session opens with the loading state (not the previous
        // about:blank flash). Outer overlay collapses via IsVisible —
        // that's what stops the wrapper from being laid out in the
        // first place, so WebView2's HWND is destroyed between sessions.
        BstoCaptchaWebViewWrapper.Margin = new Thickness(-100000, 0, 0, 0);
        BstoCaptchaLoadingCover.IsVisible = true;
        BstoCaptchaSpinner.Classes.Set("spinning", false);
        BstoCaptchaOverlay.IsVisible = false;
        if (!_bstoCaptchaResolved)
        {
            // User cancelled — drop the pending highlight so the
            // matching Recent / MyList card returns to idle. (When the
            // captcha resolves successfully, _pendingPlayingPageUrl
            // stays set so the card keeps "Continue playing" through
            // the hoster-extract → playback chain; OnPlayRequested
            // clears it once playback actually starts.)
            _pendingPlayingPageUrl = null;
            ApplyRecentPlayingHighlightNow();
            if (DataContext is MainWindowViewModel vm)
                vm.Status = "Cancelled.";
        }
        _bstoCaptchaEpisode = null;
        _bstoCaptchaResolved = false;
        _overlayMode = CaptchaOverlayMode.BstoCaptcha;
        _vidmolyEmbedOrigin = null;
    }

    // Injected JS — orchestrates the whole flow inside the page:
    //   1. Patch XHR so /ajax/embed.php responses get forwarded as
    //      `hoster_url` messages.
    //   2. Patch window.open so the embed=0 popup path also surfaces.
    //   3. Dismiss the cookie banner.
    //   4. Auto-click bs.to's watch button — best-case the user never
    //      has to interact with anything.
    //   5. Inject a CSS rule that, when the reCAPTCHA challenge bframe
    //      appears, drops a black backdrop over the entire page and
    //      z-indexes the captcha on top — so the user sees only the
    //      captcha widget, not bs.to's chrome.
    //   6. Watch for the bframe; when it shows up, signal C# so the
    //      WebView gets revealed (it starts hidden behind a spinner).
    //
    // Avalonia.Controls.WebView injects `invokeCSharpAction(string)`
    // into every page; that fires WebMessageReceived on C#'s side.
    private const string InjectedJs = @"
(function() {
    if (window.__bsto_patched__) return;
    window.__bsto_patched__ = true;

    function send(payload) {
        try {
            if (typeof invokeCSharpAction === 'function') {
                invokeCSharpAction(JSON.stringify(payload));
            }
        } catch (e) { /* ignore */ }
    }

    // 1. Patch XHR so /ajax/embed.php responses are forwarded.
    var origSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.send = function() {
        this.addEventListener('load', function() {
            try {
                if (this.responseURL && this.responseURL.indexOf('/ajax/embed.php') !== -1) {
                    send({ type: 'log', text: 'embed.php response: ' + this.responseText.substring(0, 200) });
                    var data = JSON.parse(this.responseText);
                    if (data && data.success && data.link) {
                        send({ type: 'hoster_url', url: data.link });
                    }
                }
            } catch (e) { /* not JSON */ }
        });
        return origSend.apply(this, arguments);
    };

    // 2. Patch window.open — embed=0 path uses this to launch hoster.
    var origOpen = window.open;
    window.open = function(url) {
        try {
            if (url && url.indexOf('bs.to') === -1) {
                send({ type: 'hoster_url', url: url });
                return null;
            }
        } catch (e) {}
        return origOpen.apply(this, arguments);
    };

    // 3. Inject the focus CSS and apply it immediately. Because the
    //    native WebView surface paints over any Avalonia control we
    //    might put on top of it (airspace), the only reliable hide
    //    mechanism is INSIDE the page: a body::before backdrop on a
    //    high z-index that paints over bs.to's chrome, with just the
    //    play button (then later the captcha bframe) z-indexed above
    //    the backdrop.
    // Inject the focus CSS — the backdrop hides bs.to's chrome, the
    // captcha bframe is z-indexed on top when it appears.
    var style = document.createElement('style');
    style.textContent = `
        html.mh-active, html.mh-active body {
            overflow: hidden !important;
            height: 100% !important;
            margin: 0 !important;
        }
        html.mh-active body::before {
            content: '';
            position: fixed !important;
            inset: 0 !important;
            background: __BACKDROP_COLOR__ !important;
            z-index: 99000 !important;
            pointer-events: auto !important;
        }
        html.mh-active iframe[src*='recaptcha/api2/bframe'] {
            position: fixed !important;
            top: 50% !important;
            left: 50% !important;
            transform: translate(-50%, -50%) !important;
            z-index: 99999 !important;
            border: 0 !important;
            box-shadow: 0 12px 40px rgba(0,0,0,0.6) !important;
        }
    `;
    document.head.appendChild(style);
    document.documentElement.classList.add('mh-active');

    // No 'ready' send and no in-page spinner. The WebView stays
    // offscreen (via the wrapper's offscreen Margin) for the entire
    // duration of navigation + auto-trigger, so the user keeps
    // seeing the Avalonia loading cover with its themed spinner
    // the whole time. Reveal happens only when the captcha widget
    // actually appears (via the captcha_visible message), or via
    // the C# fallback timer if nothing happens. Because WebView2
    // keeps painting offscreen, the moment we slide it onscreen
    // its frame buffer already has the matte + widget settled —
    // no transition gap, no in-page spinner needed.

    // Find bs.to's reCAPTCHA sitekey. The sitekey lives in bs.to's
    // external page.<hash>.js (not in any inline script) so we have
    // to fetch the external scripts and scan them. Regex literals
    // use \x22 instead of literal quotes to dodge escaping mess in
    // the C# verbatim string this lives in.
    function scanForSitekey(text) {
        if (!text) return null;
        // Match a quoted sitekey value (single or double quote).
        var m = text.match(/sitekey\s*:\s*[\x22']([0-9A-Za-z_-]{30,})[\x22']/);
        if (m) return m[1];
        // Any 6L-prefixed key (Google reCAPTCHA v2/v3 sitekeys all
        // start with 6L), in any quote style.
        m = text.match(/[\x22'](6L[0-9A-Za-z_-]{30,})[\x22']/);
        if (m) return m[1];
        return null;
    }

    async function findSitekey() {
        // 1. data-sitekey attribute on any element.
        var dataElem = document.querySelector('[data-sitekey]');
        if (dataElem) {
            var k = dataElem.getAttribute('data-sitekey');
            if (k) return k;
        }
        // 2. Inline script content.
        var scripts = document.querySelectorAll('script');
        for (var i = 0; i < scripts.length; i++) {
            var hit = scanForSitekey(scripts[i].textContent || '');
            if (hit) return hit;
        }
        // 3. recaptcha api script — sometimes the sitekey is in the
        //    query string for the explicit-render flow.
        var rcScripts = document.querySelectorAll('script[src*=\'recaptcha\']');
        for (var j = 0; j < rcScripts.length; j++) {
            var srcUrl = rcScripts[j].getAttribute('src') || '';
            var m2 = srcUrl.match(/[?&](?:render|sitekey)=([0-9A-Za-z_-]{30,})/);
            if (m2) return m2[1];
        }
        // 4. External same-origin scripts — fetch and scan. This is
        //    where bs.to actually puts it (page.<hash>.js).
        var externalScripts = document.querySelectorAll('script[src]');
        for (var x = 0; x < externalScripts.length; x++) {
            var sUrl = externalScripts[x].src;
            if (!sUrl) continue;
            // Skip Google's own scripts and analytics — sitekey isn't there.
            if (sUrl.indexOf('recaptcha') !== -1) continue;
            if (sUrl.indexOf('google-analytics') !== -1) continue;
            try {
                var resp = await fetch(sUrl, { credentials: 'include' });
                if (!resp.ok) continue;
                var body = await resp.text();
                var hit2 = scanForSitekey(body);
                if (hit2) {
                    send({ type: 'log', text: 'sitekey found in ' + sUrl.split('/').pop() });
                    return hit2;
                }
            } catch (fetchErr) {
                // CORS / network — skip and try next script.
            }
        }
        return null;
    }

    // The captcha widget is rendered exactly once per session — we
    // auto-trigger on page load instead of waiting for a user click.
    // grecaptcha.render errors if called twice on the same container
    // so we no-op once captchaWidgetId is set; a follow-up challenge
    // (after a wrong tile selection, etc.) is handled by Google's
    // own widget UI without us calling execute again.
    var captchaWidgetId = null;
    var autoTriggerInFlight = false;
    // Set to true the moment fireDirectCaptcha fires, so the matte
    // can stay applied (at default centered position) even before
    // the bframe iframe exists in the DOM. Without this flag,
    // ensureCaptchaMatte removes the matte when bframe is missing,
    // leaving Google's modal dimmer briefly visible on first captcha.
    var captchaTriggered = false;

    // Direct captcha flow — skips bs.to's click handler entirely.
    // bs.to's handler bails at an internal check (probably
    // `e.originalEvent.isTrusted`, which is read-only and can't be
    // faked from JS), so we call grecaptcha.render + execute
    // ourselves and POST to /ajax/embed.php with our own token.
    async function fireDirectCaptcha() {
        var hp = document.querySelector('section.serie .hoster-player')
              || document.querySelector('.hoster-player');
        if (!hp) {
            send({ type: 'log', text: 'no .hoster-player' });
            return;
        }
        var lid = hp.getAttribute('data-lid');
        if (!lid) {
            send({ type: 'log', text: 'no data-lid on hoster-player' });
            return;
        }

        if (typeof grecaptcha === 'undefined') {
            send({ type: 'log', text: 'grecaptcha not loaded yet' });
            return;
        }

        // Already rendered — nothing to do. With auto-trigger we
        // only ever render once per session.
        if (captchaWidgetId !== null) {
            return;
        }

        send({ type: 'log', text: 'searching for sitekey…' });
        var sitekey = await findSitekey();
        if (!sitekey) {
            send({ type: 'log', text: 'NO sitekey found in any script' });
            return;
        }
        send({ type: 'log', text: 'using sitekey: ' + sitekey.substring(0, 12) + '… lid=' + lid });

        // Make sure the challenge container exists.
        var challengeDiv = document.getElementById('challenge');
        if (!challengeDiv) {
            challengeDiv = document.createElement('div');
            challengeDiv.id = 'challenge';
            document.body.appendChild(challengeDiv);
        }

        // Token-received callback — POSTs to embed.php and forwards
        // the hoster URL back to C#.
        function onToken(token) {
            send({ type: 'log', text: 'token received, POSTing embed.php…' });
            try {
                jQuery.ajax({
                    url: 'ajax/embed.php',
                    type: 'POST',
                    dataType: 'JSON',
                    data: { LID: lid, ticket: token },
                    success: function(r) {
                        send({ type: 'log', text: 'embed.php response: ' + JSON.stringify(r).substring(0, 200) });
                        if (r && r.success && r.link) {
                            send({ type: 'hoster_url', url: r.link });
                        } else {
                            send({ type: 'log', text: 'embed.php success but no link' });
                        }
                    },
                    error: function(xhr, status, err) {
                        send({ type: 'log', text: 'embed.php error: ' + status + ' / ' + err + ' / ' + xhr.status });
                    }
                });
            } catch (e) {
                send({ type: 'log', text: 'ajax exception: ' + e });
            }
        }

        // Mark the session as triggered and apply the matte right
        // away — BEFORE grecaptcha.render schedules Google's modal
        // dimmer + bframe insertion. Otherwise on first captcha
        // there's a window where the dimmer is in DOM but the matte
        // isn't, and the WebView's compositor catches that intermediate
        // frame as a white flash. With the matte already in place at
        // default centered position, the bframe slides into the
        // existing cutout when it arrives.
        captchaTriggered = true;
        ensureCaptchaMatte();

        try {
            var freshId = 'mh-captcha-host';
            var freshHost = document.getElementById(freshId);
            if (!freshHost) {
                freshHost = document.createElement('div');
                freshHost.id = freshId;
                document.body.appendChild(freshHost);
            }
            captchaWidgetId = grecaptcha.render(freshHost, {
                sitekey: sitekey,
                size: 'invisible',
                // 'dark' / 'light' chosen on the C# side from the
                // popup card's resolved luminance — the only knob we
                // have to recolor the inside-of-iframe modal Google
                // paints around the widget, since cross-origin script
                // injection is blocked by WebView2.
                theme: '__CAPTCHA_THEME__',
                callback: onToken
            });
            send({ type: 'log', text: 'rendered captcha widgetId=' + captchaWidgetId });
            grecaptcha.execute(captchaWidgetId);
            send({ type: 'log', text: 'grecaptcha.execute() called' });
        } catch (e) {
            send({ type: 'log', text: 'render/execute error: ' + e });
        }
    }

    // Auto-trigger: poll until grecaptcha + .hoster-player are both
    // ready, then call fireDirectCaptcha exactly once. fireDirectCaptcha
    // itself bails if any precondition is missing (sitekey not yet in
    // the DOM, etc.) so we leave autoTriggerInFlight clear after a
    // failed attempt — the next tick retries. Once it succeeds,
    // captchaWidgetId is set and subsequent ticks short-circuit.
    async function tryAutoTrigger() {
        if (captchaWidgetId !== null) return;
        if (autoTriggerInFlight) return;
        if (typeof grecaptcha === 'undefined' || typeof grecaptcha.execute !== 'function') return;
        var hp = document.querySelector('.hoster-player');
        if (!hp || !hp.getAttribute('data-lid')) return;
        autoTriggerInFlight = true;
        try {
            await fireDirectCaptcha();
        } finally {
            autoTriggerInFlight = false;
        }
    }
    var autoTriggerTimer = setInterval(tryAutoTrigger, 300);
    setTimeout(function() { clearInterval(autoTriggerTimer); }, 30000);

    // Diagnostic — tells us whether .hoster-player exists at all,
    // plus whether bs.to has set up the captcha pieces (the
    // challenge div, the recaptcha api script, the grecaptcha
    // global). Emit once on load so we can see what state the page
    // reached without needing a click event any more.
    function dumpPageState(when) {
        var hp = document.querySelector('.hoster-player');
        var hasJQuery = typeof jQuery !== 'undefined';
        var hasHandler = false;
        if (hp && hasJQuery && jQuery._data) {
            var d = jQuery._data(hp, 'events');
            hasHandler = !!(d && d.click && d.click.length > 0);
        }
        var challenge = document.getElementById('challenge');
        var apiScript = document.querySelector('script[src*=\'recaptcha/api.js\']');
        var hasGrecaptcha = typeof grecaptcha !== 'undefined';
        var bframe = document.querySelector('iframe[src*=\'recaptcha/api2/bframe\']');

        send({ type: 'log',
            text: when + ': hp=' + !!hp
                + ' lid=' + (hp ? hp.getAttribute('data-lid') : 'n/a')
                + ' clickHandler=' + hasHandler
                + ' challengeDiv=' + !!challenge
                + ' apiScript=' + !!apiScript
                + ' grecaptcha=' + hasGrecaptcha
                + ' bframe=' + !!bframe
        });
    }
    setTimeout(function() { dumpPageState('on-load'); }, 800);
    setTimeout(function() { dumpPageState('after-2s'); }, 2000);

    // 4. Cookie banner — dismiss right away so it doesn't intercept
    //    anything in the page (it can sit above .hoster-player).
    setTimeout(function() {
        try {
            var ccBtn = document.querySelector('.cc-compliance a, .cc-compliance .cc-btn');
            if (ccBtn) ccBtn.click();
        } catch (e) {}
    }, 200);

    // 6. Watch for the reCAPTCHA challenge bframe and apply a matte
    //    over Google's modal backdrop.
    //
    //    Google's challenge bframe is a cross-origin iframe and the
    //    light-gray area surrounding the widget lives INSIDE it
    //    (Google's modal background, painted by their CSS). We can't
    //    reach it: WebView2 explicitly blocks script/CSS injection
    //    into cross-origin iframes (WebView2Feedback #821), and
    //    reCAPTCHA's `theme: 'dark'` only affects the badge, not the
    //    challenge frame's modal backdrop. So instead of changing
    //    Google's frame, we COVER it: place a fixed-position div on
    //    top of the iframe, sized to fill the viewport, with a
    //    transparent center-cutout the size of the widget. Outside
    //    the cutout we paint our themed backdrop color; through the
    //    cutout the user sees the widget. The trick is one element
    //    with a HUGE inset box-shadow — `box-shadow: 0 0 0 9999px
    //    color` extends the color 9999px outward from the element's
    //    border, painting the whole viewport with our color while
    //    leaving the element itself transparent. pointer-events:none
    //    keeps clicks passing through to the widget normally.
    function ensureCaptchaMatte() {
        var bf = document.querySelector('iframe[src*=\'recaptcha/api2/bframe\']');
        var existing = document.getElementById('mh-captcha-matte');
        // Keep matte applied if captcha was triggered, even before
        // the bframe shows up — covers Google's dimmer on first
        // captcha when there's a window between dimmer insertion
        // and bframe insertion. Only remove if no bframe AND no
        // pending trigger (i.e., the captcha session is over).
        if (!bf && !captchaTriggered) {
            if (existing) existing.remove();
            return;
        }
        // bf may be null here (preemptive call from fireDirectCaptcha
        // before grecaptcha.execute has injected the iframe). Use a
        // zero rect in that case so the size-fallback branch picks
        // the centered 320×580 default.
        var r = bf ? bf.getBoundingClientRect() : { left: 0, top: 0, width: 0, height: 0 };
        // Apply matte even if the bframe is still 0×0 (just inserted
        // by Google but not yet sized). Use a centered 320×580 default
        // in that case so the cutout is already in place when the
        // bframe sizes itself a frame later — otherwise Google's modal
        // dimmer is briefly visible (white flash on first captcha).
        // Once the bframe gets a real size, subsequent calls update
        // the strips to track the actual iframe bounds.
        var w, h, cx, cy;
        if (r.width >= 50 && r.height >= 50 && r.width <= 400 && r.height <= 700) {
            w = r.width;
            h = r.height;
            cx = r.left + r.width / 2;
            cy = r.top + r.height / 2;
        } else {
            w = 320;
            h = 580;
            cx = window.innerWidth / 2;
            cy = window.innerHeight / 2;
        }
        // Four strips forming a frame around the cutout. Two reasons
        // for strips instead of the previous box-shadow trick: (1)
        // a strip's hit area equals its painted area (pointer-events
        // works the way you'd expect), so clicks on the painted
        // matte are ABSORBED here and never reach Google's dimmer
        // beneath — that's what was dismissing the captcha when the
        // user clicked outside it. (2) The cutout area has literally
        // no element above it, so clicks pass cleanly through to
        // the captcha widget. Each strip is fixed-positioned at max
        // z-index, so they cover Google's dimmer (~2e9) too.
        if (!existing) {
            existing = document.createElement('div');
            existing.id = 'mh-captcha-matte';
            for (var i = 0; i < 4; i++) {
                var s = document.createElement('div');
                s.className = 'mh-matte-strip';
                s.style.cssText =
                    'position:fixed!important;' +
                    'background:__BACKDROP_COLOR__!important;' +
                    'pointer-events:auto!important;' +
                    'z-index:2147483647!important;' +
                    'border:0!important;' +
                    'margin:0!important;' +
                    'padding:0!important;';
                existing.appendChild(s);
            }
            document.body.appendChild(existing);
            send({ type: 'log', text: 'matte applied (' + Math.round(w) + 'x' + Math.round(h) + ')' });
        }
        var strips = existing.querySelectorAll('.mh-matte-strip');
        var L = cx - w / 2, R = cx + w / 2, T = cy - h / 2, B = cy + h / 2;
        var vw = window.innerWidth, vh = window.innerHeight;
        // Top strip — full width, from top of viewport to top of cutout.
        strips[0].style.left = '0px';
        strips[0].style.top = '0px';
        strips[0].style.width = vw + 'px';
        strips[0].style.height = Math.max(T, 0) + 'px';
        // Bottom strip — full width, from bottom of cutout to bottom of viewport.
        strips[1].style.left = '0px';
        strips[1].style.top = B + 'px';
        strips[1].style.width = vw + 'px';
        strips[1].style.height = Math.max(vh - B, 0) + 'px';
        // Left strip — between top/bottom of cutout, from viewport left to cutout left.
        strips[2].style.left = '0px';
        strips[2].style.top = T + 'px';
        strips[2].style.width = Math.max(L, 0) + 'px';
        strips[2].style.height = h + 'px';
        // Right strip — between top/bottom of cutout, from cutout right to viewport right.
        strips[3].style.left = R + 'px';
        strips[3].style.top = T + 'px';
        strips[3].style.width = Math.max(vw - R, 0) + 'px';
        strips[3].style.height = h + 'px';
    }

    var notified = false;
    function checkCaptcha() {
        var bframe = document.querySelector('iframe[src*=\'recaptcha/api2/bframe\']');
        if (!bframe) {
            ensureCaptchaMatte();
            return;
        }
        var rect = bframe.getBoundingClientRect();
        // bframe exists but is offscreen / 0×0 until invisible v2
        // escalates to a real challenge. Apply matte regardless
        // (defaults to centered 320×580) but only signal C# once
        // the bframe has a real size.
        ensureCaptchaMatte();
        if (rect.width < 50 || rect.height < 50) return;
        if (!notified) {
            notified = true;
            send({ type: 'captcha_visible' });
            send({ type: 'log', text: 'captcha bframe visible (' + rect.width + 'x' + rect.height + ')' });
        }
    }

    // Synchronous bframe pinning. The setInterval at 500 ms is fine
    // for tracking later changes, but on the FIRST appearance of
    // the bframe iframe it leaves a paint frame where Google's own
    // initial position (top-left, in their own page chrome) is
    // visible before our centered-fixed CSS resolves. MutationObserver
    // callbacks run as microtasks before the next paint, so applying
    // inline !important position styles here means the bframe is
    // already centered by the time the user sees the first frame.
    function pinBframe(el) {
        if (!el || el.tagName !== 'IFRAME') return;
        var src = el.getAttribute('src') || '';
        if (src.indexOf('recaptcha/api2/bframe') === -1) return;
        // setProperty with !important beats Google's inline styles
        // (which they don't mark !important) AND any of their CSS.
        el.style.setProperty('position', 'fixed', 'important');
        el.style.setProperty('top', '50%', 'important');
        el.style.setProperty('left', '50%', 'important');
        el.style.setProperty('transform', 'translate(-50%, -50%)', 'important');
        el.style.setProperty('z-index', '99999', 'important');
        el.style.setProperty('border', '0', 'important');
        // Apply matte right away too so it's there on the same frame.
        ensureCaptchaMatte();
    }
    var bframeObserver = new MutationObserver(function(mutations) {
        for (var i = 0; i < mutations.length; i++) {
            var added = mutations[i].addedNodes;
            for (var j = 0; j < added.length; j++) {
                var n = added[j];
                if (n.nodeType !== 1) continue;
                pinBframe(n);
                if (n.querySelectorAll) {
                    var nested = n.querySelectorAll('iframe[src*=\'recaptcha/api2/bframe\']');
                    for (var k = 0; k < nested.length; k++) pinBframe(nested[k]);
                }
            }
            if (mutations[i].type === 'attributes') pinBframe(mutations[i].target);
        }
    });
    bframeObserver.observe(document.body, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ['src', 'style']
    });

    var captchaTimer = setInterval(checkCaptcha, 500);
    setTimeout(function() {
        clearInterval(captchaTimer);
        bframeObserver.disconnect();
    }, 60000);

    send({ type: 'log', text: 'bsto JS bridge installed' });
})();
";

    // Injected on every NavigationCompleted while the overlay is in
    // VidmolyTurnstile mode. Re-runs on each navigation: first on the
    // Turnstile gate page (where we paint a themed backdrop + spinner
    // INSIDE the page so the user sees a continuous loading state
    // even when WebView2 briefly paints over Avalonia controls due
    // to airspace), then again after Cloudflare's /challenge-verify
    // redirects to the actual JWPlayer page (where we find the m3u8
    // in the page HTML or in fetch/XHR traffic and forward it back
    // to C#).
    //
    // The in-page spinner is what fixes the user-visible flicker: an
    // Avalonia spinner painted by the cover Border above the WebView
    // is at the mercy of (a) airspace — WebView2's native HWND paints
    // over Avalonia controls during navigation/heavy JS — and (b) the
    // Avalonia animation system, which runs on the UI thread and can
    // stutter under interop load. A CSS spinner inside the WebView
    // runs on Chromium's render thread and paints into WebView2's own
    // surface, so it's immune to both.
    //
    // Idempotency guard (`__vidmoly_patched__`) is per-document, so
    // each navigation gets a fresh install — the WebView creates a
    // new document on each top-level navigation, clearing window
    // properties.
    private const string InjectedJsVidmoly = @"
(function() {
    if (window.__vidmoly_patched__) return;
    window.__vidmoly_patched__ = true;

    function send(payload) {
        try {
            if (typeof invokeCSharpAction === 'function') {
                invokeCSharpAction(JSON.stringify(payload));
            }
        } catch (e) { /* ignore */ }
    }

    // Returns the first .m3u8 URL we can find inside `text`, trying
    // the JWPlayer setup shapes first (most specific) and falling
    // back to any quoted m3u8 URL anywhere in the HTML. Mirrors the
    // server-side _find_m3u8 in vidmoly.py — kept in sync so behavior
    // matches whether the page renders inline or via JS hydration.
    function findM3u8(text) {
        if (!text) return null;
        var patterns = [
            /sources\s*:\s*\[\s*\{\s*(?:\x22file\x22|file|\x22src\x22|src)\s*:\s*[\x22']([^\x22']+\.m3u8[^\x22']*)[\x22']/,
            /(?:\x22file\x22|\bfile|\x22src\x22|\bsrc)\s*:\s*[\x22']([^\x22']+\.m3u8[^\x22']*)[\x22']/,
            /[\x22']([^\x22']+\.m3u8[^\x22']*)[\x22']/
        ];
        for (var i = 0; i < patterns.length; i++) {
            var m = patterns[i].exec(text);
            if (m) return m[1];
        }
        return null;
    }

    var sentM3u8 = false;
    function reportM3u8(url) {
        if (sentM3u8 || !url) return;
        sentM3u8 = true;
        send({ type: 'log', text: 'vidmoly m3u8 captured: ' + url.substring(0, 120) });
        send({ type: 'm3u8_url', url: url });
    }

    function scanPage() {
        if (sentM3u8) return true;
        var url = findM3u8(document.documentElement.outerHTML);
        if (url) { reportM3u8(url); return true; }
        return false;
    }

    // FIRST: scan the page for an m3u8 in initial SSR HTML — common
    // on the JWPlayer page that Vidmoly redirects to once Turnstile
    // passes. If we find one here, m3u8_url goes out before any
    // vidmoly_ready, so C#'s UI-thread queue closes the overlay
    // without first sliding the WebView onscreen — which would
    // otherwise paint a 1-frame JWPlayer flash before the close
    // lands. Polling and reveal only kick in if this fast-path
    // misses, which is rare (delayed JWPlayer hydration).
    var m3u8FoundEarly = scanPage();

    // Theme the page to match the Avalonia overlay so when C# slides
    // the WebView onscreen the user sees a matching backdrop +
    // spinner — not Cloudflare's dark ""Security Check"" card. Style
    // insertion is idempotent across re-injections via the id check.
    if (!document.getElementById('mh-vidmoly-style')) {
        var style = document.createElement('style');
        style.id = 'mh-vidmoly-style';
        style.textContent = `
            html, body {
                background: __BACKDROP_COLOR__ !important;
                margin: 0 !important;
                padding: 0 !important;
                overflow: hidden !important;
            }
            /* Cloudflare's gate page chrome — hide it; the Turnstile
               iframe lives in a sibling and stays interactive. */
            .wrap, .card {
                background: transparent !important;
                border: none !important;
                box-shadow: none !important;
                backdrop-filter: none !important;
                -webkit-backdrop-filter: none !important;
            }
            .title, .desc, .error { display: none !important; }
            @keyframes mh-vidmoly-spin {
                from { transform: translate(-50%, -50%) rotate(0deg); }
                to   { transform: translate(-50%, -50%) rotate(360deg); }
            }
            #mh-vidmoly-spinner {
                position: fixed !important;
                top: 50% !important;
                left: 50% !important;
                width: 48px !important;
                height: 48px !important;
                z-index: 2147483647 !important;
                pointer-events: none !important;
                animation: mh-vidmoly-spin 1s linear infinite !important;
                transform-origin: 50% 50% !important;
            }
        `;
        (document.head || document.documentElement).appendChild(style);
    }
    // Mirror the Avalonia spinner shape (same Path data + stroke
    // weight + accent color) so the Avalonia → in-page handoff at
    // reveal time is visually seamless — same shape, same color,
    // just rendered by a different process.
    if (!document.getElementById('mh-vidmoly-spinner')) {
        var sp = document.createElement('div');
        sp.id = 'mh-vidmoly-spinner';
        sp.innerHTML =
            '<svg width=\x2248\x22 height=\x2248\x22 viewBox=\x220 0 24 24\x22 fill=\x22none\x22>' +
            '<path d=\x22M 21 12 a 9 9 0 1 1 -6.219 -8.56\x22 ' +
            'stroke=\x22__ACCENT_COLOR__\x22 stroke-width=\x222.4\x22 ' +
            'stroke-linecap=\x22round\x22 fill=\x22none\x22/>' +
            '</svg>';
        (document.body || document.documentElement).appendChild(sp);
    }
    // Only signal ready if the early scanPage missed. If m3u8_url
    // already went out (JWPlayer-page fast path), revealing here
    // would just briefly paint the JWPlayer page on screen before
    // C# closes the overlay one queue-tick later. Skipping the
    // reveal lets the close land while the cover is still up.
    if (!m3u8FoundEarly) {
        send({ type: 'vidmoly_ready' });
    }

    // Tell C# right before the page unloads (the Cloudflare gate
    // does `window.location.href = redirectTo` after Turnstile
    // passes, which fires beforeunload). C# pushes the WebView
    // offscreen and shows the cover so the next page's bare render
    // — JWPlayer page chrome with no theming yet — is hidden until
    // our CSS injection runs again on NavigationCompleted.
    window.addEventListener('beforeunload', function() {
        send({ type: 'vidmoly_navigating' });
    });
    // Patch fetch — covers two cases:
    //  1. Vidmoly's player sometimes loads the m3u8 master playlist
    //     via fetch (rather than embedding the URL inline in the
    //     JWPlayer setup), so we forward those when seen.
    //  2. The Cloudflare gate POSTs to /challenge-verify, and a
    //     successful response means a window.location.href redirect
    //     is imminent — the same signal as beforeunload, just a few
    //     ms earlier and not reliant on WebView2 honoring beforeunload.
    var origFetch = window.fetch;
    if (origFetch) {
        window.fetch = function(input, init) {
            var url = typeof input === 'string' ? input : (input && input.url);
            try {
                if (url && url.indexOf('.m3u8') !== -1) reportM3u8(url);
            } catch (e) {}
            var p = origFetch.apply(this, arguments);
            if (url && url.indexOf('/challenge-verify') !== -1) {
                p.then(function(r) {
                    if (!r || !r.ok) return;
                    r.clone().json().then(function(data) {
                        if (data && data.success && data.redirectTo) {
                            send({ type: 'vidmoly_navigating' });
                        }
                    }).catch(function() {});
                }).catch(function() {});
            }
            return p;
        };
    }
    // Patch XHR — same reason as fetch, different code path.
    var origSend = XMLHttpRequest.prototype.send;
    var origOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function(method, url) {
        try { if (url && url.indexOf('.m3u8') !== -1) reportM3u8(url); } catch (e) {}
        return origOpen.apply(this, arguments);
    };
    XMLHttpRequest.prototype.send = function() { return origSend.apply(this, arguments); };

    // Watch for the Turnstile widget — when it shows up, signal C# so
    // the overlay reveals (matching the bs.to flow's captcha_visible).
    // If Turnstile auto-passes invisibly, this fires anyway because
    // the iframe is briefly inserted; we also fire once we find the
    // m3u8 and just close. Reusing the bs.to message type so the
    // existing reveal path doesn't need a new branch.
    var notifiedVisible = false;
    function checkTurnstile() {
        if (notifiedVisible) return;
        var tf = document.querySelector('iframe[src*=\x22challenges.cloudflare.com/turnstile\x22]');
        if (!tf) return;
        var rect = tf.getBoundingClientRect();
        if (rect.width < 50 || rect.height < 30) return;
        notifiedVisible = true;
        send({ type: 'captcha_visible' });
        send({ type: 'log', text: 'turnstile visible (' + rect.width + 'x' + rect.height + ')' });
    }

    // If the early scanPage didn't already capture m3u8, poll for
    // delayed JWPlayer hydration. Vidmoly's embed page usually has
    // the m3u8 in SSR HTML, but some templates inject JWPlayer via
    // a setTimeout. checkTurnstile is also driven from this poll so
    // it gets cleaned up alongside the m3u8 hunt once we're done.
    if (!m3u8FoundEarly) {
        var attempts = 0;
        var poll = setInterval(function() {
            checkTurnstile();
            if (scanPage() || ++attempts > 120) clearInterval(poll);
        }, 500);
    }

    send({ type: 'log', text: 'vidmoly JS bridge installed at ' + location.href });
})();
";
}
