# GilgameshBot

A [Dalamud](https://dalamud.dev) plugin for Final Fantasy XIV that relays **Free Company chat to a Discord channel**. Named after the dimension-hopping Gilgamesh: while an officer running the plugin is in the game, the FC's conversation jumps over to Discord.

One bot serves any number of Free Company branches (one per home world + Free Company name), each with its own Discord server and channels. The plugin picks the branch from the character you log in as. Any number of officers can run it: exactly one of them relays and the rest wait in a standby queue. Install it from the plugin installer and configure it by pasting one setup code.

## What it does

- While your character is logged in, the plugin connects to Discord as **GilgameshBot** and posts every Free Company chat line to the configured channel as `**Character Name**: message`.
- **The Free Company branch is picked from the character you log in as.** The plugin reads your home world and Free Company name and looks them up in a branch table; each branch has its own Discord server, relay channel and state channel. An officer with characters in two branches needs no extra setup — log in as the Kraken character and Kraken's channel gets the chat, log in as the Famfrit one and Famfrit's does.
- `@name` typed in game becomes a real Discord mention (username, display name or role). `@everyone` and `@here` are never relayed as mentions.
- The channel gets **"GilgameshBot Online!"** when relaying starts and **"GilgameshBot Offline."** when the last officer stops relaying cleanly (logout, `/gilgamesh disconnect`, plugin unload). If the game crashes, no message is posted, but the bot's presence in the member list goes offline on its own, so members can still tell whether chat is being relayed.
- **One setup code configures everybody else.** The officer who created the bot exports a single string carrying the token and *every* branch; every other officer imports it and is ready to relay on any of their characters, without ever touching a Discord ID.
- **Several officers can run the plugin at the same time** without duplicating anything. The plugin needs a *state channel* for this. Each running plugin keeps one presence message there; the one that has been running longest relays, the others sit on standby and show their place in the queue. When the relaying officer logs out, the next in line takes over — cleanly and immediately, or within about 20 seconds if the relaying officer's game closed unexpectedly, once that instance's lease expires. Messages received while on standby are dropped, never replayed, so nothing is ever posted twice.

## Requirements

- Final Fantasy XIV launched through [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled.
- For the officer who sets the bot up: a Discord server where you can create channels and invite bots.

## Install

1. In game, type `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Paste this URL into the empty row, click **+**, then **Save and Close**:

   ```
   https://github.com/Marco-Andre90/gilgameshbot-ffxiv/releases/latest/download/repo.json
   ```

3. Type `/xlplugins`, search for **GilgameshBot**, click **Install**.

Updates arrive through the plugin installer like any other plugin — nothing to download by hand.

## Configure

### Got a setup code?

An officer in your FC has already set the bot up and sent you one long line of text (by private message). That is all you need:

1. Copy the code.
2. In game: `/gilgamesh` → **Import from clipboard** (or just type `/gilgamesh import`, or the short form `/gilga import`).

That's it — the plugin looks up your character's branch and connects right away. The token and every branch (server, relay channel, state channel) are filled in for you. The other options (auto-connect, whether your own lines are relayed, …) stay personal to you.

### Setting up the bot for your FC (once)

Do the [Discord setup](#discord-setup-once-per-branch) below (once per branch), then:

1. `/gilgamesh` opens the settings window (also reachable from the plugin installer's settings button).
2. On the **Discord** tab, paste the **bot token** → **Save token**. One bot serves every branch.
3. Still on the Discord tab, fill in the branch editor and click **Add branch**:
   - **Name** — a label for you, e.g. `Kraken`.
   - **Home world**, **FC name** and **FC tag** — click **Use my character** while logged in on a character in that FC and all three are filled in for you. The **home world + FC name** pair is what a character is matched on: FC names are unique on a world, FC tags are not (two Free Companies on one world may share a tag), so the tag is only a label and an extra check. The FC name is the full name spelled out on your Free Company profile; the tag is what appears in « » next to your name, typed without the brackets.
   - **Server ID**, **Channel ID**, **State channel ID** — from the Discord setup below.
4. Repeat step 3 for every other branch. Each branch needs its **own** state channel; two branches sharing one would fight over who relays.
5. Click **Connect** (or log out and back in with *Connect automatically* enabled).
6. On the **Status** tab, click **Export setup code** — the code is copied to your clipboard — and send it to your fellow officers **by private message**. They import it and are done, on every one of their characters.

The channel should receive `🟢 GilgameshBot Online! Relaying Free Company chat via <Character> @ <World>`, and the Status tab shows `Branch: Kraken («KRKN» Kraken Company @ Behemoth)`.

Officers with characters in more than one branch need nothing extra: one setup code covers all of them, and the plugin switches branch when they switch character.

## Discord setup (once per branch)

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) → **New Application** → name it `GilgameshBot`.
2. **Bot** tab → **Reset Token** → copy the token. You will paste it into the plugin. No privileged intents are needed — the bot reads only its own presence messages, over REST.
3. **OAuth2 → URL Generator**: scope `bot`; permissions **View Channels** and **Send Messages** only. Do **not** grant *Mention Everyone*: the plugin never mass-pings, and only roles marked *Allow anyone to @mention this role* can be mentioned from the game. Open the generated URL and invite the bot to your server.
4. In Discord, enable **Settings → Advanced → Developer Mode**, then right-click the server → **Copy Server ID**, and right-click the target channel → **Copy Channel ID**.
5. Make sure the bot can see and post in that channel (check the channel's permission overrides).
6. Create a **state channel**: a private text channel that no one needs to read — for example `#gilgamesh-state`, visible to officers only. Give the bot **View Channel**, **Send Messages** and **Read Message History** on it. *Manage Messages* is **not** required: the plugin only ever edits and deletes messages the bot itself posted. Copy its ID the same way and paste it into **State channel ID** in the branch editor. Every officer relaying a given branch must point at the **same** state channel — and every branch needs its **own**.

Repeat steps 3–6 for each branch: invite the same bot to that branch's Discord server, then copy its server, channel and state channel IDs. Creating the application and its token (steps 1–2) happens only once.

The state channel fills up with one short message per running plugin (`🎮 Character @ World · beat 42`), which the plugins keep updating and clean up after themselves.

## Commands

| Command | Effect |
|---|---|
| `/gilgamesh` | Open settings / status |
| `/gilgamesh connect` | Connect to Discord now |
| `/gilgamesh disconnect` | Post the Offline notice and disconnect |
| `/gilgamesh status` | Print connection state, the active branch (or why none matched), relayed-message count, and whether this instance is relaying or on standby |
| `/gilgamesh import` | Import a setup code from the clipboard |
| `/gilga` | Short for `/gilgamesh` — works with every subcommand above (`/gilga import`, `/gilga status`, …) |

## Options

| Option | Default | Notes |
|---|---|---|
| Connect automatically when a character logs in | on | Disconnects again on logout |
| Relay Free Company chat | on | Master switch; the bot can stay connected without relaying |
| Include my own messages | on | Turn off if you only want *other* members' lines relayed |
| Turn `@name` into Discord mentions | on | Exact match on a mentionable role name, then username, then display name |
| Post Online / Offline announcements | on | |
| Delay between messages (ms) | 300 | Spreads out a busy chat; Discord.Net still handles rate-limit retries |
| Free Company branches | at least one | Table on the Discord tab: one row per FC, with a label, the home world and Free Company name it is matched by (plus the FC tag as a label and extra check), and its own Discord server, relay channel and state channel. The plugin picks the row matching the logged-in character |
| Heartbeat (s) | 10 | How often this instance refreshes its presence message. Clamped to 10–120 |
| Stale after (s) | 20 | How long a silent instance keeps its place in the queue, and how long this one keeps relaying without a successful heartbeat. Clamped to at least twice the heartbeat, at most 600 |

## Mentions from the game

Type `@` followed by the person's Discord **username** (the lowercase handle), for example `@marco` or `@marco.andre`. Display names and mentionable role names work too, including ones with spaces: `@Justice Archon` pings that role or member if the name matches exactly (case-insensitive). The plugin tries the longest match first, up to four words, and leaves the rest of the sentence as text. A raw `<@123>` typed in game is shown as text and pings nobody — only mentions the plugin resolved itself are allowed to notify.

## Security notes

- **The setup code contains the bot token.** Treat it like a password: send it by **private message** only, never in a public or FC-wide channel, never in a screenshot, never in a pastebin. The plugin never shows it on screen and never writes it to the log — it only ever passes through your clipboard.
- If a setup code (or the token) leaks: **Bot → Reset Token** in the [Developer Portal](https://discord.com/developers/applications), paste the new token in the plugin, **Save token**, then **Export setup code** again and send the new code to every officer. The old code stops working the moment the token is reset.
- The token is also stored in the plugin's config file (`%AppData%\XIVLauncher\pluginConfigs\GilgameshBot.json`). Do not share that file; if it leaks, reset the token as above.
- Invite the bot with **View Channels** and **Send Messages** only, and do not grant it more later.
- The plugin never logs the token. Discord.Net's own log lines are forwarded to Dalamud's log (warnings and errors at their level, the rest at debug).
- Everything relayed is visible to everyone who can read the Discord channel. Agree with your FC on what gets relayed, and consider making the channel private to FC members.

## Troubleshooting

- **"Discord is not configured: the bot token is missing."** — save a token on the Discord tab, or import a setup code.
- **"No branch configured for Kraken Company «KRKN» @ Behemoth."** — this character's Free Company has no row in the branch table. Add one on the Discord tab (**Use my character** fills in the home world, FC name and FC tag), or ask the officer who set the bot up for an updated setup code. If a row *looks* right, check its **FC name** matches the FC exactly — the name, not the tag, is what is matched.
- **"This character is not in a Free Company, so there is nothing to relay."** — the plugin waited 30 seconds after login and never saw a Free Company name. Expected on a character with no FC; otherwise `/gilgamesh connect` tries again.
- **"This is not a GilgameshBot setup code."** — what was copied is not a setup code (it must start with `GB2:`). Copy the whole line your officer sent, nothing else.
- **"The setup code is damaged or incomplete."** — the code was cut short, wrapped or auto-corrected on the way. Ask for it again in a private message and copy it in one go.
- **"Bot is not a member of server …"** — the invite step was skipped, or the server ID is wrong.
- **"Channel … not found"** — wrong channel ID, or the bot lacks *View Channel* on it.
- **"State channel … not found"** — wrong state channel ID, or the bot lacks *View Channel* / *Read Message History* on it. The plugin does not connect until it can see that channel.
- **"The bot cannot read #state-channel"** / connected but on standby with no leader — the bot is missing **Read Message History** on the state channel. Discord answers an empty list instead of an error in that case, so the plugin cannot see its own presence message. Grant the permission (channel → Edit → Permissions → the bot's role), then delete any leftover presence messages in that channel.
- **Two officers, messages still duplicated** — both must have the **same** state channel ID on that branch, and must reconnect after saving it. `/gilgamesh status` says which one is relaying.
- **One branch never relays while another does** — the two branches are sharing a state channel. Give each its own; otherwise their instances queue against each other and only one Free Company gets relayed.
- **Connected but nothing arrives** — confirm the message really went to the Free Company channel (`/fc`), and that *Relay Free Company chat* is on. `/xllog` shows the plugin's log.
- **Mentions don't resolve** — the name must match the username or display name exactly (roles must be mentionable); check the exact handle in the member list.

## For contributors

Only needed if you want to build the plugin yourself; officers install it from the custom repository above.

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build GilgameshBot/GilgameshBot.csproj -c Release
```

Dalamud's reference assemblies are picked up automatically from `%AppData%\XIVLauncher\addon\Hooks\dev\` (created the first time XIVLauncher runs with Dalamud). On another machine or in CI, point the `DALAMUD_HOME` environment variable to an extracted copy of `https://goatcorp.github.io/dalamud-distrib/latest.zip`.

Output: `GilgameshBot/bin/Release/GilgameshBot.dll`, plus `GilgameshBot/bin/Release/GilgameshBot/latest.zip` and `GilgameshBot.json` produced by DalamudPackager.

### Load your build as a dev plugin

1. In game: `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the full path to `GilgameshBot.dll` from the build output → **Save and Close**.
2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **GilgameshBot**.
3. After each rebuild, reload it from the same list (or use the Dalamud dev auto-reload option).

### Releases

Every merge into `release` runs `.github/workflows/release.yml`, which stamps the version, builds the plugin and attaches three assets to the GitHub Release: the versioned zip, `latest.zip` and `repo.json`. The custom repository URL is the `releases/latest/download/repo.json` redirect, so it always points at the newest release without anything being committed back to the protected branch. The icon shown in the plugin installer comes from `GilgameshBot/images/icon.png` — the build copies it next to `latest.zip` (DalamudPackager keeps images out of the zip on purpose) and `repo.json` points at it through `IconUrl`; Dalamud wants a square PNG of at most 512×512. See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## A word on third-party tools

Square Enix's terms of service do not allow third-party tools. Dalamud plugins are widely used and tolerated in practice, but the risk is carried by the account running them. Keep the usual etiquette: don't mention plugins in game or on official channels.

GilgameshBot is an independent implementation. It was informed by prior FFXIV↔Discord plugins as references — [reiichi001/Dalamud.DiscordBridge](https://github.com/reiichi001/Dalamud.DiscordBridge), [Valiice/DiscordChatWebhook](https://github.com/Valiice/DiscordChatWebhook), [ViMaSter/FFXIVDiscordChatBridge](https://github.com/ViMaSter/FFXIVDiscordChatBridge) and [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin) — but does not incorporate their code.

## License

Copyright (C) 2026 Marco André Innocenti

GilgameshBot is free software, licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0-only)** — see [`LICENSE`](LICENSE) for the full text. In short: you may use, study, modify and redistribute it, but any modified version you distribute *or make available to users over a network* must also be offered under the AGPL-3.0 with its source (see section 13 of the license). It comes with no warranty.
