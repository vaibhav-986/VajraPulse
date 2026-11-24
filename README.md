# Vajra Pulse

**Version 1.6.0**

A native Windows desktop island with a true-black compact pill, single-activity expansion, live Windows integrations, five app-launch slots, local system activity, per-app audio mixing, and cute googly eyes. No browser account, camera access, cloud service, or telemetry is required.

## 1.6.0 Updates
- Added Cute Companion, simulated Glass, and Neon Pulse design modes.
- Added locally saved Googly, Kawaii, and Starry eye styles.
- Added top-left, top-center, and top-right docking with idle auto-retreat and edge reveal.
- Music artwork can now accent the music timeline, playback bars, and compact media-progress ring.
- Refined eye reactions, compact progress visuals, and settings persistence.

## Run

Double-click `Start-VajraPulse.cmd`. The ordinary app uses the installed .NET 8 Desktop Runtime.

```powershell
.\bin\Release\net8.0-windows10.0.19041.0\VajraPulse.exe
```

## Controls

- Hover to expand; move away to collapse a hover-opened island.
- After a short idle delay, the compact island retreats completely. Move the pointer to its selected top-left, top-center, or top-right screen-edge zone to reveal it again.
- Click to keep it open while interacting; click away or press Escape to tuck it away.
- Ctrl + Alt + Space expands/collapses or reveals the island.
- Choose Cute Companion, simulated Glass, or Neon Pulse design mode in Settings. Three locally saved eye styles—Googly, Kawaii, and Starry—remain available in every mode.
- Music artwork can color only the music timeline, playback bars, and compact media-progress ring; extracted colors remain in memory and are never uploaded or persisted.
- The googly eyes follow your pointer, blink, and react when apps open or close.
- Use the Music, Focus, Files, Calendar, Alerts, and Tools tabs for one activity at a time.
- The optional side buttons open System tools, Bluetooth Settings, and Wi-Fi Settings; each can be shown or hidden independently in Settings.
- Tools remains available from the expanded island and contains local CPU/memory/network activity, five app shortcuts, and the active-app volume mixer.
- Right-click the pill for Settings; the tray menu provides show/hide, settings, and quit.
- If a timer and media session are both present, the secondary artwork bubble opens music without stopping the timer.

## Features and setup

### Focus timer and startup

Running and paused focus sessions are saved locally. If Vajra Pulse closes during a running session, the original deadline is restored on the next launch; time spent closed still counts down. A finished session is reported when the app returns.

**Start with Windows** is available in Settings and is off until explicitly enabled. It creates or removes only the current user's `VajraPulse` Run entry and does not change machine-wide startup settings.

### Charging, volume, and brightness

Power connection changes trigger a green charging/power state. Volume, mute, and brightness changes appear as brief icon-and-level states rather than a dashboard. These do not replace or suppress Windows' own flyouts.

System volume and mute are available in Settings. Internal-display brightness is available when the monitor exposes Windows WMI brightness control; unsupported displays show an explicit unavailable state. Device sensing does not change system controls. Changes are made only when you operate a control.

**Test animation** cycles through clearly labeled charging, volume, brightness, and headphone previews without changing power, audio, display, or connection state.

### Headphones

A brief badge appears when headphones/headsets become the active default multimedia audio endpoint. Other output-device changes are also identified. This is default-device sensing, not universal Bluetooth battery reporting or detection of every non-active accessory.

### System activity and connectivity buttons

Choose the side **System** button to open local aggregate CPU usage, physical-memory usage, and active-interface download/upload rates. Values come from Windows counters and are not sent anywhere or stored as history. A first sample, changed adapter, unsupported counter, or unavailable service is labeled as warming up or unavailable rather than shown as a fabricated zero.

The separate Bluetooth and Wi-Fi buttons show best-effort local status and open the matching Windows Settings page when clicked. The System/CPU, Bluetooth, and Wi-Fi side buttons default to visible and can be hidden independently in Settings; the expanded Tools tab remains available even when all three are hidden. The buttons never toggle a radio, connect to a network, pair a device, or read/store Wi-Fi names. Windows can report Bluetooth connection and battery properties differently across devices.

