# Resource efficiency fork

This branch is based on `v2.2.1.0`, matching the desktop build used for validation. It adds two changes to the mpv video path while retaining the existing player, hardware decoding and desktop embedding behavior.

## Changes

### Coalesce playback policy commands

The playback monitor polls every 500 ms by default. Previously, an unchanged pause state and volume opened new named-pipe connections on every tick. `MpvPlaybackCommandCache` sends transitions immediately and reasserts unchanged properties every 30 seconds. Failed writes invalidate the relevant cached property and are retried on the next tick.

With one pause/play command and one volume command per tick, a steady minute now sends 4 commands instead of 240. This reduces repeated pipe connections and allocations; it is not a claim of a proportional reduction in total application CPU usage or RAM.

### Release a paused, hidden video process

An optional advanced setting releases the native mpv process after a single local video remains both paused and hidden for the configured delay. The selected wallpaper entry and its saved layout remain intact. When the desktop becomes visible, a new player is embedded at the captured playback percentage, with the existing Lively properties and hardware-decoding preference.

While Lively is closed, add the following property to its `Settings.json` file:

```json
"VideoMemoryReleaseDelaySeconds": 60
```

The default is `0` (disabled). The existing settings UI does not expose this property; ordinary UI settings updates preserve it. Set it to `0` and restart to disable it.

Supported cases:

- One local mpv video in span mode or on a single display.
- A locked session, a remote session paused by policy, or a separate-process screensaver showing the current wallpaper.
- Window coverage of every affected display's working area. A maximized window on one monitor does not qualify an uncovered second monitor.

The feature does not release visible, partially covered paused wallpapers, web wallpapers, streams, GIFs, duplicate layouts or configurations with multiple wallpaper players. A DWM thumbnail screensaver keeps the background window alive. Disabling the feature while suspended requests restoration on the next policy update.

Position capture and seek use bounded IPC requests. A failed capture leaves the original player running. A failed restore retains the selection and retries after five seconds. Explicit close, replacement and shutdown cancel automatic restoration, including operations already waiting for the wallpaper-loading semaphore. Subsequent video loops still begin at the start of the file.

Restoring requires player startup, so the static Windows background can briefly appear. The captured playback position is held in memory for the running Lively session; it is not a new position-persistence feature across application restarts. Keeping the existing static desktop fallback is useful.

## Validation

Local validation used the existing 2.2.1 player plugins on Windows, Intel UHD 770 hardware decoding, one 3840×1080 H.264 video and two 1920×1080 displays in span mode. The test delay was shortened to two seconds; the normal configured delay is 60 seconds.

| During the same video screensaver | Feature disabled | Feature enabled |
| --- | ---: | ---: |
| Native video processes | 2 | 1 |
| Combined player working set | 609.94 MiB | 259.05 MiB |
| Combined player private bytes | 619.27 MiB | 266.26 MiB |

Each condition used the same core code, media, mpv configuration and monitor layout, with ten process-memory samples after eight seconds of screensaver warmup. The working-set reduction was 57.53% in this run. These measurements cover the background and screensaver video processes together, not the whole application or GPU memory. Working set varies with Windows memory management; process exit is the direct evidence that the hidden player's resources were released.

The native lifecycle test verified process exit, restoration at the same playback position, unchanged saved layout, active `d3d11va` decoding and zero decoder frame drops. Automated tests cover command transitions, failed writes, periodic reconciliation, concurrent notifications, delayed suspension, visibility changes, in-flight cancellation, failed position capture, restore retry and named-pipe timeouts.

## Build and test

The core and isolated tests use the .NET 9 SDK. Player plugins, the existing UI and wallpaper bundles remain separate binary dependencies as in the upstream build.

```powershell
dotnet test src/Lively/Lively.Tests/Lively.Tests.csproj -c Release
dotnet build src/Lively/Lively/Lively.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

The test project links the production helper files directly, allowing policy and IPC tests without launching WPF or requiring media plugins. The GitHub workflow runs those tests and builds the Windows core. The complete WinUI application and installer are not rebuilt by that workflow.

The upstream dependency versions are retained. Restore reports existing NuGet vulnerability warnings, including the Magick.NET packages; this branch does not suppress those warnings or claim to resolve them.

## Further work

- Add a UI control, localization and a suspended state in the wallpaper UI before proposing a broadly distributed feature.
- Validate more GPUs, Windows lock/unlock, monitor removal and reconnection, Explorer restarts and long unattended sessions.
- Extend suspension to several independent local videos with per-display state and restoration.
- Investigate screensaver rendering reuse separately. The existing DWM path mirrors the desktop and needs validation with Windows 11's layered desktop, icons and Explorer recovery.
- Profile core allocations over time before treating a large working-set snapshot as a leak. Avoid forced GC or working-set trimming as a substitute for releasing native resources.

Upstream contribution requirements are documented in the [contributing guidelines](https://github.com/lively-community/lively/wiki/Contributing-Guidelines). This is a personal experimental fork, with no upstream submission implied.
