# RP Dashboard — context for Claude Code

WPF app, .NET 8, Windows only. One window with a tab per tool, and — the reason it exists —
**one connection to the game shared by all of them**.

Same family as **Unit Alert**, **RP Pace** and **Street Medic**. It does not replace them; it
hosts them.

## Why this exists

All three of the WPF tools are tabs now. Each used to run as its own process, holding its own
WebSocket to FiveM's DevTools port and evaluating its own
`querySelectorAll('.chat__messages > li')` against the HUD every 500 ms. Three processes, three
sockets, six DOM scrapes a second inside the game's CEF, none of them aware of the others.
`NuiChatReader.cs` was literally the same file in Unit Alert and RP Pace with the namespace
changed, and a richer variant in Street Medic.

The host now does that **once**, and only while a tool is actually listening.

## The shape

```
RpDashboard.Contracts/   IRpTool + the services the host lends a tool. The ONLY shared assembly.
RpDashboard.Host/        the shell: tabs, tray icon, one status bar, the shared services
tools/<Tool>/            a tool, built as a class library; its DLL is copied into Plugins/
tests/RpDashboard.SelfTest/
build.ps1                assembles a distributable into dist/
```

**LogPusher is the odd one out and stays that way.** It is Python, and it never touches the
game — it reads Afterline's log files off disk, so it was never part of the connection problem
this host solves. `tools/LogPusher` is a *control panel* over the existing `LogPusher.exe`:
start it, stop it, and read the `state.json` and `logs/decisions.jsonl` it already writes.
It never subscribes to the chat feed, it keeps its own repository and release cycle, and it goes
on running headless when the dashboard is closed. Embedding CPython or rewriting 5,000 lines of
Python would buy nothing.

A tool is a DLL in the `Plugins` folder beside the exe. The host reflects over anything marked
`[assembly: RpToolAssembly(HostApi.Version)]`, finds the `IRpTool` implementations, and gives
each one a tab.

## The update rule (this is the point)

**Changing a tool does not mean rebuilding the dashboard.** Rebuild the tool's DLL, copy it
over the old one, restart. Each tool keeps its own repository, its own version and its own
release cadence.

The host only needs rebuilding when `RpDashboard.Contracts` changes shape — which is why that
project is kept as small as it is, and why `HostApi.Version` exists: a plugin stamps the
contract version it was built against, and the host refuses a mismatch with a readable message
rather than failing later with a cast error between two identically-named types.

Adding a member to `IToolHost` doesn't need a version bump. An old plugin simply never calls
it. Changing or removing one does.

## Where the resource win actually comes from

All in `Chat/ChatFeed.cs`, and all worth preserving:

- **One connection, one poll, fanned out in-process.** A fourth tool costs the game nothing.
- **It sleeps when nobody is listening.** The loop only runs while at least one subscription is
  active and unpaused, and dropping to zero closes the socket. Deactivate every tab and the app
  stops touching the game entirely.
- **One payload, not three.** The evaluated expression returns `outerHTML` only, sliced to the
  last 120 messages. Street Medic needs the span colour, the other two need text, and stripping
  tags in C# is cheaper than asking the game to serialise a second array.
- **The poll interval is the shortest any active subscription asked for**, floored at 250 ms.

## The chat log (replacing Afterline)

`Chat/ChatLogWriter.cs` writes the chat to disk as it arrives, deduplicated, into
`%LOCALAPPDATA%\RpDashboard\ChatLogs`. On by default; the status bar carries the toggle and a
line count, and clicking the count opens the folder.

It exists to make Afterline unnecessary. Afterline is a separate program scraping the same HUD
through the same DevTools port, so it costs a second connection to the game and 69 MB — and it
re-reads the buffer hard enough that **73% of what it writes is duplicates** (22,738 of 31,148
lines across five days; one 1.5 MB file was 79% repeats). LogPusher then spends a whole pass
throwing them away, and every agent call reads a file that is four-fifths noise.

The dashboard already solved that problem: `ChatFeed` reports each line exactly once, so
writing what it hands over is clean by construction.

**The format is deliberately Afterline's**, because LogPusher identifies that format from the
filename alone (`Chatlog [GTAWorld Roleplay English] [29-August-2026].txt`) and reads
`[HH:MM:SS] text` bodies. Pointing LogPusher at this folder needs no change to LogPusher —
verified by parsing real writer output with its Python parser: recognised as `afterline`, date
derived, session built, 0 duplicates. Two details that matter and are easy to undo by accident:
the filename must use `CultureInfo.InvariantCulture` (an English regex reads that month name on
a non-English machine), and the file must be written **without a BOM**, because LogPusher opens
it as `utf-8` rather than `utf-8-sig`.

