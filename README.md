# Baton

Baton moves what you are doing between your Windows PC and your Android phones, right to the
second. Press **Ctrl+Alt+→** on the PC and the video, song, page or app you were using continues on
your phone; tap **Continue here** on the phone (or press **Ctrl+Alt+←** on the PC) and it comes back.

## How things continue

No operating system lets an app move another app's state, so Baton picks the best way for each
kind of activity and falls back to streaming the window when nothing better exists.

| On the source | How it continues on the other device |
| --- | --- |
| Browser tab with the Baton extension (Chrome, Edge, Opera) | Exact URL and position. YouTube opens in the YouTube app at the same second; other sites open in the browser, and the extension seeks the page's player. |
| Video in any browser without the extension (Zen, Firefox...) and the YouTube app | The video is found by title, channel **and length**, so a trailer never replaces a film. |
| Spotify, YouTube Music | Spotify track links, or Android's play-from-search, then the position is set through the app's media session. |
| Netflix, Disney+, Prime Video | The app opens on the title; the service's own resume puts you at the right place. |
| Local media files (VLC, mpv, MPC, Media Player...) | The file stays on the PC; Baton's player on the phone streams it over the pinned connection from the same second. **Send back to PC** reopens it there. |
| Anything else (games, Harbor, any app window) | Live stream of the window to the phone, which you control from there (see *Controlling a PC window from the phone*). Any PC window can also be streamed on purpose with **Stream** / **Stream instead**. |
| Any phone app | Its PC version when there is one: WhatsApp, Telegram, TikTok, To Do, Samsung Notes... (matched by known pairs, by name, and Google Play Games titles by package), else its website, else the phone's screen shown on the PC and driven with the mouse and keyboard (Android asks once per handoff to share the screen). |
| Phone to phone | Relayed through the PC. |

Positions are reconciled after opening: Baton waits for the target app's media session and seeks
it, so even links without timestamps resume at the right second.

### Choosing the app

The first time an app is continued, Baton asks where it should open (the same app on the other
device, a specific browser, the website, or a live mirror/stream) and remembers the answer; there is
no question when there is only one way. Cards show "Opens in …" with **Change**; the PC's
**Choose app…** and the phone's settings list the remembered choices, each removable. On the phone,
**Continue another app on PC…** lists every installed app, not only the one in front. What is passed
along: links (pages open in the chosen browser), media at the same second, the game itself; an
app like WhatsApp opens at its home screen, since no app exposes which chat was open.

### Same tab, per site, your build

- Media coming back to the PC continues in the tab (or app) that still has it, paused where it
  left, instead of opening another tab: same video, else same page, else same title.
- In a browser, choices are remembered per site (YouTube, x.com...), not per browser.
- With several builds of an app on a phone (YouTube, YouTube ReVanced, RVX), Baton opens the one
  that last played something, lists each in "Continue with…", and never picks a disabled one.

### Zen and other Firefox-based browsers

`scripts\Build.ps1` also builds `artifacts\browser\baton-zen.xpi`. Zen installs it unsigned:
drop it on a Zen window, or copy it to the profile's `extensions` folder as `baton@baton.dev.xpi`
and restart Zen. Firefox gives extensions a random origin, so this build carries a per-PC token
(`%LOCALAPPDATA%\Baton\browser-token`) that the PC checks.

### Instant

Every device already knows the other devices' activities, so **Continue here** opens the app at
once from that copy while the other device pauses and sends the exact position; the position then
only corrects a difference (more than 2 s). Streams need no connection setup: each phone keeps one
media channel to the PC open for the whole session, and the PC keeps its capture/encode pipeline
warm in the phone's shape. YouTube IDs for title-only videos are looked up in the background
before anyone asks. Typical times on the test setup (PC + Pixel emulator):

| Handoff | Time |
| --- | --- |
| YouTube tab (PC) → YouTube app (phone) | app opening after 67 ms, exact position after 300 ms |
| PC window stream, first frame | 110–180 ms with the viewer open, about 0.7–1 s from a cold start on the emulator |
| Phone app → PC window | consent prompt after 0.6 s, picture 1 s after accepting it |
| Remote play/pause/seek | one message, applied on arrival |

### Controlling a PC window from the phone

- **Touch** (default): your fingers are real Windows touch input, so apps scroll, pinch-zoom and
  drag on their own, and press-and-hold is a right click. Menus and pop-ups show because an
  on-screen window is captured from its monitor.
