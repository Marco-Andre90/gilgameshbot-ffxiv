# GilgameshBot

A [Dalamud](https://dalamud.dev) plugin for Final Fantasy XIV that relays **Free Company chat to a Discord channel**. Named after the dimension-hopping Gilgamesh: while an officer running the plugin is in the game, the FC's conversation jumps over to Discord.

> **Current phase: 2 — FFXIV → Discord, several officers.**
> One Free Company, one Discord channel, any number of officers running the plugin: exactly one of them relays and the rest wait in a standby queue. The roadmap and the reasoning behind the design live in [`docs/ROADMAP.md`](docs/ROADMAP.md); this README only covers what works today.

## What it does today

- While your character is logged in, the plugin connects to Discord as **GilgameshBot** and posts every Free Company chat line to the configured channel as `**Character Name**: message`.
- `@name` typed in game becomes a real Discord mention (username, display name or role). `@everyone` and `@here` are never relayed as mentions.
- The channel gets **"GilgameshBot Online!"** when relaying starts and **"GilgameshBot Offline."** when the last officer stops relaying cleanly (logout, `/gilgamesh disconnect`, plugin unload). If the game crashes, no message is posted, but the bot's presence in the member list goes offline on its own, so members can still tell whether chat is being relayed.
- **Several officers can run the plugin at the same time** without duplicating anything. The plugin needs a *state channel* for this. Each running plugin keeps one presence message there; the one that has been running longest relays, the others sit on standby and show their place in the queue. When the relaying officer logs out, the next in line takes over — cleanly and immediately, or within about 90 seconds if the game crashed. Messages received while on standby are dropped, never replayed, so nothing is ever posted twice.

### Known limitations in this phase

- After a crash (not a clean logout) there is a gap of up to ~90 seconds before the next officer takes over. This is deliberate: the crashed instance's lease has to expire before anyone else may relay, which is what makes duplicates impossible.
- One FC and one channel per configuration. Multiple FC branches (one channel each) is Phase 3.
- Nothing goes from Discord back into the game. That is Phase 4.
- The bot token is stored in plain text in the plugin config file (see [Security notes](#security-notes)).

## Requirements

- Final Fantasy XIV launched through [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.
- A Discord server where you can create channels and invite bots.

## Discord setup (once per FC)

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) → **New Application** → name it `GilgameshBot`.
2. **Bot** tab → **Reset Token** → copy the token. You will paste it into the plugin. No privileged intents are needed — the bot reads only its own presence messages, over REST.
3. **OAuth2 → URL Generator**: scope `bot`; permissions **View Channels** and **Send Messages** only. Do **not** grant *Mention Everyone*: the plugin never mass-pings, and only roles marked *Allow anyone to @mention this role* can be mentioned from the game. Open the generated URL and invite the bot to your server.
4. In Discord, enable **Settings → Advanced → Developer Mode**, then right-click the server → **Copy Server ID**, and right-click the target channel → **Copy Channel ID**.
5. Make sure the bot can see and post in that channel (check the channel's permission overrides).
6. Create a **state channel**: a private text channel that no one needs to read — for example `#gilgamesh-state`, visible to officers only. Give the bot **View Channel**, **Send Messages** and **Read Message History** on it. *Manage Messages* is **not** required: the plugin only ever edits and deletes messages the bot itself posted. Copy its ID the same way and paste it into **State channel ID** in the plugin settings. Every officer running the plugin must point at the **same** state channel.

The state channel fills up with one short message per running plugin (`🎮 Character @ World · beat 42`), which the plugins keep updating and clean up after themselves.

## Build

```powershell
dotnet build GilgameshBot/GilgameshBot.csproj -c Release
```

Dalamud's reference assemblies are picked up automatically from `%AppData%\XIVLauncher\addon\Hooks\dev\` (created the first time XIVLauncher runs with Dalamud). On another machine or in CI, point the `DALAMUD_HOME` environment variable to an extracted copy of `https://goatcorp.github.io/dalamud-distrib/latest.zip`.

Output: `GilgameshBot/bin/x64/Release/GilgameshBot.dll`, plus `GilgameshBot/bin/x64/Release/GilgameshBot/latest.zip` and `GilgameshBot.json` produced by DalamudPackager.

## Install as a dev plugin

1. In game: `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the full path to `GilgameshBot.dll` from the build output → **Save and Close**.
2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **GilgameshBot**.
3. After each rebuild, reload it from the same list (or use the Dalamud dev auto-reload option).

## Configure

1. `/gilgamesh` opens the settings window (also reachable from the plugin installer's settings button).
2. Paste the **bot token**, the **server ID**, the **channel ID** and the **state channel ID** → **Save Discord settings**.
3. Click **Connect** (or log out and back in with *Connect automatically* enabled).

The channel should receive `🟢 GilgameshBot Online! Relaying Free Company chat via <Character> @ <World>`.

### Commands

| Command | Effect |
|---|---|
| `/gilgamesh` | Open settings / status |
| `/gilgamesh connect` | Connect to Discord now |
| `/gilgamesh disconnect` | Post the Offline notice and disconnect |
| `/gilgamesh status` | Print connection state, relayed-message count, and whether this instance is relaying or on standby |

### Options

| Option | Default | Notes |
|---|---|---|
| Connect automatically when a character logs in | on | Disconnects again on logout |
| Relay Free Company chat | on | Master switch; the bot can stay connected without relaying |
| Include my own messages | on | Turn off if you only want *other* members' lines relayed |
| Turn `@name` into Discord mentions | on | Exact match on a mentionable role name, then username, then display name |
| Post Online / Offline announcements | on | |
| Delay between messages (ms) | 300 | Spreads out a busy chat; Discord.Net still handles rate-limit retries |
| State channel ID | required | Channel holding the presence messages; the same for every officer |
| Heartbeat (s) | 30 | How often this instance refreshes its presence message. Clamped to 10–120 |
| Stale after (s) | 90 | How long a silent instance keeps its place in the queue, and how long this one keeps relaying without a successful heartbeat. Clamped to at least twice the heartbeat, at most 600 |

### Mentions from the game

Type `@` followed by the person's Discord **username** (the lowercase handle, no spaces), for example `@marco` or `@marco.andre`. Display names without spaces and mentionable role names (`@Officers`) work too; matching is exact and case-insensitive. Names with spaces or non-Latin characters cannot be matched in this phase. A raw `<@123>` typed in game is shown as text and pings nobody — only mentions the plugin resolved itself are allowed to notify.

## Security notes

- The token lives in `%AppData%\XIVLauncher\pluginConfigs\GilgameshBot.json` in plain text. Anyone with that file can act as the bot. Do not share the file; if it leaks, **Reset Token** in the Developer Portal and paste the new one.
- The plugin never logs the token. Discord.Net's own log lines are forwarded to Dalamud's log (warnings and errors at their level, the rest at debug).
- Everything relayed is visible to everyone who can read the Discord channel. Agree with your FC on what gets relayed, and consider making the channel private to FC members.

## Troubleshooting

- **"Discord is not configured"** — token, server ID, channel ID or state channel ID is missing. Save them first.
- **"Bot is not a member of server …"** — the invite step was skipped, or the server ID is wrong.
- **"Channel … not found"** — wrong channel ID, or the bot lacks *View Channel* on it.
- **"State channel … not found"** — wrong state channel ID, or the bot lacks *View Channel* / *Read Message History* on it. The plugin does not connect until it can see that channel.
- **Two officers, messages still duplicated** — both must have the **same** state channel ID saved, and must reconnect after saving it. `/gilgamesh status` says which one is relaying.
- **Connected but nothing arrives** — confirm the message really went to the Free Company channel (`/fc`), and that *Relay Free Company chat* is on. `/xllog` shows the plugin's log.
- **Mentions don't resolve** — the name must match the username or display name exactly (roles must be mentionable); check the exact handle in the member list.

## A word on third-party tools

Square Enix's terms of service do not allow third-party tools. Dalamud plugins are widely used and tolerated in practice, but the risk is carried by the account running them. Keep the usual etiquette: don't mention plugins in game or on official channels.

## License

Copyright (C) 2026 Marco André Innocenti

GilgameshBot is free software, licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0-only)** — see [`LICENSE`](LICENSE) for the full text. In short: you may use, study, modify and redistribute it, but any modified version you distribute *or make available to users over a network* must also be offered under the AGPL-3.0 with its source (see section 13 of the license). It comes with no warranty.

GilgameshBot is an independent implementation. It was informed by prior FFXIV↔Discord plugins listed in [`docs/ROADMAP.md`](docs/ROADMAP.md) as references, but does not incorporate their code.