A sidecar `.lines.jsonl` carries the sequence number and the colour each line was drawn in —
what the text format cannot hold, and the raw material for the constants Street Medic still
lacks. Afterline captures colour too, into a `.colors.jsonl` nothing ever reads.

### Phase 1: run both, then compare

Afterline is **not** to be switched off yet.

```
dotnet run --project tests/RpDashboard.SelfTest -- --compare [29-August-2026]
```

That reports lines Afterline saw and we did not, which is the only number that decides whether
switching is safe. Duplicates on their side are expected and are the point.

The risk being measured is real and runs the opposite way to Afterline's bug. FiveM never writes
chat to disk, so this log only exists while the dashboard is open: **Afterline's duplication is
noisy but safe, a tail-based reader is quiet but lossy.** The measured headroom is comfortable —
peak observed chat is 24 lines/sec against a 240/sec read — and `ChatFeed.GapSuspected` fires
when a poll finds no overlap at all with the previous one, which is the case where lines went by
unseen. The status bar says so rather than swallowing it.

Switch Afterline off once several days compare clean, not before.

## Invariants — do not break these

1. **Read-only.** Nothing in `IChatFeed` writes to the game and nothing ever should. This is the
   same rule all three tools shipped with, and it is what keeps them the right side of the
   server's rules. Loopback-only too: `NuiConnection.IsLoopback` gates the WebSocket.
2. **Never persist chat content.** Lines are matched and discarded.
3. **Per-subscription baselining.** Each subscription starts from the feed's current head, so a
   tool switched on mid-session never fires on backlog. This *replaces* the per-app
   "baseline on connect" rule, which does not survive the merge — with a shared connection, a
   tool activated late would otherwise inherit a connection that baselined an hour ago.
   Resuming from a pause re-baselines for the same reason.
4. **One tool's failure is one broken tab.** `Initialise` may throw, a plugin may fail to load, a
   line handler may throw — none of it stops the other tabs. The catches that enforce this are
   deliberate, not lazy.

## The icon

`RpDashboard.Host/Assets/rpdashboard.ico` — four tiles in a two-by-two grid, tungsten and bone
on ink. Embedded into the exe's Win32 resources by `<ApplicationIcon>`, and the tray icon reads
it back out of the running process rather than shipping a second copy of the file.

It is generated by `Assets/make-icon.py`, kept because there is no image editor in this
toolchain: the script writes the PNG frames by hand with `zlib` and `struct` and packs them into
an ICO. Change the drawing there and re-run it; nothing else references the file.

## Fragile spots

- `ClientFramePrefix` (`https://cfx-nui-client/`) assumes GTAW's HUD resource is named `client`.
  Prefix match, not exact.
- `.chat__messages > li` is GTAW's HUD markup. If the selector returns nothing but the frame is
  found, this is what changed.
- `grantUniveralAccess` is spelled that way in the DevTools Protocol itself. Not a typo to fix.
- **The poll loop's cancellation guard.** `NuiConnection` gives each of its own HTTP and
  WebSocket calls a private 2 s timeout, which throws a plain `OperationCanceledException`
  indistinguishable by type from the loop being told to stop. `ChatFeed.RunAsync`'s catch filters
  on `when (token.IsCancellationRequested)` — *its own* token — before treating it as a stop.
  Without that, one slow response from FiveM (a hitch, a HUD reload, a loading screen) ends
  polling for good. Unit Alert shipped a fix for exactly this; any new catch in that loop needs
  the same guard.
- **`UseWindowsForms` implicit usings.** The SDK injects project-wide `global using`s for
  `System.Windows.Forms` *and* `System.Drawing`, which collide with WPF's `Application`,
  `Button`, `TextBox`, `Brush` and `FontFamily`. Both are removed in the csproj and those types
  are referenced fully-qualified. Don't re-add a blanket using.
- **Hotkeys are now genuinely contended.** `RegisterHotKey` binds to a window handle and there is
  one window. Two tools that each happily took Ctrl+Alt+1 as separate processes now conflict;
  first claim wins and the loser gets null back. This is the one behaviour the merge changes.
  Street Medic holds `Ctrl+Alt+1/2/3/Q` and reports in its own UI when a claim was refused,
  dropping the key hints from its headings rather than advertising keys that do nothing — copy
  that pattern rather than swallowing the failure.
