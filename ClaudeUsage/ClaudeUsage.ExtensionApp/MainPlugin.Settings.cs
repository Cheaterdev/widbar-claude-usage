using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WidBar.SDK;

namespace ClaudeUsage.ExtensionApp;

// Settings page and the taskbar gauges it previews.
public sealed partial class MainPlugin
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // Opens the system folder picker. The settings window is the foreground window
    // right after the Browse click, so it is used as the dialog's owner.
    private static async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch { return null; }
    }

    // A path box with a Browse button next to it.
    private FrameworkElement FolderBox(string? value, string placeholder, Action<string?> set)
    {
        var box = new TextBox { Width = 220, PlaceholderText = placeholder, Text = value ?? "" };
        box.TextChanged += (_, _) => set(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        var browse = new Button { Content = L("Browse…") };
        browse.Click += async (_, _) =>
        {
            var path = await PickFolderAsync();
            if (path is not null) box.Text = path;
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(box);
        panel.Children.Add(browse);
        return panel;
    }

    // ---- Taskbar gauges (shared by the taskbar preview and the settings preview) ----

    private sealed record PreviewGauge(ArcRing Ring, TextBlock Text, UIElement View, TextBlock? Label, string Name, bool TimeLeft)
    {
        // With "time left" on and a known reset time, the small label shows the
        // countdown (2h 14m) instead of the name (5h).
        public void Set(double pct, Windows.UI.Color color, DateTimeOffset? resets = null)
        {
            Ring.SetValue(pct, color);
            Text.Text = $"{pct:0}%";
            if (Label is not null)
                Label.Text = TimeLeft && resets is { } r ? Left(r) : Name;
        }
    }

    private static string Left(DateTimeOffset reset)
    {
        var t = reset - DateTimeOffset.Now;
        if (t <= TimeSpan.Zero) return Loc.T(L("now"));
        if (t.TotalDays >= 1) return Loc.T("{0}d {1}h", (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1) return Loc.T("{0}h {1}m", (int)t.TotalHours, t.Minutes);
        return Loc.T("{0}m", Math.Max(1, t.Minutes));
    }

    // Slot width: padding + 58 px per gauge + 10 px between gauges.
    private static int WidthFor(Settings st)
    {
        var n = (st.ShowSession ? 1 : 0) + (st.ShowWeek ? 1 : 0) + (st.ShowDay ? 1 : 0);
        if (n == 0) return 80; // just the two-line name
        // Countdown labels (2h 14m) can be wider than the percentage, so those gauges get 6 px more.
        var wide = st.ShowLabels && st.ShowTimeLeft ? (st.ShowSession ? 1 : 0) + (st.ShowWeek ? 1 : 0) : 0;
        return 24 + n * 58 + wide * 6 + (n - 1) * 10;
    }

    // A ring beside a two-line label like the CPU widget: small coloured name on
    // top, value below. Metrics were measured from the CPU and Pomodoro widgets.
    private static PreviewGauge BuildGauge(string name, Windows.UI.Color color, bool labels, bool timeLeft)
    {
        TextBlock? nameLabel = null;
        // 22 px outer diameter, 2.5 px stroke.
        var ring = new ArcRing(22, 2.5) { VerticalAlignment = VerticalAlignment.Center };
        ring.SetValue(0, color);
        var text = new TextBlock
        {
            Text = "…",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            LineHeight = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var view = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };

        if (labels)
        {
            ring.Margin = new Thickness(0, 0, 0, 2);
            text.Margin = new Thickness(0, -3, 0, 0); // value top = label top + 11 px
            var label = new TextBlock
            {
                Text = name,
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
                LineHeight = 11,
                RenderTransform = new TranslateTransform { Y = -1 }, // 1 px up, no layout change
            };
            nameLabel = label;
            var lines = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
            lines.Children.Add(label);
            lines.Children.Add(text);
            view.Children.Add(ring);
            view.Children.Add(lines);
        }
        else
        {
            view.Children.Add(ring);
            view.Children.Add(text);
        }
        return new PreviewGauge(ring, text, view, nameLabel, name, timeLeft);
    }

    private static (StackPanel Row, PreviewGauge? G1, PreviewGauge? G2, PreviewGauge? G3) BuildGaugeRow(Settings st)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        PreviewGauge? g1 = null, g2 = null, g3 = null;
        if (st.ShowSession) { g1 = BuildGauge(L("5h"), ArcRing.CoralHot, st.ShowLabels, st.ShowTimeLeft); row.Children.Add(g1.View); }
        if (st.ShowWeek) { g2 = BuildGauge(L("Week"), ArcRing.BlueHot, st.ShowLabels, st.ShowTimeLeft); row.Children.Add(g2.View); }
        if (st.ShowDay) { g3 = BuildGauge(L("Today (short)"), ArcRing.TealHot, st.ShowLabels, false); row.Children.Add(g3.View); }
        if (g1 is null && g2 is null && g3 is null)
        {
            // No gauge selected: just the name, on two lines.
            var name = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            foreach (var word in new[] { L("Claude"), L("Usage") })
                name.Children.Add(new TextBlock
                {
                    Text = word,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    LineHeight = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            row.Children.Add(name);
        }
        return (row, g1, g2, g3);
    }

    private void RebuildPreview()
    {
        if (_previewRoot is null) return;
        var (row, g1, g2, g3) = BuildGaugeRow(_settings);
        _g1 = g1;
        _g2 = g2;
        _g3 = g3;
        _previewRoot.Children.Clear();
        _previewRoot.Children.Add(row);
        RenderPreview();
    }

    // ---- Settings page ----

    private const string IconFont = "Segoe Fluent Icons";

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock Muted(string text, double size = 12) =>
        new() { Text = text, FontSize = size, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Section(string title, string subtitle)
    {
        var p = new StackPanel { Spacing = 2, Margin = new Thickness(2, 18, 0, 6) };
        p.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        p.Children.Add(Muted(subtitle));
        return p;
    }

    // One settings card: icon, title and subtitle on the left, the control on the right.
    private static Border Row(string glyph, string title, string subtitle, FrameworkElement control, out TextBlock subtitleBlock)
    {
        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        g.Children.Add(new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFont), FontSize = 18, VerticalAlignment = VerticalAlignment.Center });

        subtitleBlock = Muted(subtitle);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(subtitleBlock);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        g.Children.Add(control);

        return new Border
        {
            Child = g,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(6),
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    private static Border Row(string glyph, string title, string subtitle, FrameworkElement control) =>
        Row(glyph, title, subtitle, control, out _);

    private UIElement? BuildSettings(IWidgetSettingsContext context)
    {
        var draft = Settings.FromJson(context.SettingsJson);

        // ---- live preview card ----
        var previewHost = new Border
        {
            MinHeight = 64,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
            Padding = new Thickness(12),
        };
        var widthText = Muted("");
        void RefreshPreview()
        {
            var (row, g1, g2, g3) = BuildGaugeRow(draft);
            // Real numbers when known, otherwise sample values so the colours show.
            var p = Percents(draft) ?? (42, 18, 30);
            g1?.Set(p.Session, ArcRing.CoralHot, _plan?.Limits?.SessionResets ?? DateTimeOffset.Now.AddMinutes(134));
            g2?.Set(p.Week, ArcRing.BlueHot, _plan?.Limits?.WeekResets ?? DateTimeOffset.Now.AddHours(75));
            g3?.Set(p.Day, ArcRing.TealHot);
            previewHost.Child = row;
            widthText.Text = L("Width {0} px", WidthFor(draft));
        }

        var previewHeader = new Grid();
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition());
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = L("Live preview"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(Muted(L("The taskbar widget with the current settings.")));
        previewHeader.Children.Add(titles);
        Grid.SetColumn(widthText, 1);
        widthText.VerticalAlignment = VerticalAlignment.Top;
        previewHeader.Children.Add(widthText);

        var previewBody = new StackPanel { Spacing = 12 };
        previewBody.Children.Add(previewHeader);
        previewBody.Children.Add(previewHost);
        var previewCard = new Border
        {
            Child = previewBody,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(6),
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };

        // Save, apply thresholds, and refresh both previews after any change.
        void Changed()
        {
            draft.Normalize();
            ApplyThresholds(draft);
            context.SaveSettings(draft.ToJson());
            context.RequestPreviewRefresh();
            RefreshPreview();
        }

        ToggleSwitch Toggle(bool on, Action<bool> set)
        {
            var t = new ToggleSwitch { IsOn = on, OnContent = L("On"), OffContent = L("Off"), MinWidth = 0 };
            t.Toggled += (_, _) => { set(t.IsOn); Changed(); };
            return t;
        }

        NumberBox Number(double value, double min, double max, double step, Action<double> set, double width = 130)
        {
            var box = new NumberBox
            {
                Value = value,
                Minimum = min,
                Maximum = max,
                SmallChange = step,
                Width = width,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            box.ValueChanged += (_, e) =>
            {
                if (double.IsNaN(e.NewValue)) return;
                set(e.NewValue);
                Changed();
            };
            return box;
        }

        var page = new StackPanel { Spacing = 6, Padding = new Thickness(24, 12, 24, 24) };
        page.Children.Add(previewCard);

        // ---- Account ----
        page.Children.Add(Section(L("Plan limits"), L("Sign in with Claude Code to show your real 5-hour and weekly limits.")));
        var signIn = new Button { MinWidth = 110 };
        var accountRow = Row("", L("Claude account"), "", signIn, out var status);
        async void UpdateAccount()
        {
            var exe = PlanUsage.ClaudeExe();
            var state = _plan switch
            {
                { State: PlanState.Ok } => L("Signed in. Showing your real 5-hour and weekly limits."),
                { State: PlanState.NeedsSignIn } => L("Not signed in. Using the token budgets below."),
                { State: PlanState.Error } p => L("Couldn't read plan limits ({0}). Using the token budgets below.", p.Message ?? ""),
                _ => L("Checking…"),
            };
            if (exe is null) state = L("Claude Code (claude.exe) was not found. Install it to sign in.");
            signIn.Content = _plan?.State == PlanState.Ok ? L("Sign in again") : L("Sign in");
            signIn.IsEnabled = exe is not null && !_signingIn;
            status.Text = state;

            // Account name, read off the UI thread; shown above the status line.
            var acct = await Task.Run(PlanUsage.ReadAccount);
            if (acct is null || _plan?.State == PlanState.NeedsSignIn) return;
            var who = string.Join(" · ", new[] { acct.Name, acct.Email, acct.Plan is null ? null : L("{0} plan", acct.Plan) }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (who.Length > 0) status.Text = who + "\n" + state;
        }
        signIn.Click += async (_, _) =>
        {
            signIn.IsEnabled = false;
            status.Text = L("Waiting for sign-in in your browser…");
            await SignInFlowAsync();
            UpdateAccount();
            RefreshPreview();
        };
        UpdateAccount();
        page.Children.Add(accountRow);

        // ---- Taskbar view ----
        page.Children.Add(Section(L("Taskbar view"), L("What the widget shows on the taskbar.")));
        page.Children.Add(Row("", L("5-hour limit"), L("Show the rolling 5-hour usage gauge."),
            Toggle(draft.ShowSession, v => draft.ShowSession = v)));
        page.Children.Add(Row("", L("Weekly limit"), L("Show the 7-day usage gauge."),
            Toggle(draft.ShowWeek, v => draft.ShowWeek = v)));
        page.Children.Add(Row("\uE8BF", L("Daily usage"), L("Show today's gauge: your use against today's share of the weekly limit (the daily budget below when not signed in)."),
            Toggle(draft.ShowDay, v => draft.ShowDay = v)));
        page.Children.Add(Row("", L("Labels"), L("Show the small names (5h, Week, Today) above the percentages."),
            Toggle(draft.ShowLabels, v => draft.ShowLabels = v)));
        page.Children.Add(Row("", L("Time left"), L("Replace the 5h and Week labels with the time until each limit resets (needs sign-in)."),
            Toggle(draft.ShowTimeLeft, v => draft.ShowTimeLeft = v)));

        // ---- Flyout ----
        page.Children.Add(Section(L("Flyout"), L("What the big window shows under each limit (needs sign-in).")));
        page.Children.Add(Row("", L("Time left"), L("Show the time left, the reset time and a progress bar for the window."),
            Toggle(draft.ShowFlyoutTime, v => draft.ShowFlyoutTime = v)));
        page.Children.Add(Row("", L("Pace"), L("Show whether you are ahead of, on, or behind pace for the window."),
            Toggle(draft.ShowPace, v => draft.ShowPace = v)));

        // ---- Colours ----
        page.Children.Add(Section(L("Colours"), L("Gauges turn amber, then red, as usage climbs.")));
        page.Children.Add(Row("", L("Warning at"), L("Percent used when a gauge turns amber."),
            Number(draft.WarnAt, 1, 99, 5, v => draft.WarnAt = v, 110)));
        page.Children.Add(Row("", L("Critical at"), L("Percent used when a gauge turns red."),
            Number(draft.CritAt, 2, 100, 5, v => draft.CritAt = v, 110)));

        // ---- Data ----
        page.Children.Add(Section(L("Data"), L("Where the numbers come from and how often they refresh.")));
        var refresh = new ComboBox { MinWidth = 130 };
        int[] minutes = [1, 5, 15];
        foreach (var m in minutes) refresh.Items.Add(m == 1 ? L("1 minute") : L("{0} minutes", m));
        refresh.SelectedIndex = Math.Max(0, Array.IndexOf(minutes, draft.RefreshMinutes));
        refresh.SelectionChanged += (_, _) =>
        {
            if (refresh.SelectedIndex < 0) return;
            draft.RefreshMinutes = minutes[refresh.SelectedIndex];
            Changed();
        };
        page.Children.Add(Row("", L("Refresh every"), L("Plan limits are asked for at most every 5 minutes."), refresh));
        page.Children.Add(Row("", L("Count cache reads"), L("Include cached input in token totals. Cache reads are cheap, so they are off by default."),
            Toggle(draft.IncludeCacheReads, v => draft.IncludeCacheReads = v)));
        page.Children.Add(Row("", L("Claude data folder"), L("Claude Code's session logs. Leave empty for the default."),
            FolderBox(draft.DataFolder, UsageReader.DefaultRoot, v => { draft.DataFolder = v; Changed(); })));
        page.Children.Add(Row("", L("Shared folder"), L("Combine usage from several computers. Point each one at the same synced folder."),
            FolderBox(draft.SyncFolder, L("Not shared"), v => { draft.SyncFolder = v; Changed(); })));

        // ---- Token budgets ----
        page.Children.Add(Section(L("Token budgets"), L("Used for the gauges when you are not signed in.")));
        page.Children.Add(Row("", L("5-hour budget"), L("Million tokens."),
            Number(draft.SessionLimitM, 0.1, 1000, 0.5, v => draft.SessionLimitM = v)));
        page.Children.Add(Row("", L("Weekly budget"), L("Million tokens."),
            Number(draft.WeekLimitM, 0.1, 10000, 1, v => draft.WeekLimitM = v)));
        page.Children.Add(Row("", L("Daily budget"), L("Million tokens, for the Today ring when not signed in."),
            Number(draft.DayLimitM, 0.1, 1000, 0.5, v => draft.DayLimitM = v)));

        RefreshPreview();
        return new ScrollViewer { Content = page };
    }

    public UIElement? CreateSettingsContent(IWidgetSettingsContext context) => BuildSettings(context);
}
