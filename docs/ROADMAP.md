# GilgameshBot — Roadmap and design decisions

This document is the long-lived plan. The README describes only the current phase and how to run it; when a phase ships, the README changes and this file gets a status update.

## Vision

A Free Company (FC) spread across Discord and the game should feel like one conversation. FC officers run the plugin; while any of them is in the game, FC chat is mirrored to Discord, and eventually Discord members can talk back into the game through a relay character. Nothing runs 24/7 on a server: if no officer is online there is no FC chat to relay, so the bot simply goes offline.

## Guiding principles

1. **No hosted infrastructure required.** The Discord bot runs *inside* the Dalamud plugin, on the officer's PC. A relay server is an optional future add-on, not a prerequisite.
2. **Ship the read path first.** Reading game chat is passive and uses Dalamud's public API. Writing into game chat is automation-adjacent, fragile across game patches and needs real care; it comes last.
3. **Bot presence is the source of truth.** Discord shows the bot online only while at least one plugin instance holds a gateway session. That signal survives crashes, unlike a posted "Offline" message.
4. **Small, testable phases.** Each phase is usable on its own and does not throw away the previous one.
5. **README = present, ROADMAP = future.** Keep operational instructions phase-specific and current.

## Phases

### Phase 1 — POC: FFXIV → Discord *(shipped, v0.1.x)*

Scope

- One FC, one Discord channel, one officer online at a time.
- Dalamud plugin connects to the Discord gateway as **GilgameshBot** using the bot token.
- FC chat lines relayed as `**Character**: message`, markdown escaped.
- `@name` in game resolved to a Discord user or mentionable role via the guild member search endpoint (no privileged intents). Sends use an allow-list of exactly the resolved IDs; `@everyone`/`@here` never resolved.
- `GilgameshBot Online!` on connect, `GilgameshBot Offline.` on clean disconnect.
- Connect on character login, disconnect on logout; manual `/gilgamesh connect|disconnect|status`.
- Outbound queue with a small delay between messages.

Done when

- An officer can build, install as a dev plugin, configure, and see FC chat appear in the channel with working mentions.

Known gaps (by design, addressed in Phase 2)

- Two officers online → duplicated messages.
- Token stored in plain text in the plugin config.

### Phase 2 — Multiple officers, one FC *(current)*

Goal: any number of officers can run the plugin without duplicates, and Online/Offline reflects "someone is relaying" vs. "nobody is".

Approach (no server): **a standby queue built from per-instance presence messages in Discord.**

- Each running instance posts **its own** presence message in an optional hidden/admin *state channel*: `🎮 <Character @ World> · beat <n>`. No shared message, so there is nothing to write-race on.
- **Ordering is the message id.** Discord snowflakes are assigned by the server and are monotonic, so the oldest presence message is the head of the queue. Local clocks are never used for ordering.
- **Heartbeat**: every `HeartbeatSeconds` (default 30) an instance edits its own message, bumping the beat counter so `edited_timestamp` moves.
- A **failed heartbeat forfeits the slot**: the instance deletes its presence message and posts a new one, which lands at the back of the queue. Retrying the edit instead would let an instance that went quiet long enough for a peer to promote itself return straight to the head of the queue (its snowflake is still the oldest) and relay alongside that peer until the peer's next tick.
- **Alive** = the message's last edit is younger than `StaleSeconds` (default 90) relative to *server* time, where server time is the newest edit timestamp seen in the channel. Comparing server timestamps against each other means clock skew between officers' PCs is irrelevant.
- **Leader** = the oldest alive presence message. Leadership is a pure function of the channel contents, recomputed on every tick by every instance — there is no claim protocol and no state machine.
- **Lease**: an instance relays only while it is leader *and* its own last heartbeat succeeded within `StaleSeconds` (measured with a monotonic local tick count). A leader that loses its connection stands itself down before any peer can consider it stale, so a takeover can never produce duplicates. The cost is a relay gap of up to ~`StaleSeconds` after a crash; that is accepted.
- **Followers drop, never buffer.** A message received while on standby is discarded at enqueue time, and leadership is re-checked again just before sending. Buffering would replay lines the outgoing leader already relayed.
- **Stale cleanup**: presence messages untouched for more than 10 minutes are debris from crashed instances and may be deleted by anyone. Otherwise an instance only ever edits or deletes *its own* message.
- **Announcements**: `🟢 Online … via <label>` on a false → true leadership change (first login, clean handoff, takeover after a crash). On clean shutdown the leader deletes its presence message first, then posts `🔴 Offline` only if no other alive instance remains; if a peer is alive it stays quiet and the peer announces itself on its next tick. Followers never announce. Getting our own slot back after a forfeit (nobody else led in between) is not a change of relaying officer and is not announced, so a flaky connection does not spam the channel.
- `StateChannelId = 0` disables all of the above and keeps Phase 1 behaviour exactly (this instance always relays).

Alternatives considered

- *One shared state message with a claim protocol* (the original plan: `leader=…, heartbeat=…, session=<random id>`; a follower claims by editing it with its own id, waits, re-reads and only relays if its id survived) — dropped. Edits to a shared message are last-write-wins, so two concurrent claims genuinely race and the "write, wait, re-read" dance only narrows the window instead of closing it. It is also more code and more states than a leader computed as a pure function over per-instance messages, which cannot race because every instance writes only its own message.
- *Content-hash dedup on the Discord side* — impossible without a server; each plugin would race to post.
- *Relay server* — cleaner (dedup, heartbeats, token stays server-side) but needs hosting. Deferred; see "Optional: relay server" below.