- **"Always on top" is window-wide.** Street Medic's toggle now raises the whole dashboard, not
  just its tab, because there is one window to raise. Its tooltip says so. If a second tool ever
  wants the same switch, it belongs in the host's status bar rather than in two tabs
  disagreeing about it.

## Converting a tool to a plugin

`tools/UnitAlert` is the reference. The pattern:

1. `OutputType` from `WinExe` to `Library`. Reference `RpDashboard.Contracts` with
   `Private=false` and `ExcludeAssets=runtime` — the plugin must bind to the host's copy.
2. `MainWindow` becomes a `UserControl` taking `IToolHost`.
3. Delete the tool's `NuiChatReader`, its poll loop, its overlap detection and its
   baseline-on-connect. Subscribe to `IChatFeed` and handle one line at a time.
4. Delete its `AlertPlayer` and `SettingsStore`; use `host.Audio` and `host.Settings`.
5. Delete its tray icon, its dark-title-bar call and its `App.xaml`.
6. **Keep the settings folder name.** `IRpTool.SettingsFolderName` returns the folder the
   standalone exe used (`"UnitAlert"`, not `"unit-alert"`), so existing configuration is picked
   up with no migration.
7. Its old palette keys go in a `Theme/Palette.xaml` redefined against the host's brushes, so the
   view's XAML converts without being rewritten. Street Medic needed none of this: its palette
   *is* the host's, because the host's `App.xaml` is Street Medic's promoted up.
8. **Resolve content beside the assembly, not the process.** Anything using
   `AppContext.BaseDirectory` to find a JSON file next to itself now looks in the host's folder
   and silently falls back to its embedded copy — the "edit content without a rebuild" invariant
   failing quietly. `ProtocolEngine.DefaultProtocolPath` uses `Assembly.Location`'s directory
   instead, which is the same folder for a standalone exe and the right one for a plugin.

### Where the two converted tools live

They are deliberately different, and the difference is the point.

**Unit Alert** (`tools/UnitAlert`) is a *copy*, sitting in this repository so the contract could
be proven end-to-end before anything else was touched. Its home is the `unit-alert` repository;
moving it is copying the folder and dropping the `CopyToDashboard` target.

**Street Medic** (`../StreetMedic/src/StreetMedic.Plugin`) and **RP Pace**
(`../RP Pace/RPPace.Plugin`) are the real arrangement. Each lives in its own repository and
*links* the shared source rather than forking it — one copy, and the standalone exe still builds
from it. What each plugin project adds is a view, an `IRpTool`, and its palette.

Street Medic's `Chat/FeedChatSource.cs` is the whole cost of joining the shared connection: it
wears Street Medic's own `IChatSource`, so nothing downstream knows the reader changed. 409 lines
of DevTools handshake, poll loop, buffer diffing and baselining became about forty.

RP Pace needed one thing first. Its `PaceTracker` was one class holding both the connection and
the pace logic, so the logic was split into a `PaceClock` that knows nothing about where lines
come from — `PaceTracker` still wraps it for the exe, with an unchanged public surface, and the
plugin drives the same clock off the feed. Two details there generalise:

- **A tool with a clock needs its own timer.** Chat lines can't drive one, because the case that
  matters is silence. RP Pace ticks locally at 500 ms and only takes *lines* from the feed.
- **`SnapshotAsync` is how a tool reads backlog.** Subscriptions deliberately start at the feed's
  head, so anything that needs the scrollback — RP Pace seeding its idle timer from a session
  already in progress — asks for it explicitly.

## Verified against a live game

2026-08-27, a real GTA World session. `dotnet run --project tests/RpDashboard.SelfTest -- --live`
reads the game once and prints what came back; it is the thing to run first when a tool goes
quiet, because the selector and the markup are GTAW's and can change without notice.

What that session established:

- The whole chain holds: handshake, `cfx-nui-client` frame hunt, `.chat__messages > li`, 40 lines
  back. The service-worker target confirms the HUD resource is still named `client`.
- **One socket in steady state.** Two appear briefly during the handshake — the second is the
  pooled HTTP keep-alive from the `/json` target-list fetch, which then times out. Closing the
  app releases it.
