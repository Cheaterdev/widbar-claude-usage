using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsage.ExtensionApp;

public sealed record Totals(long Input, long Output, long CacheWrite, long CacheRead, double Cost = 0)
{
    public static readonly Totals Zero = new(0, 0, 0, 0, 0);

    // Cache reads are billed at a fraction of normal input, so they are kept
    // out of the headline number unless the user asks for them.
    public long Headline(bool includeCacheRead) =>
        Input + Output + CacheWrite + (includeCacheRead ? CacheRead : 0);

    public static Totals operator +(Totals a, Totals b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Cost + b.Cost);
}

public sealed record UsageSnapshot(
    DateTime GeneratedAt,
    Totals Last5Hours,
    Totals Today,
    Totals Last7Days,
    Totals Last30Days,
    IReadOnlyList<(DateOnly Day, Totals Totals)> Daily7,
    IReadOnlyList<(string Model, Totals Totals)> ByModel30,
    int Messages30);

// Reads Claude Code's local session logs (~/.claude/projects/**/*.jsonl). Every
// assistant message carries a usage block; we sum those. Nothing leaves the
// machine and no credentials are needed.
public static class UsageReader
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static UsageSnapshot Read(string? root)
    {
        root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var cutoff = now.AddDays(-30);
        var seen = new HashSet<string>();
        var perDay = new Dictionary<DateOnly, Totals>();
        var perModel = new Dictionary<string, Totals>();
        Totals h5 = Totals.Zero, d1 = Totals.Zero, d7 = Totals.Zero, d30 = Totals.Zero;
        int messages = 0;

        if (Directory.Exists(root))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
            catch { files = []; }

            foreach (var file in files)
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) continue;
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    string? line;
                    while ((line = sr.ReadLine()) is not null)
                    {
                        if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                        if (!TryParse(line, out var ts, out var model, out var t, out var key)) continue;
                        var local = ts.ToLocalTime();
                        if (local < cutoff) continue;
                        if (key is not null && !seen.Add(key)) continue;

                        messages++;
                        d30 += t;
                        var day = DateOnly.FromDateTime(local);
                        perDay[day] = perDay.GetValueOrDefault(day, Totals.Zero) + t;
                        perModel[model] = perModel.GetValueOrDefault(model, Totals.Zero) + t;
                        if (day == today) d1 += t;
                        if (day >= today.AddDays(-6)) d7 += t;
                        if (local >= now.AddHours(-5)) h5 += t;
                    }
                }
                catch { /* file locked or being rewritten; skip it this round */ }
            }
        }

        var daily = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(i - 6))
            .Select(d => (d, perDay.GetValueOrDefault(d, Totals.Zero)))
            .ToList();
        var models = perModel
            .OrderByDescending(kv => kv.Value.Headline(false))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        return new UsageSnapshot(now, h5, d1, d7, d30, daily, models, messages);
    }

    private static bool TryParse(string line, out DateTime ts, out string model, out Totals totals, out string? key)
    {
        ts = default; model = ""; totals = Totals.Zero; key = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return false;
            if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("timestamp", out var tsEl) || !tsEl.TryGetDateTime(out ts)) return false;

            model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "unknown" : "unknown";
            if (model.StartsWith('<')) return false; // synthetic entries

            long L(string n) => u.TryGetProperty(n, out var v) && v.TryGetInt64(out var x) ? x : 0;
            long input = L("input_tokens"), output = L("output_tokens"),
                write = L("cache_creation_input_tokens"), read = L("cache_read_input_tokens");
            long write1h = u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object
                && cc.TryGetProperty("ephemeral_1h_input_tokens", out var h) && h.TryGetInt64(out var hv) ? hv : 0;
            totals = new Totals(input, output, write, read, Pricing.Cost(model, input, output, write - write1h, write1h, read));

            // A streamed message is logged several times; message id + request id identifies it.
            var id = msg.TryGetProperty("id", out var i) ? i.GetString() : null;
            var req = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
            key = id is null && req is null ? null : $"{id}:{req}";
            return true;
        }
        catch { return false; }
    }

    public static string Money(double d) => d switch
    {
        >= 100 => $"${d:0}",
        >= 1 => $"${d:0.00}",
        _ => $"${d:0.00}",
    };

    public static string Compact(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{n / 1_000_000d:0.#}M",
        >= 1_000 => $"{n / 1_000d:0.#}K",
        _ => n.ToString(),
    };

    public static string FriendlyModel(string model)
    {
        // claude-sonnet-5-5 -> Sonnet 5.5, claude-opus-4-1-20250805 -> Opus 4.1
        var parts = model.Replace("claude-", "").Split('-');
        if (parts.Length == 0 || parts[0].Length == 0) return model;
        var name = char.ToUpper(parts[0][0]) + parts[0][1..];
        var nums = parts.Skip(1).Where(p => p.Length <= 2 && p.All(char.IsDigit)).Take(2);
        return nums.Any() ? $"{name} {string.Join('.', nums)}" : name;
    }
}

// Estimated API-equivalent cost: logged tokens x the official per-model prices.
// Prices come from Anthropic's pricing page (platform.claude.com/docs/en/about-claude/pricing.md),
// fetched at most once a day and cached on disk. A built-in table is only the
// offline fallback. This is NOT what a Pro/Max plan bills: those are flat-rate.
public sealed record ModelPrice(double In, double Write5m, double Write1h, double Read, double Out);

