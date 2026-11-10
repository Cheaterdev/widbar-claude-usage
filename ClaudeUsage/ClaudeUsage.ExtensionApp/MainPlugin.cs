using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WidBar.SDK;

namespace ClaudeUsage.ExtensionApp;

// Claude usage widget: today's token count on the taskbar, a breakdown of the
// last 5 hours / 7 days / 30 days plus per-model usage in the flyout. Data comes
// from Claude Code's local session logs, so no sign-in is required.
public sealed partial class MainPlugin :
    WidgetPluginBase,
    IConfigurableWidgetPlugin,
    IWidgetFlyoutLifecycle
{
    private Settings _settings = new();
    private UsageSnapshot? _snapshot;          // every computer: drives the gauges
    private UsageSnapshot? _view;              // the computer picked in the flyout: costs, chart, models
    private List<Bucket> _buckets = [];
    private DateTime? _weekStart;
    private string? _filter;                   // null = all computers
    private Grid? _previewRoot;
    private PreviewGauge? _g1, _g2, _g3;
    private StackPanel? _flyoutRoot;
    private DispatcherQueue? _dispatcher;
    private DispatcherTimer? _timer;
    private int _refreshing;
    private PlanResult? _plan;
    private DateTime _planFetchedAt = DateTime.MinValue;
    private bool _signingIn;

    public override string Id => "com.claudeusage.widget";
    public override string Name => "Claude Usage";

    public override int PreviewLogicalWidth => WidthFor(_settings);
    public override int FlyoutWidth => 360;
    public override int FlyoutHeight => 640;

    private sealed class Settings
    {
        // Taskbar view
        public bool ShowSession { get; set; } = true;
        public bool ShowWeek { get; set; } = true;
        public bool ShowDay { get; set; } = false;
        public bool ShowLabels { get; set; } = true;
        public bool ShowTimeLeft { get; set; } = true;
        public bool ShowFlyoutTime { get; set; } = true;
        public bool ShowPace { get; set; } = true;
        public bool HideFooter { get; set; } = false;
        // Colour thresholds (percent used)
        public double WarnAt { get; set; } = 80;
        public double CritAt { get; set; } = 95;
        // Data
        public int RefreshMinutes { get; set; } = 1;
        public bool IncludeCacheReads { get; set; } = false;
        public string? DataFolder { get; set; }
        // Optional folder shared between computers (synced by the user) for combined statistics.
        public string? SyncFolder { get; set; }
        // Token budgets in millions. Used when plan limits aren't available
        // (not signed in), and for the Today ring.
        public double SessionLimitM { get; set; } = 2;
        public double DayLimitM { get; set; } = 5;
        public double WeekLimitM { get; set; } = 20;

        // Thresholds must be ordered. With every gauge off the taskbar shows just the name.
        public Settings Normalize()
        {
            if (CritAt <= WarnAt) CritAt = Math.Min(100, WarnAt + 1);
            if (RefreshMinutes < 1) RefreshMinutes = 1;
            return this;
        }

        public static Settings FromJson(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new Settings()
                    : (JsonSerializer.Deserialize<Settings>(json) ?? new Settings()).Normalize();
            }
            catch { return new Settings(); }
        }

        public string ToJson() => JsonSerializer.Serialize(this);
    }

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _settings = Settings.FromJson(context.SettingsJson);
        ApplyThresholds(_settings);
        await base.InitializeAsync(context);
        context.PreviewVisibilityChanged += OnPreviewVisibilityChanged;
    }

    public override UIElement? CreatePreviewContent()
    {
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        _previewRoot = new Grid { Background = null };
        RebuildPreview();

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_settings.RefreshMinutes) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        SetUpdatesEnabled(Context?.IsPreviewVisible ?? true);
        return _previewRoot;
    }

    public override UIElement? CreateFlyoutContent()
    {
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        _flyoutRoot = new StackPanel { Spacing = 14, Padding = new Thickness(20) };
        RenderFlyout();
        return new ScrollViewer { Content = _flyoutRoot };
    }

    public override void OnSettingsDraftChanged(string settingsJson)
    {
        _settings = Settings.FromJson(settingsJson);
        ApplyThresholds(_settings);
        if (_timer is not null) _timer.Interval = TimeSpan.FromMinutes(_settings.RefreshMinutes);
        RebuildPreview();
        RenderFlyout();
        _ = RefreshAsync();
    }

    public void OnFlyoutShown()
    {
        RenderFlyout();
        _ = RefreshAsync();
    }

    public void OnFlyoutHidden() { }

    public override ValueTask DisposeAsync()
    {
        if (Context is not null) Context.PreviewVisibilityChanged -= OnPreviewVisibilityChanged;
        _timer?.Stop();
        _timer = null;
        _previewRoot = null;
        _g1 = _g2 = _g3 = null;
        _flyoutRoot = null;
        return ValueTask.CompletedTask;
    }

    private void OnPreviewVisibilityChanged(object? sender, bool visible) => SetUpdatesEnabled(visible);

    private void SetUpdatesEnabled(bool visible)
    {
        if (visible)
        {
            _timer?.Start();
            _ = RefreshAsync();
        }
        else
        {
            _timer?.Stop();
        }
    }

    // Log parsing can touch many MB, so it runs off the UI thread; overlapping
    // refreshes are dropped.
    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var folder = _settings.DataFolder;
            await Pricing.EnsureAsync();
            // Real plan limits: at most every 5 minutes, and never while a sign-in is open.
            if (!_signingIn && DateTime.UtcNow - _planFetchedAt > TimeSpan.FromMinutes(Math.Max(5, _settings.RefreshMinutes)))
            {
                var plan = await PlanUsage.FetchAsync();
                _planFetchedAt = DateTime.UtcNow;
                // A transient error keeps the last good numbers on screen.
                if (plan.State != PlanState.Error || _plan?.State != PlanState.Ok) _plan = plan;
            }
            // The weekly window started 7 days before it resets.
            var weekStart = _plan?.Limits?.WeekResets?.ToLocalTime().DateTime.AddDays(-7);
            var sync = _settings.SyncFolder;
            _buckets = await Task.Run(() => UsageReader.ReadBuckets(folder, sync));
            _weekStart = weekStart;
            Recompute();
            _dispatcher?.TryEnqueue(() => { if (Loc.Refresh()) RebuildPreview(); RenderPreview(); RenderFlyout(); });
        }
        catch { /* keep showing the last snapshot */ }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    private const string RepoUrl = "https://github.com/Cheaterdev/widbar-claude-usage";

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // Claude Code's official page (download and install instructions).
    private static void OpenClaudeCodeSite() => OpenUrl("https://claude.com/claude-code");

    private void Recompute()
    {
        _snapshot = UsageReader.Aggregate(_buckets, _weekStart);
        if (_filter is not null && !(_snapshot.Sources?.Contains(_filter) ?? false)) _filter = null;
        _view = _filter is null ? _snapshot : UsageReader.Aggregate(_buckets, _weekStart, _filter);
    }

    private void RenderPreview()
    {
        if (_g1 is null && _g2 is null && _g3 is null) return;
        var p = Percents(_settings);
        if (p is not { } v)
        {
            foreach (var g in new[] { _g1, _g2, _g3 }) if (g is not null) g.Text.Text = "…";
            return;
        }
        _g1?.Set(v.Session, ArcRing.CoralHot, _plan?.Limits?.SessionResets);
        _g2?.Set(v.Week, ArcRing.BlueHot, _plan?.Limits?.WeekResets);
        _g3?.Set(v.Day, ArcRing.TealHot);
    }

    // Real plan percentages for 5h and week when signed in, otherwise local
    // estimates against the token budgets. Today has no plan limit, so it is
    // always measured against the daily budget.
    private (double Session, double Week, double Day)? Percents(Settings st)
    {
        if (_snapshot is null && _plan?.Limits is null) return null;
        double session, week;
        if (_plan?.Limits is { } pl) { session = pl.SessionPct; week = pl.WeekPct; }
        else
        {
            session = Percent(_snapshot!.Last5Hours, st.SessionLimitM, st.IncludeCacheReads);
            week = Percent(_snapshot.Last7Days, st.WeekLimitM, st.IncludeCacheReads);
        }
        var day = TodayShare(out _, out _) is { } share ? share
            : _snapshot is null ? 0 : Percent(_snapshot.Today, st.DayLimitM, st.IncludeCacheReads);
        return (session, week, day);
    }

    // Today as a share of the week. Today's part of the weekly percentage is the
    // weekly percentage split by cost (tokens inside the current weekly window).
    // The ring shows that against today's allowance: what is left of the week right now,
    // spread over the days that remain. Null without a sign-in.
    private double? TodayShare(out double weekPart, out double allowance)
    {
        weekPart = 0; allowance = 0;
        if (_plan?.Limits is not { WeekResets: { } reset } pl || _snapshot is not { WeekWindow: { } ww, TodayInWindow: { } tw }) return null;
        var share = ww.Cost > 0 ? tw.Cost / ww.Cost : ww.Headline(false) > 0 ? (double)tw.Headline(false) / ww.Headline(false) : 0;
        weekPart = pl.WeekPct * Math.Clamp(share, 0, 1);
        var daysLeft = Math.Max(1, Math.Ceiling((reset - DateTimeOffset.Now).TotalDays));
        // Based on what is left right now, so it shrinks as the week is used up.
        allowance = Math.Max(0.1, (100 - pl.WeekPct) / daysLeft);
        return weekPart / allowance * 100;
    }

    private static void ApplyThresholds(Settings st)
    {
        ArcRing.WarnAt = st.WarnAt;
        ArcRing.CritAt = st.CritAt;
    }

    private async Task SignInFlowAsync()
    {
        _signingIn = true;
        RenderFlyout();
        await PlanUsage.SignInAsync();
        _signingIn = false;
        _planFetchedAt = DateTime.MinValue;
        await RefreshAsync();
    }

    private static string L(string key, params object[] args) => Loc.T(key, args);

    private double Percent(Totals t, double limitM) => Percent(t, limitM, _settings.IncludeCacheReads);

    private static double Percent(Totals t, double limitM, bool cache) =>
        limitM <= 0 ? 0 : t.Headline(cache) / (limitM * 1_000_000d) * 100;

    private void RenderFlyout()
    {
        if (_flyoutRoot is null) return;
        var cache = _settings.IncludeCacheReads;
        var p = _flyoutRoot.Children;
        p.Clear();

        p.Add(new TextBlock { Text = L("Claude usage"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        if (_snapshot is not { } s)
        {
            p.Add(new TextBlock { Text = L("Reading usage…"), Opacity = 0.6 });
            return;
        }
        // No local usage: everything still shows (limits, zero cost, empty history) and a note goes in the footer.
        var noUsage = s.Messages30 == 0;

        var v = _view ?? s;

        var row = new Grid { ColumnSpacing = 8 };
        void Ring(int col, string label, double pct, string subtitle, Windows.UI.Color color,
                  DateTimeOffset? resets = null, TimeSpan window = default)
        {
            var ring = new ArcRing(76, 8);
            ring.SetValue(pct, color);
            var center = new TextBlock
            {
                Text = $"{pct:0}%",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var dial = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            dial.Children.Add(ring);
            dial.Children.Add(center);

            var body = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            body.Children.Add(dial);
            body.Children.Add(new TextBlock
            {
                Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ArcRing.Warn(color, pct)),
            });
            body.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            if (resets is { } rs && window > TimeSpan.Zero && (_settings.ShowFlyoutTime || _settings.ShowPace))
            {
                // Thin bar: how much of the limit window has already passed.
                var elapsed = Math.Clamp(1 - (rs - DateTimeOffset.Now).TotalSeconds / window.TotalSeconds, 0, 1);
                var bar = new Grid { Width = 72, Height = 3, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(elapsed, 0.0001), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - elapsed, 0.0001), GridUnitType.Star) });
                var track = new Microsoft.UI.Xaml.Controls.Border
                {
                    CornerRadius = new CornerRadius(1.5),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                };
                Grid.SetColumnSpan(track, 2);
                bar.Children.Add(track);
                var fill = new Microsoft.UI.Xaml.Controls.Border { CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(color) };
                bar.Children.Add(fill);
                if (_settings.ShowFlyoutTime) body.Children.Insert(body.Children.Count - 1, bar);

                // Pace: usage so far against the share of the window that has passed.
                if (_settings.ShowPace)
                {
                    var diff = pct - elapsed * 100;
                    var (paceText, paceColor) = Math.Abs(diff) < 5 ? (L("On pace"), ArcRing.Teal)
                        : diff > 0 ? (L("{0}% ahead", $"{diff:0}"), ArcRing.Amber)
                        : (L("{0}% behind", $"{-diff:0}"), ArcRing.Blue);
                    body.Children.Insert(body.Children.Count - 1, new TextBlock
                    {
                        Text = paceText, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = new SolidColorBrush(paceColor),
                    });
                }
            }
            var card = Card(body);
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(card, row.ColumnDefinitions.Count - 1);
            row.Children.Add(card);
        }
        string Local(Totals t, double limitM) => $"{UsageReader.Compact(t.Headline(cache))} / {limitM:0.##}M";
        string Reset(DateTimeOffset? r)
        {
            if (r is not { } v || !_settings.ShowFlyoutTime) return "";
            var local = v.ToLocalTime();
            var when = local.Date == DateTime.Today ? local.ToString("t", Loc.Culture) : local.ToString("ddd", Loc.Culture) + " " + local.ToString("t", Loc.Culture);
            return L("{0} left", Left(v)) + "\n" + L("Resets {0}", when);
        }
        if (_plan?.Limits is { } pl)
        {
            if (pl.HasSession) Ring(0, L("5-hour limit"), pl.SessionPct, Reset(pl.SessionResets), ArcRing.Coral, pl.SessionResets, TimeSpan.FromHours(5));
            if (pl.HasWeek) Ring(1, L("Weekly limit"), pl.WeekPct, Reset(pl.WeekResets), ArcRing.Blue, pl.WeekResets, TimeSpan.FromDays(7));
        }
        else
        {
            Ring(0, L("5 hours"), Percent(s.Last5Hours, _settings.SessionLimitM), Local(s.Last5Hours, _settings.SessionLimitM), ArcRing.Coral);
            Ring(1, L("7 days"), Percent(s.Last7Days, _settings.WeekLimitM), Local(s.Last7Days, _settings.WeekLimitM), ArcRing.Blue);
        }
        if (TodayShare(out var weekPart, out var allowance) is { } dayPct)
            Ring(2, L("Today"), dayPct, L("{0}% of weekly limit", $"{weekPart:0.#}") + "\n" + L("Allowance {0}% / day", $"{allowance:0.#}"), ArcRing.Teal);
        else
            Ring(2, L("Today"), Percent(s.Today, _settings.DayLimitM), Local(s.Today, _settings.DayLimitM), ArcRing.Teal);
        p.Add(row);

        // Filter for everything below (the rings above are account-wide).
        if (s.Sources is { Count: > 1 } sources)
        {
            var all = L("All computers");
            var names = new List<string?> { null };
            names.AddRange(sources);
            var pick = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var n in names) pick.Items.Add(n is null ? all : n == Environment.MachineName ? L("{0} (this PC)", n) : n);
            pick.SelectedIndex = Math.Max(0, names.IndexOf(_filter));
            pick.SelectionChanged += (_, _) =>
            {
                if (pick.SelectedIndex < 0 || names[pick.SelectedIndex] == _filter) return;
                _filter = names[pick.SelectedIndex];
                _dispatcher?.TryEnqueue(() => { Recompute(); RenderFlyout(); });
            };
            p.Add(pick);
        }

        p.Add(SectionHeader(L("Estimated API cost")));
        var costs = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < 3; i++) costs.ColumnDefinitions.Add(new ColumnDefinition());
        void Cost(int col, string label, Totals t)
        {
            var body = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            body.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });
            body.Children.Add(new TextBlock { Text = UsageReader.Money(t.Cost), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            // Actual tokens behind the estimate (cache reads only if enabled in settings).
            body.Children.Add(new TextBlock
            {
                Text = L("{0} tokens", UsageReader.Compact(t.Headline(cache))),
                FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center,
            });
            var card = Card(body);
            Grid.SetColumn(card, col);
            costs.Children.Add(card);
        }
        Cost(0, L("Today"), v.Today);
        Cost(1, L("7 days"), v.Last7Days);
        Cost(2, L("30 days"), v.Last30Days);
        p.Add(costs);

        // Extra usage credits (a monthly spending cap on top of the plan), when enabled.
        if (_plan?.Limits?.Extra is { Enabled: true, Limit: { } creditCap } credits)
        {
            var spent = credits.Used ?? 0;
            var leftCents = Math.Max(0, creditCap - spent);
            var usedPct = creditCap > 0 ? Math.Clamp(spent / creditCap * 100, 0, 100) : 0;
            p.Add(SectionHeader(L("Extra usage credits")));
            var cbody = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
            cbody.Children.Add(new TextBlock
            {
                Text = L("{0} left", UsageReader.Money(leftCents / 100.0)),
                FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            });
            cbody.Children.Add(new ProgressBar
            {
                Minimum = 0, Maximum = 100, Value = usedPct, Height = 6,
                Foreground = new SolidColorBrush(ArcRing.Warn(ArcRing.Teal, usedPct)),
            });
            cbody.Children.Add(new TextBlock
            {
                Text = L("of {0} · used {1}", UsageReader.Money(creditCap / 100.0), UsageReader.Money(spent / 100.0)),
                FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center,
            });
            p.Add(Card(cbody));
        }

        if (_plan?.State == PlanState.NeedsSignIn || (_plan?.State == PlanState.Error && _plan.Limits is null))
        {
            var missing = _plan.State == PlanState.NeedsSignIn && PlanUsage.ClaudeExe() is null;
            var msg = new TextBlock
            {
                Text = missing
                    ? L("Claude Code was not found on this computer. Install it to show your real plan limits. Until then the rings use the budgets from settings.")
                    : _plan.State == PlanState.NeedsSignIn
                    ? L("Sign in to Claude to show your real plan limits. Until then the rings use the budgets from settings.")
                    : L("Couldn't read plan limits ({0}). Showing local estimates.", _plan.Message ?? ""),
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7,
            };
            p.Add(msg);
            if (missing)
            {
                var get = new Button { Content = L("Get Claude Code") };
                get.Click += (_, _) => OpenClaudeCodeSite();
                p.Add(get);
            }
            else if (_plan.State == PlanState.NeedsSignIn)
            {
                var btn = new Button
                {
                    Content = _signingIn ? L("Waiting for sign-in…") : L("Sign in to Claude"),
                    IsEnabled = !_signingIn && PlanUsage.ClaudeExe() is not null,
                };
                btn.Click += async (_, _) => await SignInFlowAsync();
                p.Add(btn);
            }
        }

        p.Add(SectionHeader(L("Last 7 days")));
        var max = Math.Max(1, v.Daily7.Max(d => d.Totals.Headline(cache)));
        var days = new StackPanel { Spacing = 6 };
        foreach (var (day, t) in v.Daily7)
            days.Children.Add(BarRow(day.ToString("ddd d", Loc.Culture), t.Headline(cache), max));
        p.Add(days);

        if (v.ByModel30.Count > 0)
        {
        p.Add(SectionHeader(L("By model · 30 days (est. cost)")));
        var mmax = Math.Max(1, v.ByModel30.Select(m => m.Totals.Headline(cache)).DefaultIfEmpty(0).Max());
        var models = new StackPanel { Spacing = 6 };
        foreach (var (model, t) in v.ByModel30.Take(6))
            models.Children.Add(BarRow(UsageReader.FriendlyModel(model), t.Headline(cache), mmax, UsageReader.Money(t.Cost)));
        p.Add(models);
        }

        var note = cache
            ? L("Totals include cache reads.")
            : L("Cache reads not counted ({0} in 30 days).", UsageReader.Compact(v.Last30Days.CacheRead));
        var footer = L("{0} Costs = tokens x Anthropic's published API prices, not your plan bill. Source: Claude Code logs. Updated {1}.", note, s.GeneratedAt.ToString("t", Loc.Culture));
        footer += "\n" + L("Unofficial: not affiliated with Anthropic.");
        var noUsageNote = L("No Claude Code usage found in the last 30 days.\nLooked in: {0}", _settings.DataFolder ?? UsageReader.DefaultRoot)
            + "\n" + L("Use Claude on other computers? Set a shared folder in Settings.");
        // With the footer hidden only the troubleshooting hint for missing usage is kept.
        if (_settings.HideFooter) footer = noUsage ? noUsageNote : "";
        else if (noUsage) footer = noUsageNote + "\n\n" + footer;
        if (footer.Length > 0) p.Add(new TextBlock
        {
            Text = footer,
            FontSize = 11, Opacity = 0.5, TextWrapping = TextWrapping.Wrap,
        });
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static Border Card(UIElement child) => new()
    {
        Child = child,
        Padding = new Thickness(8, 12, 8, 12),
        CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
    };

    private static Grid BarRow(string label, long value, long max, string? text = null)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        g.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var bar = new ProgressBar { Minimum = 0, Maximum = max, Value = value, Height = 6, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(ArcRing.Coral) };
        Grid.SetColumn(bar, 1);
        g.Children.Add(bar);
        var num = new TextBlock { Text = text ?? UsageReader.Compact(value), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(num, 2);
        g.Children.Add(num);
        return g;
    }
}