Resolved questions

- *Handoff latency vs. heartbeat cost*: 30 s heartbeat / 90 s stale. Edits are one REST call per instance per 30 s in a single channel, far below Discord's per-channel edit limits; both are configurable and clamped (heartbeat 10–120 s, stale ≥ 2× heartbeat and ≤ 600 s).
- *A message received during a leader change*: it is dropped. The lease guarantees the outgoing leader stopped relaying before the incoming one starts, so a short gap replaces the duplicate. Buffering plus dedup on sender+text+minute was rejected as more machinery for a worse failure mode (a burst of late duplicates).

### Phase 3 — Multiple FC branches

Goal: one bot, one plugin, N FC branches (e.g. Kraken and Famfrit), each with its own channel.

- The plugin reads the character's **home world** and **FC tag** and looks them up in a branch table in the config: `(world, fc) → channel id, state message id`.
- Leader election, Online/Offline and mention resolution are all scoped per branch.
- An officer with characters in two branches needs no extra setup: the branch is picked from the logged-in character.
- Adding a branch = adding a row to the table (later: `/gilgamesh branch add`).

### Phase 4 — Discord → FFXIV ("Discord Lala")

Goal: Discord members can talk into FC chat through a dedicated relay character.

Format: in game, `Discord Lala: [Discord] Member: message`.

Requirements and safeguards

- Sending chat is **not** in Dalamud's public API. Use a maintained hook library (ECommons `Chat.SendMessage` or equivalent) and expect breakage after game patches.
- The relay character must be an FC member with chat permission, logged in with the plugin. Prefer a dedicated account over an officer's main so relayed lines don't look like the officer speaking.
- **Sanitise everything** coming from Discord: strip leading `/`, strip newlines (each line would become a command), cap length (~500 chars in game), collapse whitespace, convert `<@id>` mentions to names, `<:emoji:id>` to `:emoji:`, attachments to `[image]`/links.
- **Echo prevention**: never relay back to Discord the lines the relay character itself sent; never relay to game the bot's own Discord messages.
- **Rate limiting**: queue with ≥1–2 s between sends; the game has anti-spam on chat.
- Needs the `MESSAGE_CONTENT` privileged intent on the bot.
- Opt-in per branch; off by default.

### Optional: relay server (any phase)

If hosting ever becomes available, a small relay service replaces the Discord-side coordination:

- Plugins POST messages + heartbeats with a per-officer key; the server dedupes, tracks who is online, and is the only holder of the bot token.
- Fits Cloudflare Workers' free tier (HTTP endpoint + KV + cron) — no VM required.
- The plugin keeps the same chat capture and formatting; only the transport changes.

## Decision log

| # | Decision | Why |
|---|---|---|
| 1 | Dalamud plugin (C#, .NET 10, Dalamud API 15) | Only practical way to read FC chat; no official chat API exists. |
| 2 | Bot runs inside the plugin, no server | No hosting available; bot only has work while an officer is online anyway. |
| 3 | Gateway bot instead of webhook | Mentions need member lookup (bot token); gateway gives presence-based Online/Offline for free. |
| 4 | Discord.Net 3.20.x | Mature, .NET 10 support, used by the existing DiscordBridge plugin without workarounds. |
| 5 | Online/Offline messages *and* presence | Messages for humans reading the channel; presence for the crash case. |
| 6 | Read path first, write path last | Sending chat is the fragile, ToS-sensitive part; validate everything else first. |
| 7 | One branch at a time | Keep the POC small; branch table comes in Phase 3. |
| 8 | Markdown escaped, mass mentions blocked | Players must not be able to format or ping the whole server from game chat. |
| 9 | Docs split: README (now) / ROADMAP (later) | Keeps setup instructions accurate for the shipped phase. |
| 10 | One presence message *per instance* instead of one shared state message | Each instance only ever writes its own message, so concurrent claims cannot race; leadership becomes a pure function of the channel (oldest alive snowflake wins) instead of a claim state machine. |
| 11 | Lease: a leader stops relaying as soon as it cannot heartbeat | Guarantees the old leader is silent before a peer takes over. Trades a relay gap of up to ~StaleSeconds after a crash for never duplicating a line. |
| 12 | Followers drop messages, never buffer them | On a clean handoff the previous leader already relayed them; replaying a backlog on promotion would duplicate exactly the lines a standby queue exists to avoid. |

## Reference projects

- [reiichi001/Dalamud.DiscordBridge](https://github.com/reiichi001/Dalamud.DiscordBridge) — one-way FFXIV → Discord relay (its README notes it does not relay Discord → game); Discord.Net inside a Dalamud plugin. Used here only as a reference (it is AGPL-3.0; no code was copied).
- [Valiice/DiscordChatWebhook](https://github.com/Valiice/DiscordChatWebhook) — minimal webhook relay.
- [ViMaSter/FFXIVDiscordChatBridge](https://github.com/ViMaSter/FFXIVDiscordChatBridge) — another variation.
- [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin) — current plugin template and API usage.
