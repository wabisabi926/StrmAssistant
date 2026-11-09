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
| Plugin Version | **3.0.0.74** |
| Minimum Emby Version | 4.9.1.0 |
| Advertised Version | 4.10.2.0+ |
| Tested On | 4.10.2.0 |
| Target Framework | .NET 6.0 |
| Architecture | x64 / ARM64 (all features) |

> See [CHANGELOG.md](CHANGELOG.md) for per-version details.

## Latest Optimizations (v3.0.0.66 ~ v3.0.0.74)

✅ **Community rating sync (3.0.0.74)**
- Community rating source selector: Do not interfere / TMDb / IMDb / Douban, mutually exclusive; choosing Douban takes over the community rating directly, and the legacy Douban override switch migrates automatically
- Douban capabilities are independent checkboxes: fetch Douban rating (badge), sync to Kodi, overview/genres/locations, Chinese cast names, poster fallback, MdbList critic ratings; only what you check is done
- The Douban data layer activates automatically when the source is Douban or any Douban capability is checked; with everything off, detail pages never request Douban
- New 'Sync Community Rating' scheduled task and a one-click settings button with dry-run support; configurable minimum vote threshold (default 5)
- The web detail page rating row shows a per-source icon (Douban / TMDB / IMDb), plus a Douban badge for non-Douban sources

✅ **Playback experience (3.0.0.73)**
- External audio/subtitle language tags normalized (`und` / `chs` / `cht` / bilingual to standard Emby language codes, with Simplified/Traditional/bilingual written into track titles); new 'Normalize Track Languages' scheduled task
- Media health check (declaration-level): cross-checks filename claims against probed media info, flags fake 4K / HDR / Atmos, missing audio and zero duration; reports written to the plugin config folder, with 'Run Health Check', 'Export Report' and a summary line on the Home page
- HDR static metadata capture: reads MaxCLL / MaxFALL / mastering colour and luminance from ffprobe `side_data` into the video track title (only when the source carries static metadata)
- Home runtime overview now shows the FTS index count and last library scan time, breaks the queue down into media / intro / episodes, and fixes the index count showing an empty dash
- All three switches are OFF by default; full unit tests now run on every push and PR

✅ **Home and settings redesign (3.0.0.72)**
- Home is now a dashboard: a one-line runtime overview for memory, tasks, queue and index; one-click common tasks; separate Config Transfer and Maintenance groups
- Settings groups follow workflows; copy is now short titles plus one-line descriptions, with storage paths unchanged

✅ **Per-library play progress (3.0.0.72)**
- Track progress and favorites separately when a title exists in multiple libraries; restart and run 'Rebuild Per-Library Progress Index' after toggling

✅ **Server-side version label (3.0.0.72)**
- Version labels are written into `MediaSourceInfo.Name`, visible in Kodi, Apple TV and mobile apps; audio badges support Atmos / DTS:X

✅ **Config, cache and safety (3.0.0.72)**
- Config export/import with secrets omitted, metadata cache TTL and read-only verification, dry-run for destructive tasks
- On-demand media info extraction and next-episode prefetch; new 'Clean Expired Metadata Cache' task

✅ **Douban rating backfill (3.0.0.71)**
- Cache hits now also write the Douban rating, so Kodi no longer misses it when the cached score exists

✅ **Douban rating (3.0.0.68 ~ 3.0.0.70)**
- Shown on the web detail page; readable by Kodi via `ProviderIds`; refresh on demand instead of full-library polling

✅ **Automatic media-info catch-up (3.0.0.70)**
- Missing items extracted after each scan; failed items retried (3 attempts max)

✅ **Search and compatibility (3.0.0.66 ~ 3.0.0.67)**
- Search ratings fixed for Emby 4.10.2; Chinese search no longer self-disables on upgrade; ISO mount misdetection cached for 5 minutes; Dolby Vision P7 detection corrected

> Earlier versions: see [CHANGELOG.md](CHANGELOG.md).

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

✅ **Douban enhancements**
- Community rating can read Douban directly (subject page / V2 API first, IMDb fallback); the rating override switch and scope list are removed
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
- **Delete preview**: before deleting a whole-season version, see which files would be removed and which candidates are skipped as ambiguous

### Metadata Enhancement