### Five app shortcuts

Settings provides exactly five app slots. Choose or replace a slot to open Vajra Pulse's searchable installed-app list. Traditional desktop entries come from the current-user and shared Start Menu Programs folders; packaged Windows entries come from the current user's registered app list. Search is local, lazy, bounded, and never sent anywhere. Vajra Pulse does not scan Program Files, uninstall registry records, other users' private profiles, or the network.

Select an app to save it to the slot, then open it explicitly from **Tools → Apps**. Packaged apps keep their Windows app identity, and desktop apps keep their Start Menu shortcut behavior. **Advanced browse** remains available for a local `.exe` or `.lnk` that does not appear in the catalog. Targets are rechecked before launch, and missing or removed apps are shown as unavailable.

Nothing launches during catalog loading, searching, selection, startup, settings load, or when the Apps panel opens. Clearing a slot removes only Vajra Pulse's local reference; it never changes or uninstalls the selected application. The catalog, search text, icons, and launch history are not persisted.

### Per-app volume mixer

**Tools → Mixer** lists active Core Audio application sessions on the current default output device. Each listed app has its own volume slider and mute button; changing one does not intentionally change the master volume or another app. Apps with several audio sessions are grouped, and mixed levels are identified before a new level is applied to that group.

The list can change while apps start, stop, or move to another output device. Enumerating volume sessions does not capture, record, or upload audio and does not request microphone access. Automated verification reads session state but never changes real app volume.

### Bluetooth battery

Vajra Pulse checks Windows' standard paired-device information periodically. It briefly reports connection changes and warns when a connected Bluetooth device exposes a battery level of 20% or less. Devices that do not publish a standard Windows battery value remain unknown rather than receiving an estimated value. Clicking the activity opens Windows Bluetooth settings.

### Privacy and download activity

When Windows reports that an app is actively using the microphone or camera, the compact island shows a red privacy activity until that use ends. Clicking it opens the matching Windows privacy settings page. Vajra Pulse reads Windows' existing local usage records; it never opens either capture device.

Downloads in the local Downloads folder appear automatically while a browser or other app is writing a standard temporary download file. Completion and failure states appear briefly, and clicking the activity reveals the download in File Explorer. Monitoring is read-only: Vajra Pulse does not open, pause, alter, or upload downloads.

### Local file shelf

Drag local files/folders onto the pill, or choose **Files → Add files**. Drag a shelved item back into another app to use it. **Show folder** reveals the containing folder.

The shelf stores up to 30 local path references. It never uploads, moves, deletes, or opens files automatically. Removing a shortcut does not delete its source. Drag-out permits copy/link effects, not move. Network-share paths are not added. References are saved locally, and missing paths are omitted on startup.

### Music and seeking

Real artwork, title, artist, and playback progress come from Windows media sessions. Transport and seeking availability depend on the player. Drag the timeline or use its keyboard controls to request seeking. A player can advertise seeking but still reject a request; Vajra Pulse reports that instead of fabricating progress.

Compact bars are a playing/paused indicator, not measured audio amplitude. Playback animations stop when hidden or when reduced motion is enabled.

### Googly eyes

The compact pill's eyes follow the pointer across the desktop, blink now and then, show closed happy eyes with a sparkle when an application window opens, and turn sad with a lowered gaze and a small tear when an application window closes. Reactions use only whether a titled application window appeared or disappeared; window titles, app content, and activity are not stored or sent anywhere. Animations follow the Reduce motion setting.

### Calendar and meeting reminders

Choose **Calendar → Import .ics** and select an exported local calendar file. The next timed meeting appears with its local start/end time and a **Join meeting** button when it contains a valid HTTPS link. A reminder appears within 15 minutes of the next meeting. Importing does not sign into Google, Outlook, or any other service; re-import the export when it changes.

