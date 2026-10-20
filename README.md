# RP Dashboard

One window, one connection to the game, for the GTA World RP tools.

**Unit Alert**, **RP Pace** and **Street Medic** each used to run as their own process, and each
held its own WebSocket to FiveM's DevTools port, evaluating its own
`querySelectorAll('.chat__messages > li')` against the HUD every 500 ms. Three processes, three
sockets, six DOM scrapes a second inside the game, none of them aware of the others.

This hosts them as tabs and does that **once** — and only while a tool is actually listening.
Switch every tab off and the app stops touching the game entirely.

## Download and run

Grab one zip from the [latest release](https://github.com/Kraethas/RP-Dashboard/releases/latest),
unzip it anywhere, and run **`RpDashboard.exe`**. All four tabs are already inside — nothing else
to install or wire up.

| Download | Size | Needs |
|---|---|---|
| `RP-Dashboard-<version>-self-contained.zip` | ~63 MB zipped | nothing — just run it |
| `RP-Dashboard-<version>-framework-dependent.zip` | ~0.3 MB zipped | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (x64) installed once |

Pick **self-contained** if you just want it to run. Pick **framework-dependent** if you already
have the .NET 8 Desktop Runtime (or don't mind installing it once) and want a tiny download.

The app lives in the system tray. First launch opens the window; closing it minimises to the
tray, and right-clicking the tray icon exits. Chat logs are saved automatically — see
[Chat logs](#chat-logs) for where they go and how to change it.

**The LogPusher tab is the exception** — it's a control panel for a separate Python program
(session-archiving to Notion) that needs its own API keys, so it won't do anything without that
setup. The other three tabs — Unit Alert, RP Pace, Street Medic — work the moment you launch.

## What's in it

Four tools, one per tab:

- **Unit Alert** — watches chat for callsigns you choose and plays a sound the moment one
  appears, so you don't miss a page while you're looking elsewhere.
- **RP Pace** — shows how long it's been since your last roleplay action, a nudge to keep a
  scene moving, with a browsable library of emotes to copy from.
- **Street Medic** — a step-by-step triage and first-aid protocol card that follows the scene,
  for playing a medic without memorising the whole procedure.
- **LogPusher** — start, stop and watch the session archiver that saves your chat logs and
  pushes session summaries to Notion.

### How the dashboard consolidates them

Unit Alert, RP Pace and Street Medic each used to be its own program, and each opened its own
connection to the game and scraped the chat HUD on its own timer. Three programs doing the same
read three times over. The dashboard **reads the game once** and hands every tab the same chat,
and it only reads at all while a tab is actually listening — switch every tab off and it stops
touching the game entirely. One window, one connection, one set of chat logs.

Each tool is a plugin (a DLL in the `Plugins` folder beside the exe), so a tool can be updated on
its own without rebuilding the dashboard. LogPusher is the odd one out: it is a separate Python
program that reads chat *log files* off disk and never touches the game, so its tab is a control
panel over that program rather than a tab that reads chat itself.

## Chat logs

The dashboard writes your chat to disk as it arrives, de-duplicated, on by default. The status
bar carries an on/off toggle and a running line count; **click the count to open the folder**.

**Where it saves.** By default, `%LOCALAPPDATA%\RpDashboard\ChatLogs`
(e.g. `C:\Users\You\AppData\Local\RpDashboard\ChatLogs`).

**To choose your own folder:**

1. Exit the dashboard first (tray icon → Exit). The setting is read once at startup.
2. Open (or create) `%LOCALAPPDATA%\RpDashboard\host.json` in a text editor.
3. Add a `ChatLogFolder` value — JSON, so back-slashes are doubled:

   ```json
   {
     "ChatLogEnabled": true,
     "ChatLogFolder": "D:\\GTA RP\\Chat Logs"
   }
   ```

4. Start the dashboard again. If the folder can't be created (a drive that isn't there), it
   quietly falls back to the default rather than failing to log.

**What gets stored.** One pair of files per in-game day, named like Afterline's so other tools
can read them unchanged:

- `Chatlog [GTAWorld Roleplay English] [20-October-2026].txt` — the chat itself, one
  `[HH:MM:SS] message` per line, on the **game's** clock (so it lines up with other players'
  logs), with duplicates already removed.
- `Chatlog [GTAWorld Roleplay English] [20-October-2026].lines.jsonl` — a companion file, one
  JSON record per line, carrying what the plain text can't: a sequence number, both the game and
  local timestamps, the line's colour, and the original HUD markup. Nothing has to read it; it's
  there so no detail is lost.

## Read-only, always

No tool here ever sends input to the game. They read chat and write to the clipboard; the human
pastes. That is what keeps them the right side of the server's rules, and there is deliberately
no method on the shared chat feed that could write.

## Building

```
dotnet build
dotnet run --project RpDashboard.Host
dotnet run --project tests/RpDashboard.SelfTest          # unit tests
dotnet run --project tests/RpDashboard.SelfTest -- --live # read the running game once
```

Packaging, into `dist/`:

```
.\build.ps1              # both flavours
.\build.ps1 -Zip         # and a zip for each
```

| | exe | folder | needs |
|---|---|---|---|
| `dist\self-contained\` | 146 MB | 155 MB | nothing |
| `dist\framework-dependent\` | 0.2 MB | 0.8 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime), x64 |

## More

`CLAUDE.md` carries the architecture, the invariants, the fragile spots, and the steps for
converting another tool into a tab.

When a tab is missing, read `%LOCALAPPDATA%\RpDashboard\last-start.log`.
