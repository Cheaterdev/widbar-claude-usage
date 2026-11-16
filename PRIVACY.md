# Privacy policy

**Claude Usage for WidBar** (the "widget") is an unofficial, independent project. It is not made, endorsed or supported by Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic, PBC, used here only to say what the widget works with.

Last updated: 16 November 2026

## Short version

The widget runs on your computer. Its author receives nothing: there is no server, no analytics and no telemetry. The only network requests go to Anthropic (the company behind Claude), exactly as listed under "What the widget sends over the internet" below. No other third party is contacted, and no data is sold or shared with anyone.

## What the widget reads on your computer

- **Claude Code's usage records** (`.claude\projects`, session log files). They are used to count tokens and estimate cost. They are read locally and never uploaded.
- **Your Claude Code sign-in** (`.claude\.credentials.json`). Only the short-lived access token is used, to ask Claude for your plan limits (see below). It is never written to a file, logged or shown.
- **Your account name, email and plan** from Claude Code's local files, to show which account is signed in in Settings. They stay on your computer.

## What the widget sends over the internet

1. **To Anthropic (`api.anthropic.com`)**: your sign-in token, to get your real 5-hour and weekly limits (and any per-model limits and extra usage credits). This is the service that issued the token. Nothing else about you is sent.
2. **To Anthropic, only when your sign-in token has expired**: the widget asks Claude Code to send one tiny message ("Reply with one word: ok") so that Claude Code can renew your sign-in. The reply is discarded. This counts as a very small amount of normal Claude usage and may appear in your Claude Code history.
3. **To Anthropic's public pricing page (`platform.claude.com`)**: a plain download of the public prices, about once a day, to keep cost estimates accurate. It sends nothing about you.

The widget does not contact any server run by its author.

## Optional shared folder

If you set a **Shared folder** in Settings, the widget writes one small file there (`usage-<computer>.json`) with token counts per model per 15 minutes, and the computer's name. It contains no prompts, no code, no file names and no account details. Where that folder goes (OneDrive, a network share, Syncthing, ...) and who can read it is up to you.

## What is stored on your computer

- The widget's settings.
- A cache of Anthropic's public prices and the last plan-limits answer (percentages and limits only, no token), in `%LOCALAPPDATA%\ClaudeUsageWidget`.

Uninstalling the widget removes its settings. You can delete the folder above at any time.

## Children

The widget is not directed at children and collects no personal information from anyone.

## Changes

If this policy changes, the new version will be published at this address with a new date.

## Contact

Questions or concerns: open an issue at <https://github.com/Cheaterdev/widbar-claude-usage/issues>.