- **Chinese metadata**: custom TMDB / TVDB fallback languages and priority order
- **Douban enhancements**: Community rating can let Douban take over the community rating; badge rating, Kodi third rating, overview, cast, poster and critic rating are independent options; season matching with title validation and mapping cache
- **Episode group scraping**: TMDB episode groups and local episode group JSON
- **Person enhancement**: cast aliases, season cast import, series cast merging
- **Poster & sorting**: original-language poster priority, Pinyin initial sorting, NFO enhancement, tagline clearing
- **Local metadata cache**: two-layer `override/` + `cache/` structure, enables offline library rebuild
- **Scraping optimization**: season-level episode metadata fetch, optional skip of per-episode fallback and episode images
- **Sync tasks**: new items update automatically after scraping; "Sync Community Ratings" and "Sync Douban Data" ship without triggers — add a weekly or monthly trigger in Emby's Scheduled Tasks if needed

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
| Metadata | Chinese metadata, community ratings, Douban enhancements, episode groups, scraping optimization, person enhancement, poster & sorting, metadata refresh, local metadata cache |
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
| Sync Douban Data | Sync Douban IDs, cache, badge/Kodi ratings, overview, cast, poster and critic ratings for the whole library |
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
| 4.10.2.x | ✅ Full | Current target and tested version |
| 4.10.1.x | ✅ Full | Tested (previous baseline) |
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

CI can be triggered two ways:

- **Automatic on tag push**: `git push origin v3.0.0.73` builds and publishes that version (the tag name is validated against the project version)
- **Manually**: open the Actions tab, choose `Build Plugin`, then click `Run workflow`

When done, download `StrmAssistantPlus_v<version>.dll` for the matching tag (e.g. `v3.0.0.73`) from [Releases](https://github.com/wabisabi926/StrmAssistant/releases), rename it to `StrmAssistantPlus.dll`, overwrite `config/plugins/StrmAssistantPlus.dll`, then restart Emby.

> Always use the filename `StrmAssistantPlus.dll`. If an older copy such as `StrmAssistant.dll` is still in the plugins folder, delete it — Emby loads both and you end up with a duplicate plugin.

### Method 2: Local build

- Requires .NET 6.0 SDK or higher
- On Linux use `build-local.sh` (works around the Resource.Embedder Linux path bug)

```bash
bash build-local.sh
```

Output: `StrmAssistant/bin/Release/StrmAssistant.merged.dll` — copy it into the plugins folder as `StrmAssistantPlus.dll`.

### Build libsimple.so (Chinese/Pinyin tokenizer)

Source and one-click script live in `StrmAssistant/Tokenizer/libsimple-src/` (see its README).

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**. See [LICENSE](LICENSE) for the full text.

This project is based on the community edition of [OneManJS/StrmAssistant](https://github.com/OneManJS/StrmAssistant) and its community forks. Modifications are also released under GPL-3.0. Copyright in upstream code belongs to the original authors and community contributors; retain the copyright notices and license text when using, modifying, or redistributing this project.

> GPL-3.0 permits commercial use. If you redistribute this project or a derivative work, you must continue to provide the source under GPL-3.0 and retain the copyright and disclaimer notices.

## Disclaimer

This is a GPL-3.0 open-source project not affiliated with, authorized, or endorsed by Emby LLC. It is intended to provide extra enhancements and convenience for users who have legally purchased and installed Emby.

### Terms of Use

1. **Legal use**: This project applies only to users with a legally installed and licensed Emby. Users must comply with Emby's terms of service and license agreement.
2. **Free and licensed**: This project is free of charge and has no paid edition. Use, modification and redistribution are governed by [GPL-3.0](LICENSE); derivative works must also be released under GPL-3.0 with copyright notices retained. The authors do not provide paid commercial support.
3. **No proprietary Emby components**: This project contains no proprietary Emby components (DLLs, code, icons or other copyrighted assets).
4. **Feature limits**: This project does not bypass Emby's licensing, DRM, or unlock any paid features.
5. **User responsibility**: Users are responsible for complying with applicable laws and regulations.

### Limitation of Liability

1. The developers are not liable for any direct or indirect consequences (including but not limited to data loss, software failure, or legal disputes) arising from the use of this project.
2. If you believe this project may infringe any rights, please contact the developer.

### Star History

[![Star History Chart](https://api.star-history.com/svg?repos=wabisabi926/StrmAssistant&type=Date)](https://www.star-history.com/#wabisabi926/StrmAssistant&Date)