public static class Pricing
{
    private const string Url = "https://platform.claude.com/docs/en/about-claude/pricing.md";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", "pricing.json");

    // Fallback, used until the first successful fetch (USD per million tokens).
    private static ModelPrice P(double i, double o, double? read = null) =>
        new(i, i * 1.25, i * 2, read ?? i * 0.1, o);

    private static Dictionary<string, ModelPrice> _table = new()
    {
        ["fable-5-2"] = P(8, 40, 0.20), ["mythos-5-2"] = P(8, 40, 0.20),
        ["fable-5-1"] = P(10, 50, 0.25), ["mythos-5-1"] = P(10, 50, 0.25),
        ["fable-5"] = P(10, 50, 1), ["mythos-5"] = P(10, 50, 1),
        ["opus-5-5"] = P(4, 20, 0.20), ["opus-5"] = P(5, 25), ["opus-4-8"] = P(5, 25),
        ["opus-4-7"] = P(5, 25), ["opus-4-6"] = P(5, 25), ["opus-4-5"] = P(5, 25),
        ["opus-4-1"] = P(15, 75), ["opus-4"] = P(15, 75),
        ["sonnet-5-5"] = P(2, 10, 0.10), ["sonnet-5"] = P(2, 10, 0.20),
        ["sonnet-4-6"] = P(3, 15), ["sonnet-4-5"] = P(3, 15), ["sonnet-4"] = P(3, 15),
        ["haiku-5-5"] = P(0.10, 0.50, 0.01), ["haiku-5-5:long"] = P(0.50, 2.50, 0.05),
        ["haiku-4-5"] = P(1, 5),
    };
    private static bool _loaded;

    private static readonly Regex Dollar = new(@"\$([\d,]*\.?\d+)", RegexOptions.Compiled);
    private static readonly Regex Date = new(@"-\d{8}$", RegexOptions.Compiled);

    private static string Key(string name)
    {
        // "Claude Haiku 5.5 (for prompts over 100,000 tokens)" -> "haiku-5-5:long"
        var lower = name.ToLowerInvariant();
        var isLong = lower.Contains("over 100");
        var paren = lower.IndexOf('(');
        if (paren >= 0) lower = lower[..paren];
        lower = lower.Replace("claude", "").Trim();
        var key = Regex.Replace(lower, @"[\s\.]+", "-").Trim('-');
        return isLong ? key + ":long" : key;
    }

    private static double? Money(string cell)
    {
        var m = Dollar.Match(cell);
        return m.Success && double.TryParse(m.Groups[1].Value.Replace(",", ""),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // Parses the "Model pricing" table of the official page.
    public static Dictionary<string, ModelPrice> Parse(string markdown)
    {
        var map = new Dictionary<string, ModelPrice>();
        var inSection = false;
        foreach (var raw in markdown.Split("\n"))
        {
            var line = raw.Trim();
            if (line.StartsWith("## "))
            {
                if (inSection) break;
                inSection = line.Equals("## Model pricing", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || !line.StartsWith('|')) continue;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 6 || !cells[0].StartsWith("Claude", StringComparison.OrdinalIgnoreCase)) continue;
            var v = cells.Skip(1).Take(5).Select(Money).ToArray();
            if (v.Any(x => x is null)) continue;
            map[Key(cells[0])] = new ModelPrice(v[0]!.Value, v[1]!.Value, v[2]!.Value, v[3]!.Value, v[4]!.Value);
        }
        return map;
    }

    // Loads the cached table, and refreshes it from the official page when it is over a day old.
    public static async Task EnsureAsync()
    {
        try
        {
            var file = CacheFile;
            if (!_loaded && File.Exists(file))
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(File.ReadAllText(file));
                if (cached is { Count: > 0 }) _table = cached;
                _loaded = true;
            }
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(24)) return;

            var md = await Http.GetStringAsync(Url);
            var parsed = Parse(md);
            if (parsed.Count < 5) return; // page layout changed; keep what we have
            _table = parsed;
            _loaded = true;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(parsed));
        }
        catch { /* offline or blocked: keep the cached or built-in table */ }
    }

    private static ModelPrice? Find(string model, bool longPrompt)
    {
        var id = Date.Replace(model.ToLowerInvariant().Replace("claude-", ""), "");
        if (longPrompt && _table.TryGetValue(id + ":long", out var lp)) return lp;
        if (_table.TryGetValue(id, out var exact)) return exact;
        // e.g. "opus-4-1-extra": longest known key that prefixes the id
        return _table.Where(kv => !kv.Key.Contains(':') && id.StartsWith(kv.Key + "-"))
            .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
    }

    public static double Cost(string model, long input, long output, long write5m, long write1h, long read)
    {
        var longPrompt = input + write5m + write1h + read > 100_000;
        var p = Find(model, longPrompt);
        if (p is null) return 0; // unknown model: not priced
        return (input * p.In + output * p.Out + write5m * p.Write5m + write1h * p.Write1h + read * p.Read) / 1_000_000d;
    }
}
