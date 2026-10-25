# Emby StrmAssistant Plus

![logo](StrmAssistant/Properties/thumb.png "logo")

Community Edition based on StrmAssistant, continuously adapted to the latest Emby with AI. Issues are not resolved here — please refer to the [Pro version](https://github.com/sjtuross/StrmAssistant).

[中文](README.md)

## Docs

- **Changelog**: [CHANGELOG.md](CHANGELOG.md)
- **Contributing**: [CONTRIBUTING.md](CONTRIBUTING.md)
- **Install & usage**: [Wiki](https://github.com/sjtuross/StrmAssistant/wiki)

## Version Info

| Item | Value |
|---|---|
| Plugin Version | **3.0.0.65** |
| Minimum Emby Version | 4.9.1.0 |
| Advertised Version | 4.10.1.0+ |
| Tested On | 4.10.1.0 |
| Target Framework | .NET 6.0 |
| Architecture | x64 / ARM64 (all features) |

> See [CHANGELOG.md](CHANGELOG.md) for per-version details.

## Latest Optimizations (v3.0.0.58 ~ v3.0.0.65)

✅ **Stability & security fixes (3.0.0.65)**
- Deleting a whole-season version no longer wipes other versions: version identity is now checked by folder, resolution and codec, and ambiguous candidates are skipped
- Image cropping works again: the temp file kept an extension ffmpeg could not map to an output format
- Some HDR Vivid sources were not detected: the extra probe now selects the stream by absolute index
- Turning Catchup off can no longer permanently consume a concurrency slot
- Direct-link logging no longer records the full URL (signatures and tokens are stripped)
- CI now runs the unit tests; a failing test blocks the release

✅ **HDR tagging decoupled + Vivid detection (3.0.0.62)**
- New "HDR tagging" group under Media Info; Dolby Vision Profile 7 and HDR Vivid toggles are now independent of ISO extraction
- HDR Vivid now probes one frame for CUVA metadata (absent from Emby's probe result), so the tag actually appears
- Existing ISO toggle state carries over on upgrade

✅ **Safety & data integrity (3.0.0.61)**
- Deleting one version no longer removes another movie's poster, subtitles or NFO in the same folder
- Extraction tasks abort on timeout instead of occupying the queue when cloud storage stalls
- Douban ratings endpoint now requires login; local metadata cache no longer mixes details with credits
- Audio / subtitle language and channel backfill for disc images actually works (STN parsing rewritten against libbluray)
- Linux builds include the missing DiscUtils assemblies

✅ **Remote ISO extraction reworked (3.0.0.60)**
- Locate and sample the main feature via UDF/ISO9660 directory entries instead of feeding the ISO header to ffprobe
- Filter looping/duplicate clips, invalid durations and missing streams before selection; dedupe samples by stream file (256 files / 64 MiB budget)
- Support 115 pickcode-only redirect links while preserving the original signature and HTTP source protocol
- Fail explicitly when no candidate is valid, instead of passing an arbitrary largest stream off as the title

✅ **ISO audio/subtitle backfill (3.0.0.59)**
- Fill language, channel count and sample rate from the MPLS STN_table, only for missing fields
- Backfill total duration for remote ISO; write chapters after duration is resolved

✅ **Context menu & settings (3.0.0.58)**
- New "Extract Media Info" / "Delete Media Info" context menu entries for movies/episodes/seasons/series
- Settings groups unified: media info persistence merged, multi-version merged, "Behavior Probe" renamed "Behavior Marking"
- Layout-only changes, no config migration

✅ **Performance & stability (3.0.0.58 ~ 59)**
- Queue batch memory reclamation, fewer DB round-trips, cached container/extension exclusion parsing
- Intro/outro dedupe keyed by `InternalId`; Chinese index rebuild uses `BEGIN IMMEDIATE`
- Multi-version info API now enforces authentication and user library permissions

## Latest Optimizations (v3.0.0.57)

✅ **Media info backup / restore split**
- "Persist Media Info" and "Restore Media Info" are now separate tasks, with selective restore by library or item
- Force re-extract marker: after clearing media info, items can truly re-probe and rebuild the JSON

✅ **Settings page polish**
- "UI Features" split into Item Display / Library & Sorting / Search & Logs, with runtime status moved to the page end
- Option search box, friendly hint when an action fails after a server restart
- Chinese dropdowns for multi-version display and sorting

✅ **Full ISO extraction pipeline**
- Remote ISO (strm → http) now writes MPLS chapters; custom ffprobe reads UDF images directly
- Persistent mount manifest + startup reconciliation + shutdown release hook
- Rewritten frame extraction for ISO screenshots/chapter images, fixed corrupted frames

✅ **Douban assist**
- Season mapping table (title-body validation + single-season fallback) with a "Clear Season Map" button
- Fixed four blocking layers (language fallback / Wikidata genid filtering / IMDb path fallback / full-pipeline logging)
- Same-source rating badge marking, rating snapshot backfill, incremental Douban ID population with retry queue

✅ **Stability & engineering**
- Chinese search: fixed metadata write failures caused by missing tokenizer on write connections, index restore on disable, tokenizer initialization per library connection
- LiveTv direct-link race fixed, `ContinueWith` replaced with `async/await`, timing probes added
- IntroDB scrape log aggregation (90+ warnings per task → 1 summary line)
- New unit test project (32 cases), fixed in-process UDF file enumeration
- Build-time resx key consistency check with auto-repair, unified LF line endings

## Previous Optimizations (v26.3.25)

✅ **Full ARM64/aarch64 support (2026-09)**
- **Harmony 2.4.2**: upgraded Lib.Harmony 2.3.6 → 2.4.2, fixing `NotImplementedException` on every patch on arm64
- **Chinese search enhancement on arm64**: built arm64 `libsimple.so` (SQLite FTS5 Chinese/Pinyin tokenizer)
- **libsimple source integrated**: full source + one-click build script in `Tokenizer/libsimple-src/`

✅ **Complete Architecture Refactoring**
- New `EmbyVersionAdapter` for intelligent version detection and adaptation
- `ServiceLocator` for unified global service management
- Enhanced `PatchTracker` with detailed patch status tracking

✅ **Full Support for Emby 4.9.1.90 / 4.10.0.29**
- Optimized API calls specifically for 4.9.1.90 and 4.10.0.29
- Support for latest MediaSources and notification system enhancements
- Supports Emby 4.9.1 and above only

✅ **Enhanced Error Handling**
- More detailed diagnostic logging and error tracking
- Multi-layer fallback mechanism: Harmony → Reflection → PublicAPI
- Intelligent feature degradation with core functionality guaranteed

## Features

### Startup Speed & Media Info

- **Pre-extract media info**: probe before/after library import so playback starts without waiting for ffprobe
- **Exclusive extraction mode**: takes over Emby's native ffprobe and image capture; custom ffprobe binary supported (rffmpeg for distributed extraction)
- **ISO direct read**: custom ffprobe reads UDF images directly on Linux/Docker, with MPLS playlist language and chapter parsing
- **Media info persistence**: writes `*-mediainfo.json` with backup / restore / clear-then-re-extract support
- **Image capture**: video screenshots, preview thumbnails, backdrop generation (with multi-select post-processing)
- **HDR tagging**: detects CUVA dynamic metadata and shows the range label as `HDR Vivid`; dual-layer Dolby Vision is tagged Profile 7 on the enhancement layer
- **Concurrency & timeout control**: 1–20 concurrent tasks, configurable timeouts and screenshot timeout multiplier

### Intro & Credits

- **Fingerprint detection**: audio fingerprint extraction based on Emby's native intro detection
- **Behavior detection**: marks intros/credits from playback behavior
- **Online scraping**: syncs markers from IntroDB / Tidb, batch-scrape by library or series
- **Manual control**: mark, clear, or re-detect per episode or per season

### Multi-Version

- **Automatic merging**: merges same-title videos in a folder or across libraries without strict naming rules
- **Display enhancement**: labels show resolution tier, codec, bitrate and size (e.g. `4K HEVC 46.1Mbps 168.4MB`)
- **Sorting**: by quality, size, or path; customizable display format

### Metadata Enhancement

- **Chinese metadata**: custom TMDB / TVDB fallback languages and priority order
- **Douban assist**: rating override, Chinese cast names, title/overview, poster fallback; season matching with title-body validation and mapping cache
- **Episode group scraping**: TMDB episode groups and local episode group JSON
- **Person enhancement**: cast aliases, season cast import, series cast merging
- **Poster & sorting**: original-language poster priority, Pinyin initial sorting, NFO enhancement, tagline clearing
- **Local metadata cache**: two-layer `override/` + `cache/` structure, enables offline library rebuild
- **Scraping optimization**: season-level episode metadata fetch, optional skip of per-episode fallback and episode images

### Search & Network

- **Chinese search enhancement**: SQLite FTS5 + libsimple tokenizer for Chinese fuzzy search and Pinyin search
- **Proxy & direct link**: HTTP proxy, local server discovery, media direct-link redirection
- **TMDB replacement**: replace the TMDB API endpoint

## Settings Page

The plugin settings are organized into 6 tabs, with a **master switch** at the top (when off, patches are not loaded, events are not subscribed and queues are not initialized; restart required after changing):

| Tab | Contents |
|---|---|
| Basic | Master switch, task concurrency, memory management, Chinese search, UI features, runtime status & maintenance |
| Advanced | Multi-version merge & display, playback & cleanup, notifications, item display, library & sorting, search & logs |
| Media Info | Extraction scope, image capture, ISO extraction, HDR tagging, persistence, performance & exclusivity |
| Metadata | Chinese metadata, episode groups, scraping optimization, person enhancement, Douban assist, poster & sorting, metadata refresh, local metadata cache |
| Intro & Credits | Fingerprint detection, behavior detection, online scraping |
| Network | Network & local discovery, media direct link, TMDB replacement |

An **option search box** at the top-left of the settings page filters options by keyword.

## Scheduled Tasks

| Task | Description |
|---|---|
| Extract Media Info | Batch probe media info and screenshots |
| Persist Media Info | Write media info to JSON backups |
| Restore Media Info | Restore media info from JSON backups |
| Extract Video Thumbnails | Generate chapter preview thumbnails (enable "Generate video preview thumbnails" in the library settings first) |
| Extract Intro Fingerprints | Generate audio fingerprints for intro detection |
| Scrape IntroDb | Sync intro/credits markers online |
| Merge Multi Versions | Scan and merge multiple versions of the same title |
| Build Douban Cache | Batch-match Douban entries, fill ratings and season mappings |
| Populate Douban IDs | Incrementally fill missing Douban IDs (with retry queue) |
| Refresh Episodes | Refresh episodes by scope and lookback days |
| Refresh / Delete Persons | Person metadata maintenance |
| Scan External Subtitles | Independent external subtitle scanning |

## Install

1. Download `StrmAssistant.dll` into Emby's `plugins` folder
2. Restart Emby
3. Check the plugin version and configure settings on the Plugins page

See the [Wiki](https://github.com/sjtuross/StrmAssistant/wiki) for installation and usage details.

## Compatibility

The plugin adapts dynamically: it detects the Emby version at startup, prefers Harmony patches, falls back to reflection, then to public APIs.

| Emby Version | Status | Notes |
|---|---|---|
| 4.10.1.x | ✅ Full | Current target and tested version |
| 4.10.0.x | ✅ Full | Tested on x64 (all modules Harmony + Chinese search) |
| 4.9.1.90+ | ✅ Full | Recommended, best performance |
| 4.9.1.0–89 | ✅ Supported | Some features may degrade |
| < 4.9.1.0 | ❌ Unsupported | Compatibility logic removed |

| Architecture | Status | Notes |
|---|---|---|
| x86-64 / x64 | ✅ Full | All features available |
| ARM64 / aarch64 | ✅ Full | Requires Harmony 2.4.2+, incl. Chinese search |

## Build

### Method 1: GitHub Actions (recommended)

CI is **manually triggered**: open the Actions tab, choose `Build Plugin`, then click `Run workflow`.

When done, download `StrmAssistantPlus_v<version>.dll` for the matching tag (e.g. `v3.0.0.62`) from [Releases](https://github.com/wabisabi926/StrmAssistant/releases), rename it to `StrmAssistant.dll`, overwrite `config/plugins/StrmAssistant.dll`, then restart Emby.

### Method 2: Local build

- Requires .NET 6.0 SDK or higher
- On Linux use `build-local.sh` (works around the Resource.Embedder Linux path bug)

```bash
bash build-local.sh
```

Output: `StrmAssistant/bin/Release/StrmAssistant.merged.dll`

### Build libsimple.so (Chinese/Pinyin tokenizer)

Source and one-click script live in `StrmAssistant/Tokenizer/libsimple-src/` (see its README).

## Disclaimer

This is an open-source project not affiliated with, authorized, or endorsed by Emby LLC. It is intended to provide extra enhancements and convenience for users who have legally purchased and installed Emby.

### Terms of Use

1. **Legal use**: This project applies only to users with a legally installed and licensed Emby. Users must comply with Emby's terms of service and license agreement.
2. **Non-commercial**: This project is free and for personal study, research and non-commercial use only. Commercial use of this project or its derivatives is strictly prohibited.
3. **No proprietary Emby components**: This project contains no proprietary Emby components (DLLs, code, icons or other copyrighted assets).
4. **Feature limits**: This project does not bypass Emby's licensing, DRM, or unlock any paid features.
5. **User responsibility**: Users are responsible for complying with applicable laws and regulations.

### Limitation of Liability

1. The developers are not liable for any direct or indirect consequences (including but not limited to data loss, software failure, or legal disputes) arising from the use of this project.
2. If you believe this project may infringe any rights, please contact the developer.

### Star History

[![Star History Chart](https://api.star-history.com/svg?repos=wabisabi926/StrmAssistant&type=Date)](https://www.star-history.com/#wabisabi926/StrmAssistant&Date)
