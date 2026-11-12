using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsage.ExtensionApp;

// Extra usage ("credits") on top of the plan: a monthly spending cap. Amounts are in cents.
public sealed record ExtraUsage(bool Enabled, double? Limit, double? Used, double? Pct);

public sealed record PlanLimits(
    double SessionPct, DateTimeOffset? SessionResets,
    double WeekPct, DateTimeOffset? WeekResets,
    bool HasSession = true, bool HasWeek = true, ExtraUsage? Extra = null,
    IReadOnlyList<ScopedLimit>? Scoped = null);

// A weekly limit that applies to one model only (for example "Fable").
public sealed record ScopedLimit(string Name, double Pct, DateTimeOffset? Resets);

public sealed record AccountInfo(string? Name, string? Email, string? Plan);

public enum PlanState { Ok, NeedsSignIn, Error }

public sealed record PlanResult(PlanState State, PlanLimits? Limits = null, string? Message = null);

// Real plan limits (the 5-hour and weekly percentages claude.ai shows).
//
// Authentication is delegated to Claude Code: "claude auth login" opens the
// browser to authorize and stores a token in ~/.claude/.credentials.json. The
// widget only reads that token and sends it to api.anthropic.com, the service
// that issued it. It never writes the file, never logs the token and never
// handles the refresh token. The usage endpoint is undocumented and may change.
public static class PlanUsage
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string CredentialsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    // Returns null when there is no usable access token.
    private static (string Token, DateTimeOffset? Expires)? ReadToken()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o)) return null;
            var token = o.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token)) return null;
            DateTimeOffset? exp = o.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var ms) && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            return (token, exp);
        }
        catch { return null; }
    }

    // Who Claude Code is signed in as. Reads only the account's display name and
    // email from Claude Code's local ~/.claude.json and the plan name from the
    // credentials file; no token is read here and nothing is sent anywhere.
    public static AccountInfo? ReadAccount()
    {
        string? name = null, email = null, plan = null;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using var fs = new FileStream(Path.Combine(home, ".claude.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty("oauthAccount", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                name = a.TryGetProperty("displayName", out var n) ? n.GetString() : null;
                email = a.TryGetProperty("emailAddress", out var e) ? e.GetString() : null;
            }
        }
        catch { }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var o) && o.TryGetProperty("subscriptionType", out var t))
                plan = t.GetString();
        }
        catch { }
        if (plan is { Length: > 0 }) plan = char.ToUpper(plan[0]) + plan[1..];
        return name is null && email is null && plan is null ? null : new AccountInfo(name, email, plan);
    }

    public static string? ClaudeExe()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Built from the profile folder: in a packaged app %APPDATA% can point at the app's private copy.
        var local = Path.Combine(home, "AppData", "Local");
        var dirs = new List<string>
        {
            Path.Combine(home, ".local", "bin"),                    // native installer
            Path.Combine(home, "AppData", "Roaming", "npm"),        // npm
            Path.Combine(home, ".claude", "local"),                 // old "local" install
            Path.Combine(home, ".claude", "local", "node_modules", ".bin"),
            Path.Combine(local, "Microsoft", "WinGet", "Links"),    // winget
            Path.Combine(home, "scoop", "shims"),                   // scoop
            Path.Combine(home, ".volta", "bin"),                    // volta
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        // The widget may have started before Claude Code was installed, so its own PATH is stale.
        // The saved user and machine PATH are read as well.
        dirs.AddRange(SavedPath());
        // The native installer gives claude.exe; an npm install gives claude.cmd.
        foreach (var name in new[] { "claude.exe", "claude.cmd" })
            foreach (var dir in dirs)
            {
                try
                {
                    var f = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
        return DesktopAppExe(local);
    }

    // The Claude desktop app (Microsoft Store) keeps its own copy of Claude Code:
    // %LOCALAPPDATA%\Claude-Data\<id>\claude-code\<version>\<hash>\claude.exe. Newest wins.
    private static string? DesktopAppExe(string localAppData)
    {
        try
        {
            var root = Path.Combine(localAppData, "Claude-Data");
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .Select(d => Path.Combine(d, "claude-code"))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "claude.exe", SearchOption.AllDirectories))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static IEnumerable<string> SavedPath()
    {
        var result = new List<string>();
        foreach (var (key, sub) in new[]
        {
            (Microsoft.Win32.Registry.CurrentUser, "Environment"),
            (Microsoft.Win32.Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        })
        {
            try
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw)
                    result.AddRange(Environment.ExpandEnvironmentVariables(raw).Split(';', StringSplitOptions.RemoveEmptyEntries));
            }
            catch { }
        }
        return result;
    }

    // .cmd shims cannot be started directly without a shell.
    private static ProcessStartInfo Start(string exe, string args, bool shell)
    {
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {args}\"") { UseShellExecute = shell, CreateNoWindow = !shell };
        return new ProcessStartInfo(exe, args) { UseShellExecute = shell, CreateNoWindow = !shell };
    }

    // Opens Claude Code's own sign-in (browser authorization). Completes when
    // the sign-in process exits.
    public static async Task<bool> SignInAsync()
    {
        var exe = ClaudeExe();
        if (exe is null) return false;
        try
        {
            using var p = Process.Start(Start(exe, "auth login", true));
            if (p is null) return false;
            await p.WaitForExitAsync();
            return true;
        }
        catch { return false; }
    }

    // Claude Code renews its own token (about every 8 hours) only when it makes a
    // real request; "auth status" does not. So a stale token is renewed by letting
    // Claude Code send one tiny Haiku message, quietly, from a neutral folder.
    private static DateTime _lastRenew = DateTime.MinValue;

    private static async Task TryRenewAsync()
    {
        if (DateTime.UtcNow - _lastRenew < TimeSpan.FromMinutes(10)) return;
        _lastRenew = DateTime.UtcNow;
        var exe = ClaudeExe();
        if (exe is null) return;
        try
        {
            var psi = Start(exe, "-p \"Reply with one word: ok\" --model haiku --max-turns 1", false);
            psi.WorkingDirectory = Path.GetTempPath();
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            if (p is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        }
        catch { }
    }

    public static async Task<PlanResult> FetchAsync()
    {
        var tok = ReadToken();
        if (tok is null || (tok.Value.Expires is { } e0 && e0 < DateTimeOffset.UtcNow.AddMinutes(1)))
        {
            await TryRenewAsync();
            tok = ReadToken();
        }
        if (tok is null || (tok.Value.Expires is { } e1 && e1 < DateTimeOffset.UtcNow))
            return new PlanResult(PlanState.NeedsSignIn);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok.Value.Token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("claude-usage-widget/1.0");
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new PlanResult(PlanState.NeedsSignIn);
            if (!resp.IsSuccessStatusCode)
                return new PlanResult(PlanState.Error, Message: $"HTTP {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync();
            SaveDump(body);
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var (sp, sr) = Window(r, "five_hour");
            var (wp, wr) = Window(r, "seven_day");
            var extra = ReadExtra(r);
            if (sp is null && wp is null && extra is null)
                return new PlanResult(PlanState.Error, Message: "Unexpected response");
            return new PlanResult(PlanState.Ok, new PlanLimits(sp ?? 0, sr, wp ?? 0, wr, sp is not null, wp is not null, extra, ReadScoped(r)));
        }
        catch (Exception ex)
        {
            return new PlanResult(PlanState.Error, Message: ex.GetType().Name);
        }
    }

    // Keeps the last usage answer (percentages and limits only, no token) so the
    // shape can be checked when Claude changes it. Overwritten on every fetch.
    private static void SaveDump(string body)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last-usage.json"), body);
        }
        catch { }
    }

    private static ExtraUsage? ReadExtra(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var e) || e.ValueKind != JsonValueKind.Object) return null;
        double? Num(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
        var enabled = e.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True;
        return new ExtraUsage(enabled, Num("monthly_limit"), Num("used_credits"), Num("utilization"));
    }

    // Per-model weekly limits: entries of "limits" with kind "weekly_scoped".
    private static List<ScopedLimit> ReadScoped(JsonElement root)
    {
        var list = new List<ScopedLimit>();
        try
        {
            if (!root.TryGetProperty("limits", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("kind", out var k) || k.GetString() != "weekly_scoped") continue;
                if (!e.TryGetProperty("percent", out var p) || !p.TryGetDouble(out var pct)) continue;
                string? name = null;
                if (e.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object)
                    foreach (var part in new[] { "model", "surface" })
                        if (name is null && sc.TryGetProperty(part, out var m) && m.ValueKind == JsonValueKind.Object
                            && m.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
                            name = dn.GetString();
                DateTimeOffset? resets = e.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(r.GetString(), out var t) ? t : null;
                list.Add(new ScopedLimit(string.IsNullOrWhiteSpace(name) ? "Model" : name!, pct, resets));
            }
        }
        catch { }
        return list;
    }

    private static (double? Pct, DateTimeOffset? Resets) Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return (null, null);
        double? pct = w.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var d) ? d : null;
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var t) ? t : null;
        return (pct, resets);
    }
}
