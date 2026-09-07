using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GilgameshBot.Setup;

/// <summary>
/// The shareable half of the configuration, carried between officers as one opaque string.
/// </summary>
/// <remarks>
/// The payload contains the bot token. It only ever travels through the clipboard:
/// never log it, never print it to game chat, never render it on screen.
/// </remarks>
public sealed class SetupPayload
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("guild")] public string Guild { get; set; } = string.Empty;
    [JsonPropertyName("channel")] public string Channel { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("heartbeat")] public int Heartbeat { get; set; } = 30;
    [JsonPropertyName("stale")] public int Stale { get; set; } = 90;

    // Parsed IDs, filled in by TryDecode once validated.
    [JsonIgnore] public ulong GuildId { get; set; }
    [JsonIgnore] public ulong ChannelId { get; set; }
    [JsonIgnore] public ulong StateChannelId { get; set; }
}

/// <summary>
/// Encodes/decodes the "setup code": the one string an officer exports so the rest of the
/// Free Company can be configured by pasting it, without hunting for IDs.
/// </summary>
/// <remarks>
/// Format: <c>GB1:</c> + base64url(UTF-8 JSON). This is encoding, not encryption — the code is
/// as sensitive as the token inside it and must only be shared by private message.
/// </remarks>
public static class SetupCode
{
    /// <summary>Marker + format version. Case-sensitive; a future format bumps the digit.</summary>
    public const string Prefix = "GB1:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Builds the setup code from the shareable settings. Never log the result.</summary>
    public static string Encode(Configuration config)
    {
        var heartbeat = Math.Clamp(config.HeartbeatSeconds, 10, 120);

        var payload = new SetupPayload
        {
            Version = 1,
            Token = config.BotToken,
            Guild = config.GuildId.ToString(),
            Channel = config.ChannelId.ToString(),
            State = config.StateChannelId.ToString(),
            Heartbeat = heartbeat,
            Stale = Math.Clamp(config.StaleSeconds, heartbeat * 2, 600),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return Prefix + ToBase64Url(json);
    }

    /// <summary>
    /// Parses a setup code. On failure <paramref name="error"/> holds a message meant for the
    /// settings window; it never contains any part of the code.
    /// </summary>
    public static bool TryDecode(string? code, [NotNullWhen(true)] out SetupPayload? payload, out string error)
    {
        payload = null;
        error = string.Empty;

        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "Paste a setup code first.";
            return false;
        }

        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "This is not a GilgameshBot setup code.";
            return false;
        }

        SetupPayload? parsed;
        try
        {
            var bytes = FromBase64Url(trimmed[Prefix.Length..]);
            parsed = JsonSerializer.Deserialize<SetupPayload>(bytes, JsonOptions);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the exception message can quote the payload.
            error = "The setup code is damaged or incomplete.";
            return false;
        }

        if (parsed is null)
        {
            error = "The setup code is damaged or incomplete.";
            return false;
        }

        if (parsed.Version != 1)
        {
            error = "This setup code was made by a newer version of GilgameshBot. Update the plugin.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Token))
        {
            error = "The setup code has no bot token in it.";
            return false;
        }

        if (!ulong.TryParse(parsed.Guild, out var guildId) || guildId == 0)
        {
            error = "The setup code has no valid server ID in it.";
            return false;
        }

        if (!ulong.TryParse(parsed.Channel, out var channelId) || channelId == 0)
        {
            error = "The setup code has no valid channel ID in it.";
            return false;
        }

        if (!ulong.TryParse(parsed.State, out var stateChannelId) || stateChannelId == 0)
        {
            error = "The setup code has no valid state channel ID in it.";
            return false;
        }

        parsed.Token = parsed.Token.Trim();
        parsed.GuildId = guildId;
        parsed.ChannelId = channelId;
        parsed.StateChannelId = stateChannelId;

        // Same clamps as the settings window, applied in the same order: the heartbeat first,
        // because the lower bound of the stale window is twice the *clamped* heartbeat.
        parsed.Heartbeat = Math.Clamp(parsed.Heartbeat, 10, 120);
        parsed.Stale = Math.Clamp(parsed.Stale, parsed.Heartbeat * 2, 600);

        payload = parsed;
        return true;
    }

    /// <summary>Writes the shareable settings into the config and persists it.</summary>
    public static void Apply(SetupPayload payload, Configuration config)
    {
        config.BotToken = payload.Token;
        config.GuildId = payload.GuildId;
        config.ChannelId = payload.ChannelId;
        config.StateChannelId = payload.StateChannelId;
        config.HeartbeatSeconds = payload.Heartbeat;
        config.StaleSeconds = payload.Stale;
        config.Save();
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length."),
        };

        return Convert.FromBase64String(padded);
    }
}