- **Trackpad**: slide to move the cursor, tap to click, tap-and-hold then slide to drag,
  two-finger tap to right-click, two fingers to scroll, pinch to zoom the picture.
- **Keyboard**: any keyboard app, plus a key row with Esc, Tab, sticky Ctrl/Alt/Shift/Win, arrows,
  Del and F1–F12. A Bluetooth keyboard works too, shortcuts included.
- **Fit** resizes the PC window to the phone's shape while you use it and restores it afterwards;
  **⟲** switches between landscape and portrait; **On PC** ends the stream and brings the window
  to the front on the PC. The PC stays awake while it is being streamed.

### Remote control without moving anything

Cards for media on the other device have play/pause, ±10 s, a seek bar and the app's volume. The
PC's playback also appears in Android's media controls (notification shade, lock screen, headset
buttons), and the PC's Baton window can play, pause and skip what the phone is playing.

## Using it

**PC.** Baton lives in the notification area. Click its icon for the quick panel. Shortcuts:
Ctrl+Alt+→ send to the phone used last, Ctrl+Alt+← continue the phone's activity here,
Ctrl+Alt+↑ open the panel. When you come back to the PC, Baton offers to continue what your phone
was playing (or showing in the last few minutes, if you are no longer using it), once per 30 minutes;
*Settings* → *Offer to continue from my phone* turns this off.

**Phone.** The Baton notification always shows what is on the PC with a **Continue here** button.
There is also a **Send to PC** Quick Settings tile, a **Continue on PC** share target, and the home
screen lists everything you can continue in both directions. When you pick up the phone after
leaving the PC, Baton offers to continue what the PC was playing or showing, by the same rules
(*Settings* → *Offer to continue from my PC*).

## Install

1. **PC:** download `Baton.App-win-Setup.exe` from the [latest release](https://github.com/TheTobiTiger02/Baton/releases/latest)
   and run it. It installs for your user only (no admin prompt), adds Baton to the Start menu, starts
   it, and keeps it up to date: new releases download in the background and a toast offers
   *Restart to update*.
2. **Phone:** in Baton on the PC choose *Pair a phone* and scan the small *No Baton on the phone yet?*
   code with the phone's camera, or download `Baton.apk` from the release. Install it (Android asks
   to allow installs from the browser once), open Baton and follow its three steps. The app also
   updates itself from the releases (*Settings → About*).

## Releasing