- `HtmlText` lost nothing: 0 of 40 lines came back empty after tag-stripping.
- Colour survived on 40 of 40 lines, which is what Street Medic depends on and the reason the
  feed carries `outerHTML` rather than text.
- Unit Alert and RP Pace confirmed working in play. Street Medic ran, but its auto-advance is
  scene-dependent and was not exercised end to end.

Reference constants seen, beyond the two now in `KnownColours`: `#1e90ff` server info, `#ff0000`
kick/ban/offline notices, `#ffffff` GPS. Recorded so a future capture isn't spent re-identifying
them.

## Not yet built

- Saved tab order, and remembering which tab was last open.
- **"On top" belongs to the host.** Street Medic and RP Pace each have the toggle, each now
  raises the whole window, and last-changed wins. Two tabs disagreeing about one window's state
  is a seam, not a feature — it should move to the status bar.
- A "raw chat" diagnostic tab over `IChatFeed.SnapshotAsync`, which RP Pace's Raw chat button
  currently does per-tool.
- **Street Medic's colour table is still incomplete.** `ChatLineReader.KnownColours` now has OOC
  and Radio, read off a live session. Speech, Me and Do — the three that actually decide whether
  a scene advances — are still missing, because that capture had no roleplay in it. Until they
  are filled in, Street Medic classifies those on the text patterns, which is what it has always
  done and what `DoLineFilter` is built to tolerate.
- **LogPusher's tab has not been used against a real run.** It loads and auto-detects; start,
  stop and the decision list have not been exercised while LogPusher was actually working.

## Running it

```
dotnet build
dotnet run --project RpDashboard.Host
dotnet run --project tests/RpDashboard.SelfTest
```

## Shipping it

```
.\build.ps1                              # both packages, into dist\
.\build.ps1 -Zip                         # and a zip for each
.\build.ps1 -Flavour FrameworkDependent  # just the small one
```

Two packages, same tools, same layout, differing only in whether the .NET runtime travels with
them:

| | exe | folder | zip | needs |
|---|---|---|---|---|
| `dist\self-contained\` | 146 MB | 155 MB | 63 MB | nothing |
| `dist\framework-dependent\` | 0.2 MB | 0.8 MB | 0.3 MB | .NET 8 Desktop Runtime, x64 |

The framework-dependent build is roughly **200× smaller** and is the one to hand to a machine
that already has the runtime, or to update an existing install — the runtime is the whole of the
difference. The self-contained one is for a machine with nothing on it.

The script builds the tools once and packages them into both. Street Medic and RP Pace build
from their own repositories, so it picks them up when they are sitting beside this one and
reports what it couldn't find rather than failing: a two-tab build is a valid build.

Three things it is careful about, all of which produce baffling failures if got wrong:

- **`RpDashboard.Contracts.dll` must not end up in `Plugins\`.** A second copy there gives each
  plugin a different `IRpTool` from the one the host casts to, and every tool fails to load with
  a cast error between two identically-named types. The plugin projects reference it with
  `Private=false` to prevent that; the script asserts the packaging didn't reintroduce it.
- **Content travels with its tool.** Street Medic's `Assets\` and RP Pace's `Emotes\` go into
  `Plugins\` beside their DLLs, because both resolve content relative to their own assembly.
- **`build.ps1` is saved UTF-8 *with* BOM.** Windows PowerShell 5.1 reads a BOM-less file as
  ANSI, and the em dashes in the comments then decode into a curly quote that terminates a
  string early — the file fails to parse, at a line nowhere near the actual character. If you
  rewrite this script with a tool that strips the BOM, that is why it suddenly won't run.

Single-file is the default for both. Plugin loading was verified *inside* each bundle rather
than assumed — that is what `last-start.log` below was added for. The `Plugins` folder stays
loose beside the exe on purpose: tools are loaded from disk by path, which is exactly what lets
one be replaced without rebuilding the host.

## When a tab is missing

`%LOCALAPPDATA%\RpDashboard\last-start.log`, overwritten each start, lists what loaded and what
didn't and why. Clicking the tool count in the status bar shows the same thing while running.
The usual causes are a DLL built against an older `HostApi.Version`, a content folder that
didn't get copied, and a plugin that shipped its own copy of the contracts assembly.

The self-test covers the new-line overlap rule, the HTML stripping, and — importantly — loads
the real `Plugins` folder through `PluginLoader` and checks each tool binds to the host's
contract assembly rather than its own copy. That last check is what catches the plugin split
breaking.
