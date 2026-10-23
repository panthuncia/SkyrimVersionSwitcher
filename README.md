# Skyrim Runtime Manager

A Windows C#/.NET 10 WinForms prototype with selective Steam acquisition, declarative compatibility profiles, a content-addressed cache, and transactional game-file overlays.

## Run

Install the .NET 10 SDK:

Build the solution in Release configuration with `build.cmd` from the repository root. Additional arguments are passed to `dotnet build`.

```powershell
dotnet run --project SkyrimVersionSwitcher
```

Or publish a framework-dependent Windows release (requires the .NET 10 Windows Desktop Runtime):

```powershell
dotnet publish SkyrimVersionSwitcher -c Release -o artifacts/app
```

No manual tool installation is required. The app downloads the latest stable standalone Windows [DepotDownloader](https://github.com/SteamRE/DepotDownloader/releases) from its official GitHub release when Steam acquisition first needs it. SKSE archives are extracted in process with SharpCompress; 7-Zip is not required. This repository contains no game or SKSE binaries. Third-party license notices ship with the published app.

## Initial setup and offline switching

1. Select your Steam Skyrim Special Edition folder, **whatever version is currently installed**. You do not need to identify its runtime or update it first. The app compares installed files with Steam manifests and prepares the known shared-data baseline automatically. Preparation does not modify the active game. Avoid simultaneous Steam updates/mod deployments during switches.
2. Enter a Steam username and click **Download** beside the version you want. Its Download button sits next to Switch. Download installs DepotDownloader automatically, prepares shared data and just that runtime/profile, and obtains its matching SKSE archive. It never activates or launches the game. **Advanced → Download all versions** retains optional bulk setup. DepotDownloader handles Steam authentication in its console; the manager never handles your password. Close that console to cancel its download; completed files remain cached.
3. Click **Switch** beside the version you want. Missing files are prepared and verified automatically before activation. Ready versions switch entirely offline and never initialize the tool installer or check GitHub. Clicking an already active version does nothing. **Play Skyrim** launches the verified active SKSE runtime. **Advanced** contains recovery, imports, compatibility options, manifest inspection, baseline restoration and manual tool-update checking; diagnostic output is hidden under **Show details**.
4. Preparation reads pinned Steam depot manifests, compares file content, and downloads only missing/changed files with a literal `-filelist`. Size and Steam SHA-1 are verified, plus catalog SHA-256 where published. The normalized baseline covers disk depot 489831 (meshes, scripts, resource packs and the bundled free CC files), core depot 489832 (masters, textures, shaders and support files), and executable depot 489833. It checks the complete vanilla file lists, not just SkyrimSE.exe. Depending on the starting installation, the first setup can require several GB. Matching large archives stay shared in place without a duplicate cache copy. Downloaded replacements and overwritten originals are retained as immutable objects; retry metadata is kept.
5. For Skyrim 1.7.104, **Download** opens the one-time Nexus SKSE 2.3.1 import guide when needed. Open Nexus, download the correct archive and import it; the app verifies the prepared files and leaves activation to Switch. Switch can also guide you through missing SKSE setup before activating. No separate verification button is needed. The permitted manual Nexus flow is retained rather than bypassing download authorization. SKSE scripts, loader and matching runtime DLL are cached independently of data profiles.
6. Default runtime compatibility uses modern shared data regardless of the starting version. **Advanced → Legacy mod compatibility** selects optional older masters/interface; **Include legacy data in downloads** makes bulk setup prefetch both profiles. Per-version Download prepares the currently selected compatibility profile. The legacy 1.5.97 profile temporarily removes Creation Club plugins/archives, including official files supplied during normalization, and restores them when leaving it. Third-party `Data/SKSE/Plugins` remain under your mod manager's control. **Restore shared baseline** restores the normalized supported vanilla files. **Restore original installation** instead restores the managed files from your starting installation, including unknown/modified originals, and removes files introduced by normalization. Unlisted mods are left alone.
7. Normalization and runtime activation form one journaled transaction on **Switch**, so an interrupted operation rolls back both together. Recovery runs automatically before setup/switching and when detected on startup; an Advanced recovery action is also available. Invalid recovery objects block restoration and retain the journal. With the bundled normalization catalog, outside changes to managed files are automatically repaired on the next switch, preserving conflicting originals in history. You do not need to restore a particular version manually first.

**Advanced → Import game files from folder** provides an additional local acquisition source. Imported files must match the requested Steam manifest and pinned hashes; use a matching extracted depot layout. Previously downloaded files from `RuntimeManager/versions/<version>` are considered and verified before reuse. No file is accepted merely because its name looks correct. **Managed files and compatibility** shows each selected profile's file policies.

## Progress and errors

The selected row shows its current stage while the main status identifies the file or action in progress: shared-data inspection, runtime acquisition, SKSE extraction, cache verification, preserving originals, installing files, verifying the installation, or rollback. A progress bar displays actual file counts during checks/replacement and real byte counts for HTTP tool/SKSE downloads. Percentages describe the current stage and can reset when the stage changes. Steam authentication and content transfer use an indeterminate bar, explicitly directing you to DepotDownloader's console for sign-in and its native transfer progress; the manager does not invent a byte percentage or intercept console credentials. An elapsed-time counter stays visible while working.

Errors appear inline with a short explanation and a next step, rather than only a raw exception. Steam access/network failures, content mismatches, manifest problems, missing files/accounts, locked files, permissions and disk space have distinct guidance. **Retry** repeats the failed action and reuses completed files. A retained recovery journal changes the action to **Recover files**. **Show details** includes the full exception and operation/stage context; **Copy error details** copies that diagnostic report for troubleshooting. If activation and rollback both fail, both original errors are retained in the report.

## Automatic DepotDownloader management

The installer queries GitHub's official latest-stable release API and selects `DepotDownloader-windows-x64.zip` or the native ARM64 equivalent. It validates source URLs, download size, archive paths and the GitHub SHA-256 digest when the release provides one. It records local hashes for every installed file and only publishes complete versioned installations. Releases without an upstream digest are obtained over official HTTPS and recorded with local integrity hashes; this is not a publisher-signature guarantee.

Tools live under `%LOCALAPPDATA%/SkyrimVersionSwitcher/tools/asset-<id>-<installation-id>/`; `installed.json` points to the verified current installation. Older installations remain available after an update. A failed update leaves the previous installation intact. An unavailable update check can reuse an intact cached tool, but a corrupted cached executable cannot be used offline. The current latest release is resolved at runtime, never hardcoded. **Advanced → Update DepotDownloader** explicitly checks again. Complete offline switches need neither DepotDownloader nor GitHub.

**Advanced → Remember Steam login in DepotDownloader** is on by default. Every manifest and file download includes `-remember-password`, allowing DepotDownloader to reuse its saved Steam login token after the first successful password/Steam Guard login. Earlier settings migrate to this default once; an explicit opt-out in the updated app is preserved. DepotDownloader owns token storage in its per-user isolated storage, while processes share the stable `tools/steam-session` working directory. Steam can expire or revoke tokens and require another login. Turning the option off stops requesting token reuse; it does not erase DepotDownloader's existing saved token. The manager stores only the checkbox choice, compatibility options and game folder; it never stores the Steam username or password. See [third-party notices](THIRD-PARTY-NOTICES.md).

## Catalog and compatibility

The catalog's `Normalization` section pins a supported **destination** release (currently 1.7.104) and its three depot manifests. It is not a starting-version requirement and does not mean “always use whatever Steam currently calls latest.” Older, mixed and unrecognized file sets are compared by hash and brought to this supported data combination. Future starting releases are treated the same way; compatibility still comes from the fixed catalog. Additional purchased Creation Club content, mod files and localized add-on depots are not acquired or deleted by normalization. The bundled disk manifest includes the English vanilla voice archive; it does not manage every language pack.

- `SkyrimVersionSwitcher/runtimes.json` describes runtime labels and SKSE sources. Its original manifest/required-file fields remain for the earlier prototype's recovery engine; the current overlay pipeline uses `compatibility.json` as its authoritative game-file catalog.
- `SkyrimVersionSwitcher/compatibility.json` is independent of both the downloader and activation engine. Schema 1 declares source depot/manifest/release identities, a modern baseline, per-runtime profiles and each file's `versioned`, `shared` or `absent` policy. A versioned rule can name a source from another release. Optional mod-compatibility overrides are complete separate profiles, not mandatory downloads for the default mode.
- Each supplied versioned game rule has a pinned SHA-256. The default 1.5.97 shader rule and its selected legacy masters use the **1.6.1170-compatible data baseline**, rather than assuming every file must come from the 1.5.97 release. The bundled mapping follows the reference project's published content hashes; changing it requires a catalog edit and fresh preparation/verification. These combinations are marked **reference-documented, not game-tested by this project**.
- `catalog-provenance.json` records a pinned [Skyrim Runtime Swapper](https://github.com/Modding-Forge/Skyrim-Runtime-Swapper) commit and the factual manifest/content-hash metadata consulted. No implementation code, binary patches or game payloads from that project are included. [Profile documentation](https://github.com/Modding-Forge/Skyrim-Runtime-Swapper/blob/master/docs/modules/ROOT/pages/profiles.adoc) describes the compatibility distinctions. SHA-256 values are reference-catalog assertions, not publisher signatures; downloaded game bytes must also match the authenticated Steam manifest.
- The legacy 1.5.97 mode uses selected 1.6.1170-compatible masters/interface, not a complete historical 1.5.97 data installation. It can require Backported Extended ESL Support and compatible mods; these are not installed automatically. Existing Creation Club files are preserved, never downloaded or fabricated.
- A catalog fingerprint binds every prepared receipt to its profile and source identities. Editing those definitions invalidates old readiness; the app will not silently activate a different recipe. This is a supported combination catalog, not a promise that future Steam data remains compatible.

## Shared storage and transactions

Under `<game>/RuntimeManager`:

```text
objects/<SHA256>                         one copy of each unique payload/original
catalog/<runtime>/skse.json              runtime-specific SKSE object references
catalog/<runtime>/<profile>/steam.json   prepared game object references
catalog/<runtime>/<profile>/ready.json   sealed complete overlay receipt
downloads/<depot>/<manifest>/            manifest text and downloader working area
steam-objects.json                      content index for cross-manifest reuse
normalization.json                      verified complete Steam baseline file plan
baseline.json                           supported normalized shared-file expectations
original-installation.json              preserved starting files and original absences
state.json                              current installed overlay expectations
transactions/overlay-journal.json       pending rollback record
transactions/history/<id>.json          retained original/conflict references
```

Preparation is separate from activation. Activation resolves the normalized baseline plus runtime overlay before mutations, verifies required objects or matching installed content, captures changed originals into the object store, writes/flushed recovery metadata, and stages/replaces only destinations whose content needs changing. It verifies installed hashes and writes active state before retiring the journal. Failure restores originals; recovery validates every recovery object before starting. Original snapshots and the supported canonical baseline are separate, so a starting 1.5.97 installation is never mislabeled as modern shared data. This is a journaled multi-file transaction, not a single atomic filesystem operation. No hardlinks are used. Unchanged bulk assets are recorded by hash and remain in place; overwritten versions are cached before replacement. A shared asset subsequently changed by an external tool may need re-downloading if its expected bytes were never cached.

Paths are constrained to supported game files, compiled SKSE scripts and Creation Club patterns; traversal and junctions/symlinks in managed paths are rejected. An installation lock prevents concurrent operations by manager instances. History and original/conflict objects are retained; there is deliberately no garbage collection yet. Do not delete the object store while baseline, state or recovery/history records refer to it. Storage is on the game volume; this is not a whole-game backup or protection against drive failure.

Settings contain the game folder and advanced option choices under `%LOCALAPPDATA%/SkyrimVersionSwitcher/settings.json`. Earlier settings arrays are migrated by retaining the game folder; previous external tool paths are ignored.

## Manifest inspection and future catalogs

The downloader uses documented `-manifest-only`, `-filelist` and `-validate` options. Text manifests remain under the depot/manifest download directory. **Compare Steam manifests** accepts two `manifest_<depot>_<id>.txt` files from the same depot and exports added/removed/changed paths to JSON. Every difference is marked `Unclassified`: detection never automatically decides that a texture, master, shader or script needs downgrading. Review, compatibility testing and a catalog change are separate steps.

The parser validates depot/manifest identity and rejects malformed/duplicate/traversing paths. An unrecognized future dump format fails explicitly. Steam access/authentication/network failures are reported as acquisition failures; missing catalog paths and content mismatches are separately typed failures. The visible downloader console retains Steam's detailed error; the manager does not guess whether a nonzero exit specifically means denied historical access.

## Earlier-prototype installations

Old caches/backups remain untouched, and pending old-engine recovery runs first. The bundled normalization recipe can migrate an older `active.json` installation directly by comparing actual files and acquiring missing vanilla content; no manual Steam update or metadata deletion is required. Existing overlay baselines contribute their original-file records when available, while earlier backup/history files are retained. Catalog fingerprints invalidate outdated readiness automatically; click Download or Switch beside a version to prepare the expanded shared-data requirements.

## Validation and remaining limits

```powershell
dotnet build -c Release
dotnet run --project SkyrimVersionSwitcher.Tests
# Optional Windows form/binding smoke check:
dotnet run --project SkyrimVersionSwitcher.Tests -- --smoke-ui
# Optional live official SKSE download/managed extraction check:
dotnet run --project SkyrimVersionSwitcher.Tests -- --smoke-skse
# Optional live latest official DepotDownloader install and --version check:
dotnet run --project SkyrimVersionSwitcher.Tests -- --smoke-tools
```

Tests use temporary fake installations, deterministic Steam-manifest/download providers and injected I/O failures. They cover original rollback behavior, selected-file acquisition, source identity, manifest parsing, cache tampering, deduplication, offline reuse, shared-DLL restoration, optional data, CC restoration, baseline restore, conflict preservation, interrupted transactions, catalog invalidation and candidate comparison. Normalization tests exercise older/current/mixed/unknown starting file layouts, download-only preparation, full-data replacement, unchanged-archive reuse without cache duplication, original restoration, introduced-file removal, atomic rollback, and acquisition retry. These are synthetic fixtures, not actual game installations or in-game compatibility tests.

Authenticated Steam downloads and in-game compatibility have **not** been exercised in this development environment. Historical Steam manifest availability depends on Valve/account access. SKSE archives have local integrity records and matching-runtime filename checks, not pinned publisher signatures. Live checks cover official SKSE managed extraction and official latest DepotDownloader installation/execution. Nexus API integration, automatic mod dependency installation, MO2 profile selection, background Steam-update monitoring, storage garbage collection and automatic validation/classification of future game releases are outside this prototype. ContentCatalog handling is restricted to files within the game root/Data; external user-profile content is not modified.
