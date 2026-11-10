using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsage.ExtensionApp;

// Usage in 15-minute buckets per model. This is the unit that is shared between
// computers: small, and fine-grained enough to rebuild the 5-hour, daily and
// weekly figures.
public sealed record Bucket(DateTime StartUtc, string Model, Totals Totals, int Messages, string Source = "");

// A synced folder lets several computers (Windows, a NAS, ...) show combined
// statistics. Every computer writes one file with its OWN usage and reads the
// files of the others. Keeping the folder in sync (OneDrive, Syncthing, a network
// share) is up to the user.
//
// File: usage-<machine>.json
//   { "version": 1, "machine": "NAS", "updated": "2026-11-10T09:00:00Z",
//     "buckets": [ { "t": "2026-11-10T09:15:00Z", "m": "claude-sonnet-5-5",
//                    "i": 120, "o": 800, "cw": 5000, "cr": 90000, "c": 0.021, "n": 3 } ] }
//   t  = start of the 15-minute bucket (UTC)      m  = model id
//   i / o / cw / cr = input, output, cache write, cache read tokens
//   c  = estimated cost in USD (optional; worked out from the tokens when missing)
//   n  = number of messages (optional)
public static class SyncStore
{
    private const string Prefix = "usage-";

    private sealed class FileDto
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("machine")] public string? Machine { get; set; }
        [JsonPropertyName("updated")] public DateTime Updated { get; set; }
        [JsonPropertyName("buckets")] public List<BucketDto> Buckets { get; set; } = [];
    }

    private sealed class BucketDto
    {
        [JsonPropertyName("t")] public DateTime T { get; set; }
        [JsonPropertyName("m")] public string M { get; set; } = "unknown";
        [JsonPropertyName("i")] public long I { get; set; }
        [JsonPropertyName("o")] public long O { get; set; }
        [JsonPropertyName("cw")] public long Cw { get; set; }
        [JsonPropertyName("cr")] public long Cr { get; set; }
        [JsonPropertyName("c")] public double? C { get; set; }
        [JsonPropertyName("n")] public int? N { get; set; }
    }

    public static string MachineId
    {
        get
        {
            var raw = $"{Environment.MachineName}-{Environment.UserName}";
            return new string(raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        }
    }

    private static string OwnFile(string folder) => Path.Combine(folder, $"{Prefix}{MachineId}.json");

    private static string _lastWritten = "";

    // Writes this computer's usage. Skipped when nothing changed since the last write.
    public static void Write(string folder, IReadOnlyList<Bucket> buckets)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var dto = new FileDto
            {
                Machine = Environment.MachineName,
                Updated = DateTime.UtcNow,
                Buckets = buckets.OrderBy(b => b.StartUtc).Select(b => new BucketDto
                {
                    T = DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), M = b.Model,
                    I = b.Totals.Input, O = b.Totals.Output, Cw = b.Totals.CacheWrite, Cr = b.Totals.CacheRead,
                    C = Math.Round(b.Totals.Cost, 6), N = b.Messages,
                }).ToList(),
            };
            // Compare without the timestamp so an idle computer does not rewrite the file.
            var signature = JsonSerializer.Serialize(dto.Buckets);
            if (signature == _lastWritten && File.Exists(OwnFile(folder))) return;

            var path = OwnFile(folder);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            File.Move(tmp, path, overwrite: true);
            _lastWritten = signature;
        }
        catch { /* folder offline or read-only: carry on with local numbers */ }
    }

    // Reads every other computer's file. The own file is skipped (it is rebuilt from the logs).
    public static List<Bucket> ReadOthers(string folder, DateTime cutoffUtc)
    {
        var result = new List<Bucket>();
        try
        {
            if (!Directory.Exists(folder)) return result;
            var own = Path.GetFileName(OwnFile(folder));
            foreach (var file in Directory.EnumerateFiles(folder, Prefix + "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), own, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var dto = JsonSerializer.Deserialize<FileDto>(fs);
                    if (dto is null || dto.Version != 1) continue;
                    var source = string.IsNullOrWhiteSpace(dto.Machine) ? Path.GetFileNameWithoutExtension(file)[Prefix.Length..] : dto.Machine.Trim();
                    foreach (var b in dto.Buckets)
                    {
                        var start = b.T.Kind == DateTimeKind.Utc ? b.T : b.T.ToUniversalTime();
                        if (start < cutoffUtc) continue;
                        var cost = b.C ?? Pricing.Cost(b.M, b.I, b.O, b.Cw, 0, b.Cr);
                        result.Add(new Bucket(start, b.M, new Totals(b.I, b.O, b.Cw, b.Cr, cost), b.N ?? 0, source));
                    }
                }
                catch { /* half-synced or unreadable file: ignore it this round */ }
            }
        }
        catch { }
        return result;
    }
}
