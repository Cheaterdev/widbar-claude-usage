#!/usr/bin/env python3
"""Writes this computer's Claude Code usage into a shared folder, in the format
the Claude Usage widget reads. Use it on machines that cannot run the widget
(a NAS, a Linux box, a server). Needs only Python 3.

    python3 export-usage.py /path/to/shared/folder
    python3 export-usage.py /path/to/shared/folder --name NAS --logs ~/.claude/projects

Run it from cron / a scheduled task every few minutes. Keeping the folder in
sync with your other computers is up to you (Syncthing, OneDrive, a network share).
Costs are left out; the widget works them out from the tokens.
"""
import argparse, json, os, re, sys
from datetime import datetime, timedelta, timezone

BUCKET = 15 * 60  # seconds


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", help="shared folder the widget points at")
    ap.add_argument("--logs", default=os.path.join(os.path.expanduser("~"), ".claude", "projects"))
    ap.add_argument("--name", default=os.uname().nodename if hasattr(os, "uname") else os.environ.get("COMPUTERNAME", "pc"))
    args = ap.parse_args()

    cutoff = datetime.now(timezone.utc) - timedelta(days=30)
    seen, buckets = set(), {}

    for dirpath, _, files in os.walk(args.logs):
        for f in files:
            if not f.endswith(".jsonl"):
                continue
            path = os.path.join(dirpath, f)
            try:
                if datetime.fromtimestamp(os.path.getmtime(path), timezone.utc) < cutoff:
                    continue
                with open(path, encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        if '"usage"' not in line:
                            continue
                        try:
                            row = json.loads(line)
                            msg = row["message"]
                            u = msg["usage"]
                            ts = datetime.fromisoformat(row["timestamp"].replace("Z", "+00:00"))
                        except Exception:
                            continue
                        model = msg.get("model") or "unknown"
                        if model.startswith("<") or ts < cutoff:
                            continue
                        key = f'{msg.get("id")}:{row.get("requestId")}'
                        if (msg.get("id") or row.get("requestId")) and key in seen:
                            continue
                        seen.add(key)
                        t = int(ts.timestamp()) // BUCKET * BUCKET
                        b = buckets.setdefault((t, model), [0, 0, 0, 0, 0])
                        b[0] += u.get("input_tokens", 0) or 0
                        b[1] += u.get("output_tokens", 0) or 0
                        b[2] += u.get("cache_creation_input_tokens", 0) or 0
                        b[3] += u.get("cache_read_input_tokens", 0) or 0
                        b[4] += 1
            except OSError:
                continue

    out = {
        "version": 1,
        "machine": args.name,
        "updated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "buckets": [
            {"t": datetime.fromtimestamp(t, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
             "m": m, "i": v[0], "o": v[1], "cw": v[2], "cr": v[3], "n": v[4]}
            for (t, m), v in sorted(buckets.items())
        ],
    }
    os.makedirs(args.folder, exist_ok=True)
    safe = re.sub(r"[^A-Za-z0-9_-]", "_", args.name)
    final = os.path.join(args.folder, f"usage-{safe}.json")
    tmp = final + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(out, fh)
    os.replace(tmp, final)
    print(f"wrote {len(out['buckets'])} buckets to {final}")


if __name__ == "__main__":
    sys.exit(main())