UTC, local/floating, known TZID, all-day, DURATION, and simple DAILY/WEEKLY recurrence with INTERVAL, COUNT, UNTIL, BYDAY, and EXDATE are supported. All-day events are not shown as meetings. Unsupported recurrence features, unknown timezones, and recurrence overrides are reported rather than silently fabricated. MONTHLY/YEARLY recurrence and arbitrary custom VTIMEZONE definitions are not supported in this version.

Import replaces the prior accepted calendar. **Clear** removes the imported state/reference, not the original .ics file. Join only opens an HTTPS link when clicked.

### Opt-in Windows notification previews

Notification reading is OFF by default. Content masking is ON by default, and no apps are allowed until selected or **Allow all apps** is explicitly enabled. Existing notifications are not replayed. Previews stay in memory, are not uploaded or logged, and do not dismiss Windows notifications.

Windows requires package identity and user consent. The ordinary app reports this requirement without trying to bypass it. An unsigned notification-capable `.msix` and explicit local-development install script are prepared in `packaging/`; see [notification setup](packaging/README.md). No package was installed and no certificate was created or trusted during development.

## Build and verification

```powershell
dotnet build .\VajraPulse.csproj -c Release
dotnet run --project .\tests\VajraPulse.CoreTests.csproj -c Release
dotnet run --project .\tests\ActivityTests\ActivityTests.csproj -c Release
dotnet run --project .\tests\CalendarTests\CalendarTests.csproj -c Release
dotnet run --project .\tests\FeatureTests\FeatureTests.csproj -c Release
dotnet run --project .\tests\BluetoothTests\BluetoothTests.csproj -c Release
dotnet run --project .\tests\ClipboardTests\ClipboardTests.csproj -c Release
dotnet run --project .\tests\DownloadTests\DownloadTests.csproj -c Release
dotnet run --project .\tests\OcrTests\OcrTests.csproj -c Release
dotnet run --project .\tests\PrivacyTests\PrivacyTests.csproj -c Release
dotnet run --project .\tests\ToolsTests\ToolsTests.csproj -c Release
.\tests\DesktopSmoke.ps1 -Mode Exercise
.\tests\AdvancedDesktopSmoke.ps1
```

The advanced desktop test uses clearly labeled temporary fixtures, imports and clears a demo calendar, adds and removes a test shelf shortcut, and never opens the demo meeting link. Run it with an empty shelf and no imported calendar. It does not request notification permission or change volume/brightness. The native feature test attempts a same-position seek only if media is paused and the player reports support; rejected requests are reported as skipped.

## Storage and limits

Preferences, visual mode, eye style, dock position, artwork-accent preference, side-button visibility, active focus state, shelf path references, the selected calendar file path, and five selected app references are stored in `%LOCALAPPDATA%\VajraPulse\settings.json`. Extracted artwork colors, CPU, memory, network, Wi-Fi, Bluetooth, launch history, audio-session state, and notification bodies are not persisted. A runtime crash report, if needed, is written to `last-error.txt`. Display selection does not survive restart. The decorative lens never opens the camera. This app does not mirror phone calls or phone activity.

## Source

- `MainWindow.xaml` / `.xaml.cs`: layout, compact/expanded interaction, polling, and animation.
- `MainWindow.Design.cs`: visual-mode selection, docking, artwork accents, and compact progress visuals.
- `MainWindow.Features.cs`: existing live states, shelf, seeking, brightness, calendar, and privacy flows.
- `MainWindow.Tools.cs`: system telemetry, connectivity rail, app slots, and active-app mixer UI.
- `MainWindow.Eyes.cs`: googly-eye pointer tracking, blinking, and app open/close reactions.
- `Core/`: independent timer and island state logic.
- `Services/SystemTelemetryService.cs`, `WifiStatusService.cs`, `InstalledAppCatalogService.cs`, `AppLauncherService.cs`, and `AudioSessionService.cs`: bounded Windows 1.3 integrations.
- `UI/PulseTheme.xaml`: native visual theme.
- `tests/`: standalone regression and desktop smoke tests.
- `packaging/`: optional notification-capable packaging and opt-in installation.