`scripts\Release.ps1 -Version 1.2.3` builds the self-contained Windows installer with
[Velopack](https://velopack.io) (`dotnet tool install -g vpk`), a signed release APK and publishes both
as GitHub release `v1.2.3`; `-NoPublish` only builds. The APK is signed with the key named in
`android\keystore.properties` (`storeFile`, `storePassword`, `keyAlias`, `keyPassword`; never
committed). Keep that key: phones only accept updates signed with the same one. The Zen/Firefox
extension is not part of releases, since each build carries its PC's private browser token.

## Setup from source

1. **PC:** run `scripts\Install.ps1` after a build (`scripts\Build.ps1` does it for you). It installs to
   `%LOCALAPPDATA%\Programs\Baton`, adds **Baton** to the Start menu (so Windows search finds it) and
   starts it. It asks nothing; it listens on the LAN only. `Install.ps1 -Uninstall` removes it again
   (pairings and settings stay).
2. **Phone:** install the APK, open Baton, grant *Notifications* and *Media access*
   (and *Instant open* so handoffs open without a tap).
3. **Pair:** PC → *Devices* → *Pair a phone*, then scan the QR code in Baton (the camera app works
   too), or pick the PC in the phone's list and type the 6-digit code.
4. **Browser extension (optional, recommended):** PC → *Settings* → *Open extension folder*, then
   in `edge://extensions` or `chrome://extensions` enable Developer mode → *Load unpacked* → that folder.

## Build

```powershell
.\scripts\Build.ps1
```

Produces `artifacts\windows\Baton.exe` (needs the .NET 9 Desktop Runtime) and
`artifacts\android\Baton.apk`, then installs the PC app (see *Setup*; `-NoInstall` skips that).
Autostart follows the installed copy. Errors that would have closed the app are written to
`%LOCALAPPDATA%\Baton\crash.log`, and Windows restarts Baton if it still crashes. Tests:

```powershell
dotnet test tests\Baton.Tests
cd android; .\gradlew.bat :app:testDebugUnitTest
```

## Architecture

```
src/Baton.Protocol   Envelope, messages, Activity model (JSON, camelCase)
src/Baton.Media      Window/monitor capture (Windows.Graphics.Capture), GPU NV12 conversion,
                     hardware H.264 encoder (Media Foundation), per-app audio loopback, touch and
                     SendInput injection, per-app volume, H.264 decoder and presenter for phone mirrors
src/Baton.Host       Pinned WSS host, pairing and trust store, discovery, handoff coordinator,
                     activity sources (media sessions, browser bridge, windows, local files), openers
src/Baton.App        WPF app: tray, hotkeys, windows, the phone mirror window
tools/Baton.DevHost  Headless host for development, driven from the console
browser-extension/   Chromium MV3 extension
android/             Kotlin + Compose app
```

The PC is the hub: phones connect to it; it relays between phones. Each device publishes its
continuable activities (`activity.list`); the PC sends every phone the others' (`peers`). A
handoff is `handoff.pull` (ask the owner) → `handoff.deliver` (activity, plus a stream offer when
it continues as a window stream) → `handoff.result`.

A `handoff.pull` for an activity the target already knows carries `speculative: true`: the target
has opened it already and only reconciles. `mode: "stream"` asks for the window (or the phone's
screen) instead of the native app. `media.command` plays, pauses, seeks or sets the volume of an
activity where it is; `stream.control` stops, returns or fits a stream.

Media travels on one persistent pinned-TLS socket per phone (`media.channel`, stream kind 6).
Every record carries its channel in the header's second byte: 0 video (H.264 Annex-B), 1 audio
(48 kHz 16-bit stereo PCM), 2 control (binary input messages, identical codec in C# and Kotlin),
3 meta (JSON format/end), 255 keepalive. When the network falls behind, the PC drops video frames
up to the next keyframe rather than queueing latency. The PC's encoder output gets a VUI
`bitstream_restriction` (no frame reordering) written into its SPS so decoders show each frame at once.

Ports (LAN): UDP 7837 discovery, TCP 7838 pinned WSS sessions and `/files/{token}`, TCP 7839 stream
sockets. Loopback only: TCP 7836 browser extension.

## Security

- Pairing pins the PC's certificate from the QR code; sessions use pinned TLS and an HMAC
  challenge, and the trust key never crosses the network after pairing. Trust keys are protected
  with DPAPI on the PC and Android Keystore on the phone.
- Stream sockets need single-use tickets issued inside an authenticated session. Shared files are
  served only by random per-file tokens, sent only to paired phones.
- The browser endpoint listens on loopback and accepts only the Baton extension's origin.

## Limits

- Protected video (the Netflix, Disney+ and Prime *Windows apps*) captures as black, so those
  continue by opening the title instead of streaming.
- Streaming drives the PC's real mouse, keyboard and touch input: someone at the PC sees it, and
  it cannot work while the PC is locked. While a window is streamed, the PC is kept from sleeping
  or locking, which also means it stays unlocked while you are away.
- Showing a phone app on the PC needs Android's screen-sharing consent. The phone's *Settings* →
  *Showing this phone on your PC* chooses how: **Ask each time**; **Keep ready** (asked once, then
  kept between handoffs with the cast icon showing, until stopped, a restart or 4 hours unused; some
  Android versions end it when the phone locks); or **Shizuku** (the Shizuku app grants it for good,
  and the PC's input arrives as real multi-touch, which games that read raw touch accept). The phone
  shows its casting indicator while it is shown.
- The Shizuku mode needs Shizuku 13.6+ set up once without a computer: in Shizuku, *Pairing* (with
  Wireless debugging's pairing code), then *Start*. Baton then grants Shizuku `WRITE_SECURE_SETTINGS`,
  so on Android 13+ it starts by itself after a restart when the phone is on a Wi-Fi where Wireless
  debugging is always allowed, and Baton switches Wireless debugging off again once Shizuku runs
  (Shizuku keeps running without it). Keep *Developer options* and *USB debugging* on. Sound comes along when Baton has the microphone
  permission (Android requires it for playback capture); apps that forbid capture stay silent.
- The PC's input into a phone app goes through Baton's accessibility service: taps, swipes,
  long presses (hold the mouse button), the wheel and typing into text fields work; games that
  read raw multi-touch do not, unless the Shizuku mode is on. If the service is off, the PC window says "View only" and the
  phone opens its Accessibility settings.
- After updating the browser extension's files, reload it in `edge://extensions` (or the
  browser's equivalent) so remote play/pause/seek of tabs works.
- The browser extension targets Chromium browsers; Firefox-based browsers work through the
  title-and-length lookup.
