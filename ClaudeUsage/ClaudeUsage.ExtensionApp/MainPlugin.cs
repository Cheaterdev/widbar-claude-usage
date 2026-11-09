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
    private UsageSnapshot? _snapshot;
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
        // Colour thresholds (percent used)
        public double WarnAt { get; set; } = 80;
        public double CritAt { get; set; } = 95;
        // Data
        public int RefreshMinutes { get; set; } = 1;
        public bool IncludeCacheReads { get; set; } = false;
        public string? DataFolder { get; set; }
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
            var snap = await Task.Run(() => UsageReader.Read(folder));
            _snapshot = snap;
            // Real plan limits: at most every 5 minutes, and never while a sign-in is open.
            if (!_signingIn && DateTime.UtcNow - _planFetchedAt > TimeSpan.FromMinutes(Math.Max(5, _settings.RefreshMinutes)))
            {
                var plan = await PlanUsage.FetchAsync();
                _planFetchedAt = DateTime.UtcNow;
                // A transient error keeps the last good numbers on screen.
                if (plan.State != PlanState.Error || _plan?.State != PlanState.Ok) _plan = plan;
            }
            _dispatcher?.TryEnqueue(() => { RenderPreview(); RenderFlyout(); });
        }
        catch { /* keep showing the last snapshot */ }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
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
        _g1?.Set(v.Session, ArcRing.CoralHot);
        _g2?.Set(v.Week, ArcRing.BlueHot);
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
        var day = _snapshot is null ? 0 : Percent(_snapshot.Today, st.DayLimitM, st.IncludeCacheReads);
        return (session, week, day);
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

    private double Percent(Totals t, double limitM) => Percent(t, limitM, _settings.IncludeCacheReads);

    private static double Percent(Totals t, double limitM, bool cache) =>
        limitM <= 0 ? 0 : t.Headline(cache) / (limitM * 1_000_000d) * 100;

    private void RenderFlyout()
    {
        if (_flyoutRoot is null) return;
        var cache = _settings.IncludeCacheReads;
        var p = _flyoutRoot.Children;
        p.Clear();

        p.Add(new TextBlock { Text = "Claude usage", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        if (_snapshot is not { } s)
        {
            p.Add(new TextBlock { Text = "Reading usage…", Opacity = 0.6 });
            return;
        }
        if (s.Messages30 == 0)
        {
            p.Add(new TextBlock
            {
                Text = "No Claude Code usage found in the last 30 days.\nLooked in: " + (_settings.DataFolder ?? UsageReader.DefaultRoot),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
            });
            return;
        }

        var row = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition());
        void Ring(int col, string label, double pct, string subtitle, Windows.UI.Color color)
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
                Foreground = new SolidColorBrush(ArcRing.Warn(color, pct)),
            });
            body.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            var card = Card(body);
            Grid.SetColumn(card, col);
            row.Children.Add(card);
        }
        string Local(Totals t, double limitM) => $"{UsageReader.Compact(t.Headline(cache))} / {limitM:0.##}M";
        string Reset(DateTimeOffset? r) => r is { } v ? "Resets " + v.ToLocalTime().ToString("ddd h:mm tt") : "";
        if (_plan?.Limits is { } pl)
        {
            Ring(0, "5-hour limit", pl.SessionPct, Reset(pl.SessionResets), ArcRing.Coral);
            Ring(1, "Weekly limit", pl.WeekPct, Reset(pl.WeekResets), ArcRing.Blue);
        }
        else
        {
            Ring(0, "5 hours", Percent(s.Last5Hours, _settings.SessionLimitM), Local(s.Last5Hours, _settings.SessionLimitM), ArcRing.Coral);
            Ring(1, "7 days", Percent(s.Last7Days, _settings.WeekLimitM), Local(s.Last7Days, _settings.WeekLimitM), ArcRing.Blue);
        }
        Ring(2, "Today", Percent(s.Today, _settings.DayLimitM), Local(s.Today, _settings.DayLimitM), ArcRing.Teal);
        p.Add(row);

        p.Add(SectionHeader("Estimated API cost"));
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
                Text = $"{UsageReader.Compact(t.Headline(cache))} tokens",
                FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center,
            });
            var card = Card(body);
            Grid.SetColumn(card, col);
            costs.Children.Add(card);
        }
        Cost(0, "Today", s.Today);
        Cost(1, "7 days", s.Last7Days);
        Cost(2, "30 days", s.Last30Days);
        p.Add(costs);

        if (_plan?.State == PlanState.NeedsSignIn || (_plan?.State == PlanState.Error && _plan.Limits is null))
        {
            var msg = new TextBlock
            {
                Text = _plan.State == PlanState.NeedsSignIn
                    ? "Sign in to Claude to show your real plan limits. Until then the rings use the budgets from settings."
                    : $"Couldn't read plan limits ({_plan.Message}). Showing local estimates.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7,
            };
            p.Add(msg);
            if (_plan.State == PlanState.NeedsSignIn)
            {
                var btn = new Button
                {
                    Content = _signingIn ? "Waiting for sign-in…" : "Sign in to Claude",
                    IsEnabled = !_signingIn && PlanUsage.ClaudeExe() is not null,
                };
                btn.Click += async (_, _) => await SignInFlowAsync();
                p.Add(btn);
                if (PlanUsage.ClaudeExe() is null)
                    p.Add(new TextBlock { Text = "Claude Code (claude.exe) was not found; install it to sign in.", FontSize = 11, Opacity = 0.6 });
            }
        }

        p.Add(SectionHeader("Last 7 days"));
        var max = Math.Max(1, s.Daily7.Max(d => d.Totals.Headline(cache)));
        var days = new StackPanel { Spacing = 6 };
        foreach (var (day, t) in s.Daily7)
            days.Children.Add(BarRow(day.ToString("ddd d"), t.Headline(cache), max));
        p.Add(days);

        p.Add(SectionHeader("By model · 30 days (est. cost)"));
        var mmax = Math.Max(1, s.ByModel30.Max(m => m.Totals.Headline(cache)));
        var models = new StackPanel { Spacing = 6 };
        foreach (var (model, t) in s.ByModel30.Take(6))
            models.Children.Add(BarRow(UsageReader.FriendlyModel(model), t.Headline(cache), mmax, UsageReader.Money(t.Cost)));
        p.Add(models);

        var note = cache
            ? "Totals include cache reads."
            : $"Cache reads not counted ({UsageReader.Compact(s.Last30Days.CacheRead)} in 30 days).";
        p.Add(new TextBlock
        {
            Text = $"{note} Costs = tokens x Anthropic's published API prices, not your plan bill. Source: Claude Code logs. Updated {s.GeneratedAt:HH:mm}.",
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
