# Jellyfin Audiobookshelf Plugin

<p align="center">
  <img alt="Alpha" src="https://img.shields.io/badge/status-alpha-red?labelColor=black" />
  <img alt="Jellyfin" src="https://img.shields.io/badge/Jellyfin-10.10%2B-00A4DC?logo=jellyfin&logoColor=white&labelColor=black" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white&labelColor=black" />
  <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0-blue?labelColor=black" />
</p>

> [!WARNING]
> **This plugin is in active alpha development. Expect breaking changes between releases. Use at your own risk and back up your Jellyfin data before installing.**

Bridges [Audiobookshelf](https://www.audiobookshelf.org/) and Jellyfin so your existing **AudioBooks** library becomes the unified front-end for your audiobook collection — no separate sidebar, no duplicate entries.

---

## How it works

Audiobookshelf (ABS) manages your audiobook files, metadata, and listening progress. Jellyfin plays and displays everything. This plugin connects the two:

- Jellyfin scans your local audiobook files as it normally would
- The plugin enriches each scanned item with richer metadata pulled from ABS (cover art, series, narrators, publishers, tags)
- Listening progress is kept in sync in both directions — play in Jellyfin, pick up where you left off in ABS (or any ABS client), and vice versa

---

## Features

### Metadata
- **Cover art** — fetches cover images from your ABS server and uses them as primary images in Jellyfin
- **Rich book metadata** — title, authors, narrators, series name and sequence number, publisher, genres, tags, description (HTML stripped)
- **ASIN / ISBN provider IDs** — stored on the Jellyfin item so future lookups are fast
- **Fuzzy matching** — when no exact ASIN/ISBN match exists, falls back to file-path and title+author scoring to find the right ABS item
- **Type-aware matching** — ebook (.epub, .pdf) matches to ABS items with ebook files; audiobook matches to items with audio files

### Progress Sync
- **Outbound (real-time)** — whenever you play an audiobook in Jellyfin, your position is pushed to ABS within 10 seconds (debounced to avoid hammering the API)
- **Inbound (scheduled)** — a Jellyfin scheduled task pulls ABS progress on a configurable interval (default: 10 minutes) and at startup; uses last-write-wins logic based on ABS timestamps
- **Outbound (on-demand)** — a second scheduled task lets you trigger a bulk push of all Jellyfin positions to ABS from the admin Tasks page; useful after ABS was unreachable or for initial migration

### Tasks
- **Link Unmatched Items** — finds unlinked Jellyfin items and matches them to ABS using ASIN, ISBN, or fuzzy title+author matching; respects ebook vs audiobook types
- **Cleanup: Remove Invalid Links** — removes ABS IDs from items that moved outside selected libraries
- **Cleanup: Repair Broken Links** — detects when ABS items were deleted and re-matches to current library
- **Cleanup: Validate Link Types** — validates that linked items match types (audiobook↔audiobook, ebook↔ebook) and removes mismatches
- **Refresh Metadata** — queues metadata refresh for all ABS-linked items
- **Cache** — ABS library items are cached for 10 minutes to speed up multiple task runs

### User Mapping
- **Auto-discovery** — enter your ABS admin token and click "Discover Users" to automatically match Jellyfin users to ABS accounts by username
- **Exact and fuzzy matching** — exact username matches are auto-selected; fuzzy matches are surfaced for manual confirmation
- **Manual fallback** — per-user tokens can also be entered directly in the config table

---

## Requirements

| Component | Version |
|-----------|---------|
| Jellyfin Server | 10.10.7 or newer |
| Audiobookshelf | Any recent version (API v1) |
| .NET Runtime | 9.0 (included in Jellyfin's Docker image) |

Both services must be reachable from the machine running Jellyfin — either on the same host or on the same Docker network.

---

## Installation

### Via Jellyfin Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **➕** and paste this URL:
   ```
   https://raw.githubusercontent.com/SirFfej/Jellyfin.Plugin.Audiobookshelf/main/manifest.json
   ```
3. Go to **Dashboard → Plugins → Catalog**, find **Audiobookshelf**, and click **Install**
4. Restart Jellyfin when prompted
5. Go to **Dashboard → Plugins** and confirm **Audiobookshelf** appears

### From source

1. Clone the repository:
   ```bash
   git clone https://github.com/SirFfej/Jellyfin.Plugin.Audiobookshelf.git
   cd Jellyfin.Plugin.Audiobookshelf
   ```

2. Build the plugin:
   ```bash
   dotnet build Jellyfin.Plugin.Audiobookshelf/Jellyfin.Plugin.Audiobookshelf.csproj --configuration Release
   ```

3. Copy the output DLL to your Jellyfin plugins directory:
   ```bash
   # Linux / Docker volume
   cp Jellyfin.Plugin.Audiobookshelf/bin/Release/net9.0/Jellyfin.Plugin.Audiobookshelf.dll \
      /path/to/jellyfin/plugins/

   # Or place it in a subdirectory — Jellyfin scans recursively
   mkdir -p /path/to/jellyfin/plugins/Audiobookshelf
   cp ... /path/to/jellyfin/plugins/Audiobookshelf/
   ```

4. Restart Jellyfin.

5. Go to **Dashboard → Plugins** and confirm **Audiobookshelf** appears.

### Setting up Audiobookshelf alongside Jellyfin

If you don't already have ABS running, the included `setup-abs.sh` script inspects your existing Jellyfin Docker setup and generates a ready-to-use `docker-compose.abs.yml`:

```bash
chmod +x setup-abs.sh
./setup-abs.sh
# Review the generated docker-compose.abs.yml, then:
docker compose -f docker-compose.abs.yml up -d
```

The script auto-detects Jellyfin's media volumes, network, and UID/GID — no manual path entry required in most cases.

---

## Configuration

1. Go to **Dashboard → Plugins → Audiobookshelf → Settings**

2. **Server URL** — base URL of your ABS server, e.g. `http://192.168.1.10:13378` (no trailing slash)

3. **Admin API Token** — generate one in ABS under **Settings → Users → your user → API Token**

4. Click **Test Connection** to verify ABS is reachable and the token is valid

5. **Included Library IDs** — leave blank to include all ABS book libraries, or enter comma-separated library IDs to restrict

6. **Progress Sync** — enable/disable inbound and outbound sync independently; adjust the pull interval

7. **User Mapping** — click **Discover Users** to auto-match accounts, or manually enter per-user tokens in the table below

8. Click **Save**

---

## Scheduled Tasks

All tasks appear under **Dashboard → Scheduled Tasks → Audiobookshelf**:

### Cleanup Tasks (grouped together)
| Task | Default trigger | What it does |
|------|----------------|--------------|
| **Cleanup: Remove Invalid Links** | Manual only | Removes ABS IDs from items outside selected libraries |
| **Cleanup: Repair Broken Links** | Weekly Sun 3am | Re-matches items whose ABS item was deleted |
| **Cleanup: Validate Link Types** | Manual only | Validates audiobook↔audiobook, ebook↔ebook matches; removes mismatches |

### Sync Tasks
| Task | Default trigger | What it does |
|------|----------------|--------------|
| **Pull Progress from ABS** | Every 10 min + on startup | Fetches ABS progress for all mapped users; updates Jellyfin positions using last-write-wins |
| **Push Progress to ABS** | On demand only | Bulk-pushes all Jellyfin playback positions to ABS; useful after ABS was unreachable or for initial migration |
| **Sync Chapters** | On demand only | Populates Jellyfin chapter markers from ABS chapter data for all matched books; run after a metadata refresh |

### Metadata Tasks
| Task | Default trigger | What it does |
|------|----------------|--------------|
| **Link Unmatched Items** | Manual only | Finds items without ABS links and matches them via ASIN/ISBN/title; respects ebook vs audiobook type |
| **Refresh Metadata** | Weekly Mon 2am | Queues metadata refresh for all ABS-linked items |

The interval for the pull task is controlled by **Sync interval (minutes)** in the plugin settings.

---

## Verifying your setup

The `test-connection.sh` script in the repo root checks both services end-to-end before the plugin is involved:

```bash
chmod +x test-connection.sh
./test-connection.sh
# Or supply values directly:
ABS_TOKEN=your_token ./test-connection.sh
./test-connection.sh --abs-url http://localhost:13378 --jf-url http://192.168.1.10:8096 --token your_token
```

It checks:
- ABS reachable (`/ping`)
- ABS token valid (`/api/me`)
- ABS libraries accessible
- ABS cover image endpoint public (no auth required)
- Jellyfin reachable (`/System/Info/Public`)
- Inter-container network (both containers can reach each other), adapting automatically for host-networking setups

---

## Plugin log

All plugin log entries are written to a dedicated rolling log file in Jellyfin's log directory:

```
audiobookshelf-YYYYMMDD.log
```

Logs are retained for 7 days. The file is in addition to Jellyfin's main log — useful for isolating plugin activity without grep.

---

## Known limitations

- **Narrator display** — narrators are stored as `PersonKind.Unknown` with `Role = "Narrator"`. Jellyfin may not surface this in all client UIs.
- **Author matching** — `BookInfo` in Jellyfin 10.10 does not expose author name during metadata lookup, so fuzzy fallback is title-only. This will be improved when Jellyfin adds author to `ItemLookupInfo`.
- **Podcast libraries** — ABS podcast libraries are excluded by default. Enable them under plugin settings; note that full metadata enrichment for podcasts is not yet implemented.

---

## Building from source

```bash
# Requires .NET 9 SDK
dotnet build Jellyfin.Plugin.Audiobookshelf/Jellyfin.Plugin.Audiobookshelf.csproj
# Expected: 0 Warning(s)  0 Error(s)
```

---

## Contributing

Bug reports and pull requests are welcome. Please open an issue before starting any significant work so we can discuss the approach.

When submitting a PR:
- Build must pass with `TreatWarningsAsErrors=true` (0 warnings, 0 errors)
- Follow the existing `[LoggerMessage]` source-gen pattern for new log messages
- Test against a real ABS instance if the change touches API calls or progress sync

---

## License

[GPL-3.0](LICENSE)
