# GilgameshBot

A [Dalamud](https://dalamud.dev) plugin for Final Fantasy XIV that relays **Free Company chat to a Discord channel**. Named after the dimension-hopping Gilgamesh: while a member running the plugin is in the game, the FC's conversation jumps over to Discord.

One bot serves any number of Free Company branches (one per home world + Free Company name), each with its own Discord server and channels. The plugin picks the branch from the character you log in as. Any number of members can run it: exactly one of them relays and the rest wait in a standby queue. Install it from the plugin installer and configure it by pasting one setup code.

## What it does

- While your character is logged in, the plugin connects to Discord as **GilgameshBot** and posts every Free Company chat line to the configured channel as `**Character Name**: message`.
- It also relays the game's FC announcements, worded as your game client shows them, such as a member joining, leaving or being removed (`📣 …`); turn this off with *Relay Free Company announcements*. Members logging in and out (`🔔 Name has logged in.`) can be relayed too, but that is off by default: turn it on with *Relay member logins and logouts*.
- **The Free Company branch is picked from the character you log in as.** The plugin reads your home world and Free Company name and looks them up in a branch table; each branch has its own Discord server and relay channel, and the branches on a server share one hidden state channel. A member with characters in two branches needs no extra setup — log in as the Kraken character and Kraken's channel gets the chat, log in as the Famfrit one and Famfrit's does.
- `@name` typed in game becomes a real Discord mention (username, display name or role). `@everyone` and `@here` are never relayed as mentions.
- The channel gets **"GilgameshBot Online!"** when relaying starts and **"GilgameshBot Offline."** (naming who stopped) when the last member stops relaying cleanly (logout, `/gilgamesh disconnect`, plugin unload). If the game crashes, no message is posted, but the bot's presence in the member list goes offline on its own, so members can still tell whether chat is being relayed.
- **One setup code configures everybody else.** The member who created the bot exports a single string carrying the token and *every* branch; every other member imports it and is ready to relay on any of their characters, without ever touching a Discord ID.
- **Configuration changes reach everybody on their own.** The branch table (without the token) is published to Discord, and every member's plugin takes over a newer version within a few minutes. See [Changing the configuration later](#changing-the-configuration-later).
- **Several members can run the plugin at the same time** without duplicating anything. The plugin needs a *state channel* for this. Each running plugin keeps one presence message there; the one that has been running longest relays, the others sit on standby and show their place in the queue. When the relaying member logs out, the next in line takes over — cleanly and immediately, or within about 20 seconds if the relaying member's game closed unexpectedly, once that instance's lease expires. Messages received while on standby are dropped, never replayed, so nothing is ever posted twice.

## Requirements

- Final Fantasy XIV launched through [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled.
- For the member who sets the bot up: a Discord server where you can create channels and invite bots.

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

A member in your FC has already set the bot up and sent you one long line of text — as a Discord DM from **GilgameshBot**, or by private message. (If your role is allowed to, you can also ask the bot for one yourself: type `/setupcode` in the FC's Discord server while a member is in game, see [Discord commands](#discord-commands).) That is all you need:

1. Copy the code.
2. In game: `/gilgamesh` → **Import from clipboard** (or just type `/gilgamesh import`, or the short form `/gilga import`).

If it arrived as a DM from the bot, that message is replaced with a receipt the first time the plugin connects, and the code is gone from your inbox. A DM you never import is deleted automatically (24 hours after `/gilga send`, 5 minutes after `/setupcode`), so ask for a new one if you left it too long.

That's it — the plugin looks up your character's branch and connects right away. The token and every branch (server, relay channel, state channel) are filled in for you. The other options (auto-connect, whether your own lines are relayed, …) stay personal to you.

### Setting up the bot for your FC (once)

Do the [Discord setup](#discord-setup-once-per-branch) below (once per branch), then:

1. `/gilgamesh` opens the settings window (also reachable from the plugin installer's settings button).
2. On the **Discord** tab, paste the **bot token** → **Save token**. One bot serves every branch.
3. Still on the Discord tab, fill in the branch editor and click **Add branch**:
   - **Name** — a label for you, e.g. `Kraken`.
   - **Home world**, **FC name** and **FC tag** — click **Use my character** while logged in on a character in that FC and all three are filled in for you. The **home world + FC name** pair is what a character is matched on: FC names are unique on a world, FC tags are not (two Free Companies on one world may share a tag), so the tag is only a label and an extra check. The FC name is the full name spelled out on your Free Company profile; the tag is what appears in « » next to your name, typed without the brackets.
   - **Server ID**, **Channel ID**, **State channel ID** — from the Discord setup below.
4. Repeat step 3 for every other branch. All branches on the same Discord server can share one state channel.
5. Click **Connect** (or log out and back in with *Connect automatically* enabled).
6. On the **Status** tab, click **Publish to Discord** and confirm. This makes your branch table the [shared configuration](#changing-the-configuration-later) every member's plugin follows.
7. Hand the setup code to your fellow members. They import it and are done, on every one of their characters.

### Changing the configuration later

The branch table (every branch's server, channels and roster settings) and the presence timers form the **shared configuration**. It is kept in Discord, in a bot message in each branch's state channel, with a `gilgamesh-config.json` file attached. The **bot token is not part of it**: the token only ever travels in a setup code.

- **To change it:** edit the branches (Discord tab) or roster settings (FC roster tab), then click **Publish to Discord** on the Status tab and confirm. Anyone whose plugin is connected can publish, so agree in your FC on who does. The Status tab shows which revision your plugin follows and who published it.
- **Everybody else does nothing.** Each plugin checks on connect and every 5 minutes. When it finds a newer revision, it replaces its own branch table and timers with it, says so in game chat, and reconnects if its own branch moved. Personal options (auto-connect, relaying your own lines, …) are never touched. Local branch edits that were not published are overwritten by the next newer revision.
- **Setup codes are always current.** A code fetched with `/setupcode` or sent with `/gilga send` comes from a plugin that follows the shared configuration, and carries its revision.
- **Publishing is refused** if any branch is incomplete, if the bot cannot see a branch's server, relay, state or roster channel, if it lacks *Attach Files* in a state channel, or if Discord already holds a newer revision than yours. In that last case, wait for your plugin to take it over, redo your change on top of it and publish again.
- **A new bot token still needs new setup codes.** Put the new token in first and connect, so that your plugin is the one relaying. Every other plugin then fails to connect with the old token, and its member fetches a fresh code with `/setupcode`.

   **Recommended — send it by Discord DM.** While connected, type `/gilga send <discord username>` in game (or use **Send by DM** on the Status tab). The bot sends that member a direct message containing the code; they copy the line and type `/gilga import`. Nothing ever passes through your own chat window or clipboard.

   The DM looks after itself:
   - **On import**, their plugin replaces the message with a receipt — `✅ Setup code imported by …` — so the code stops sitting in their inbox as soon as it has been used.
   - **After 24 hours**, if they never imported it, your plugin deletes the message. `/gilga revoke` does the same immediately, for every code you sent.
   - The Status tab lists the codes you sent that are still out there, with a **Revoke all** button.

   **Alternative — the clipboard.** On the **Status** tab, click **Export setup code** — the code is copied to your clipboard — and send it to your fellow members **by private message**. This code carries no receipt: it neither expires nor can be revoked, so prefer the DM path when you can.

The channel should receive `🟢 GilgameshBot Online! Relaying Free Company chat via <Character> @ <World>. v<version>`, and the Status tab shows `Branch: Kraken («KRKN» Kraken Company @ Behemoth)`.

Members with characters in more than one branch need nothing extra: one setup code covers all of them, and the plugin switches branch when they switch character.

## Discord setup (once per branch)

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) → **New Application** → name it `GilgameshBot`.
2. **Bot** tab → **Reset Token** → copy the token. You will paste it into the plugin. No privileged intents are needed — the bot reads only its own presence messages, over REST.
3. **OAuth2 → URL Generator**: scopes `bot` **and** `applications.commands`; permissions **View Channels**, **Send Messages**, **Read Message History** and **Attach Files** (the shared configuration and the FC roster are kept as files), plus optionally **Pin Messages** if you use the [FC roster](#fc-roster). Do **not** grant *Mention Everyone*: the plugin never mass-pings, and only roles marked *Allow anyone to @mention this role* can be mentioned from the game. Open the generated URL and invite the bot to your server.

   *Already invited the bot before the slash commands existed?* Open the generated URL again with both scopes ticked and authorise it for the same server. It adds the `applications.commands` scope; no new permissions are requested.
4. In Discord, enable **Settings → Advanced → Developer Mode**, then right-click the server → **Copy Server ID**, and right-click the target channel → **Copy Channel ID**.
5. Make sure the bot can see and post in that channel (check the channel's permission overrides).
6. Create a **state channel**: a private text channel that no one needs to read — for example `#gilgamesh-state`, visible only to the people who run the plugin. It must be a **separate channel from the relay channel**, and nobody should post in it; one such channel is enough for every branch on that server: the plugin decides who relays from the messages it keeps there, and any other traffic breaks that. Give the bot **View Channel**, **Send Messages**, **Read Message History** and **Attach Files** on it (the last one for the [shared configuration](#changing-the-configuration-later)). *Manage Messages* is **not** required: the plugin only ever edits and deletes messages the bot itself posted. Copy its ID the same way and paste it into **State channel ID** in the branch editor. Every member relaying a given branch must point at the **same** state channel — and every branch needs its **own**.

7. Decide who may fetch a setup code with `/setupcode`. Out of the box the command is only visible to members with **Manage Server**. To open it to a role: **Server Settings → Integrations → GilgameshBot → Command permissions**, remove `@everyone` if present and add the roles that may run `/setupcode`. Discord alone decides who can use it; nothing needs to change in the plugin.

Repeat steps 3–6 for each branch: invite the same bot to that branch's Discord server, then copy its server, channel and state channel IDs (branches on the same server reuse the same state channel). Creating the application and its token (steps 1–2) happens only once.

The state channel fills up with one short message per running plugin (`🎮 [relay channel id] Character @ World · beat 42`), which the plugins keep updating and clean up after themselves. The id tells the branches apart when they share the channel. Once the configuration is published, the channel also holds one `⚙️ GilgameshBot shared configuration` message from the bot. Do not delete it: plugins would stop receiving changes until the next publish.

## Commands

| Command | Effect |
|---|---|
| `/gilgamesh` | Open settings / status |
| `/gilgamesh connect` | Connect to Discord now |
| `/gilgamesh disconnect` | Post the Offline notice and disconnect |
| `/gilgamesh status` | Print connection state, the active branch (or why none matched), relayed-message count, and whether this instance is relaying or on standby |
| `/gilgamesh import` | Import a setup code from the clipboard |
| `/gilgamesh send <discord name>` | DM the setup code to that member of the branch's Discord server (username, display name or nickname, spaces allowed). Needs an active connection |
| `/gilgamesh revoke` | Delete every setup code DM you sent that has not been imported |
| `/gilga` | Short for `/gilgamesh` — works with every subcommand above (`/gilga import`, `/gilga status`, …) |

## Discord commands

The bot answers three slash commands in each branch's Discord server.

| Command | Effect |
|---|---|
| `/setupcode` | The bot DMs you your own setup code, exactly as `/gilga send` would. The reply in the channel is ephemeral (only you see it) and never contains the code. The DM is deleted after 5 minutes if you don't import it, and replaced with a receipt as soon as you do |
| `/relaystatus` | Ephemeral reply: which character is relaying this branch right now, how many members are on standby, and the branch name |
| `/fcscan world` | Runs a [roster scan](#fc-roster) of a Free Company: pick it from the list, which shows the ones set up on this server as *FC name @ World*. The report goes to the roster channel; the ephemeral reply says how it went |

Two things to know:

- **They only work while at least one member's plugin is connected.** The bot has no server of its own — it runs inside the plugin. With nobody in game, Discord shows *"The application did not respond"*.
- **`/setupcode` is gated by Discord.** Its command permissions decide who can see and run it: **Manage Server** until the server owner opens it to specific roles under *Server Settings → Integrations → GilgameshBot*. Keep that list short: anyone who can run it gets the bot token.

`/relaystatus` and `/fcscan` carry nothing sensitive, so the plugin does not gate them further: anyone Discord lets run them gets an answer.

## FC roster

A manual scan reads a Free Company's member list from its public Lodestone page and compares it with the previous scan, to keep track of how long new members have been in the rank they join in (for example before promoting them to a trusted rank).

- **Set it up** on the plugin's *FC roster* tab, per Free Company: its **Lodestone ID** (the number in `…/lodestone/freecompany/<ID>/`; *Use my FC* reads it from your character), its **starter rank** as spelled on the Lodestone (the game's default is `Member`), and the **roster channel**. These are part of the [shared configuration](#changing-the-configuration-later) and travel in the setup code like the rest of the branch.
- **Roster channel.** One channel can serve every Free Company on the server, but it must not be any branch's relay or state channel. Create it empty: the bot's first message there is the **state message**, holding one `roster-<lodestone id>.json` per Free Company, and it stays on top. In a channel that already has messages the bot pins it instead, which needs *Pin Messages*. Only the bot edits it; do not delete it, or the next scan starts over. The bot also needs *View Channel*, *Send Messages*, *Attach Files* and *Read Message History* there.
- **Scan** with *Scan now* on that tab or `/fcscan` in Discord. *Scan now* works for any member whose plugin is connected, whatever branch they are on; `/fcscan` is answered by the relaying member of a branch on that Discord server, so someone from one of its branches must be in game. A scan reads about one Lodestone page per second (50 members each).
- **One report per Free Company.** Each scan edits that Free Company's report in place (reposting it only when it needs a different number of messages), deletes any older copy, and posts a short *Roster updated* line with a link to it, replacing the previous one, so the channel shows when the last scan ran. Discord sends no notification for an edit; the new line is what shows up as unread.
- **The report** lists who joined, who left and who changed name since the last scan (members are tracked by Lodestone character ID, so a rename is never a leave + join), and the members who have been in the starter rank for at least the Free Company's **days before promotion** (set on the same tab, 30 by default), longest first, out of how many are in it. The first scan only records the list: members already in the starter rank count from the date of the first scan, shown as *N days+ (before* that date*)*.
- **Limits.** The Lodestone lags the game by a few hours, and a date is the scan that first saw the change, so scan every week or two rather than expecting exact days. If the Lodestone is down, answers with an incomplete list, or changes during the scan, nothing is posted or saved: try again later.

## Options

| Option | Default | Notes |
|---|---|---|
| Connect automatically when a character logs in | on | Disconnects again on logout |
| Relay Free Company chat | on | Master switch; the bot can stay connected without relaying |
| Include my own messages | on | Turn off if you only want *other* members' lines relayed |
| Relay Free Company announcements | on | The game's FC announcements (joined, left, removed, rank changes…), in the game client's language. Never pings anyone |
| Relay member logins and logouts | off | The game's FC login/logout notices, in the game client's language. Never pings anyone |
| Turn `@name` into Discord mentions | on | Exact match on a mentionable role name, then username, then display name |
| Post Online / Offline announcements | on | |
| Delay between messages (ms) | 300 | Spreads out a busy chat; Discord.Net still handles rate-limit retries |
| Free Company branches | at least one | Table on the Discord tab: one row per FC, with a label, the home world and Free Company name it is matched by (plus the FC tag as a label and extra check), and its own Discord server, relay channel and state channel. The plugin picks the row matching the logged-in character |
| Heartbeat (s) | 10 | How often this instance refreshes its presence message. Clamped to 10–120 |
| Stale after (s) | 20 | How long a silent instance keeps its place in the queue, and how long this one keeps relaying without a successful heartbeat. Clamped to at least twice the heartbeat, at most 600 |

## Mentions from the game

Type `@` followed by the person's Discord **username** (the lowercase handle), for example `@marco` or `@marco.andre`. Display names and mentionable role names work too, including ones with spaces: `@Justice Archon` pings that role or member if the name matches exactly (case-insensitive). The plugin tries the longest match first, up to four words, and leaves the rest of the sentence as text. A raw `<@123>` typed in game is shown as text and pings nobody — only mentions the plugin resolved itself are allowed to notify.

## Security notes

- **The setup code contains the bot token**, whether it travels by clipboard or by DM. Treat it like a password: send it by **private message** only, never in a public or FC-wide channel, never in a screenshot, never in a pastebin. The plugin never shows it on screen and never writes it to the log — it only ever passes through your clipboard or through the DM it was sent in.
- **A code sent with `/gilga send` is short-lived by design.** The DM is replaced with a receipt the moment the recipient's plugin connects successfully, and deleted 24 hours after it was sent if they never import it. `/gilga revoke` deletes every outstanding one at once. A code exported to the clipboard has none of that: it lives wherever you pasted it until you reset the token.
- **A code fetched with `/setupcode` belongs to the member who was relaying at the time.** Their plugin is the one that sent the DM, so their plugin is the one that deletes it after 5 minutes if it is not imported, and the only one whose `/gilga revoke` can withdraw it earlier.
- If a setup code (or the token) leaks: **Bot → Reset Token** in the [Developer Portal](https://discord.com/developers/applications), paste the new token in the plugin, **Save token**, then **Export setup code** again and send the new code to every member. The old code stops working the moment the token is reset.
- The token is also stored in the plugin's config file (`%AppData%\XIVLauncher\pluginConfigs\GilgameshBot.json`). Do not share that file; if it leaks, reset the token as above.
- Invite the bot with **View Channels** and **Send Messages** only, and do not grant it more later.
- The plugin never logs the token. Discord.Net's own log lines are forwarded to Dalamud's log (warnings and errors at their level, the rest at debug).
- Everything relayed is visible to everyone who can read the Discord channel. Agree with your FC on what gets relayed, and consider making the channel private to FC members.

## Troubleshooting

- **"Discord is not configured: the bot token is missing."** — save a token on the Discord tab, or import a setup code.
- **"No branch configured for Kraken Company «KRKN» @ Behemoth."** — this character's Free Company has no row in the branch table. Add one on the Discord tab (**Use my character** fills in the home world, FC name and FC tag), or ask the member who set the bot up for an updated setup code. If a row *looks* right, check its **FC name** matches the FC exactly — the name, not the tag, is what is matched.
- **"This character is not in a Free Company, so there is nothing to relay."** — the plugin waited 30 seconds after login and never saw a Free Company name. Expected on a character with no FC; otherwise `/gilgamesh connect` tries again.
- **"The game has not loaded this character's Free Company details yet."** — the plugin sees your FC tag but the game has not filled in the FC name (typical after the plugin was updated or reloaded mid-session). Open the Free Company window once, or log out and back in, then `/gilga connect`.
- **"This is not a GilgameshBot setup code."** — what was copied is not a setup code (it must start with `GB2:`). Copy the whole line your member sent, nothing else.
- **"The setup code is damaged or incomplete."** — the code was cut short, wrapped or auto-corrected on the way. Ask for it again in a private message and copy it in one go.
- **"… does not accept DMs from members of this server."** — that member has direct messages from server members turned off (Discord → Privacy Settings, per server). Ask them to allow it, or hand the code over with **Export setup code** instead.
- **"No member named X in …"** — the name must match a username, display name or nickname in *that branch's* Discord server exactly (case does not matter). Check the exact handle in the member list.
- **"Connect first."** — `/gilga send` and `/gilga revoke` need a live connection: the bot has to ask the server who that member is, and needs to reach the DM to delete it.
- **"Bot is not a member of server …"** — the invite step was skipped, or the server ID is wrong.
- **"Channel … not found"** — wrong channel ID, or the bot lacks *View Channel* on it.
- **"State channel … not found"** — wrong state channel ID, or the bot lacks *View Channel* / *Read Message History* on it. The plugin does not connect until it can see that channel.
- **"Branch ... uses its relay channel as the state channel"** - the branch was saved with the same channel ID in both fields. The state channel must be its own hidden channel: the plugin builds the standby queue from the newest 100 messages there, and chat traffic pushes the presence messages out of that window, which makes members disagree about who is relaying (duplicated lines, or nobody relaying). Create a separate channel, fix the branch, export a new setup code and send it to every member.
- **"The bot cannot read #state-channel"** / connected but on standby with no leader — the bot is missing **Read Message History** on the state channel. Discord answers an empty list instead of an error in that case, so the plugin cannot see its own presence message. Grant the permission (channel → Edit → Permissions → the bot's role), then delete any leftover presence messages in that channel.
- **Two members, messages still duplicated** — both must have the **same** state channel ID on that branch, and must reconnect after saving it. `/gilgamesh status` says which one is relaying.
- **Connected but nothing arrives** — confirm the message really went to the Free Company channel (`/fc`), and that *Relay Free Company chat* is on. `/xllog` shows the plugin's log.
- **"The application did not respond" on `/setupcode`, `/relaystatus` or `/fcscan`** — nobody's plugin is connected to that branch right now, or the members who are connected have not settled on a relaying instance yet (it takes a few seconds after a login). Only the member whose plugin is currently relaying answers the commands. Check the bot's presence in the member list: grey means nobody is in game.
- **The commands don't show up when typing `/`** — either the bot was invited without the `applications.commands` scope (open the OAuth2 URL again with both scopes; the plugin's log says *Missing Access* when registration fails), or the command is not allowed for your roles. Only members with **Manage Server** see it until the server owner adds roles under *Server Settings → Integrations → GilgameshBot → Command permissions*.
- **Mentions don't resolve** — the name must match the username or display name exactly (roles must be mentionable); check the exact handle in the member list.

## For contributors

Only needed if you want to build the plugin yourself; members install it from the custom repository above.

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
