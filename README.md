# OsuMapManager

An osu! lazer beatmap management tool — sync your local library against a beatmap database, query beatmaps, and manage your collections (collection management is still being developed though).

## Usage

1. **Settings** — Point the app to your osu! lazer installation folder and a beatmap database (contains only metadata for downloading reference, you can download one from `./map_dbs`, or generate one using `fetch_catboy.py`).
2. **Sync** — Define "big filters" (one big filter includes genre, date range, difficulty, status, modes and so on) and sync beatmaps into your local library. (You can add multiple "big filters", so the final sync target is a union of all filters)
3. **Query** — Search your local library or the beatmap database for specific maps.

## Requirements

- **Windows x64** (Linux x64 and macOS also supported — build from source)
- **.NET 10 SDK** (to build from source) or **.NET 10 Runtime** (for the framework-dependent release)

Two release variants are provided:

| Variant | Size | Includes .NET Runtime |
|---------|------|-----------------------|
| Self-contained | Larger | Yes — just run it |
| Framework-dependent | Smaller | No — requires .NET 10 installed |

## Build from Source

```bash
# Windows (default)
dotnet publish OsuMapManager/OsuMapManager.csproj -c Release

# Linux
dotnet publish OsuMapManager/OsuMapManager.csproj -c Release -r linux-x64

# macOS
dotnet publish OsuMapManager/OsuMapManager.csproj -c Release -r osx-x64
```

## Tech Stack

Avalonia UI · Realm · CommunityToolkit.Mvvm · catboy.best mirror
