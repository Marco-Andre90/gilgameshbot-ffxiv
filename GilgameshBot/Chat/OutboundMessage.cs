namespace GilgameshBot.Chat;

/// <summary>What a relayed line is, which decides how it is formatted on Discord.</summary>
public enum OutboundKind
{
    /// <summary>A member's Free Company chat line: <c>**Sender**: text</c>, mentions resolved.</summary>
    Chat,

    /// <summary>The game's "X has logged in / out" notice for a Free Company member.</summary>
    LoginLogout,

    /// <summary>A Free Company announcement: a member joined, left, was removed, changed rank…</summary>
    Announcement,
}

/// <summary>A Free Company line captured from the game, ready to be relayed.</summary>
/// <param name="SenderName">Character name as shown in game (no world suffix); empty for notices.</param>
/// <param name="Text">Plain-text body of the message.</param>
/// <param name="ReceivedAt">Local time the game delivered the message.</param>
/// <param name="Kind">Chat line or game notice.</param>
public sealed record OutboundMessage(
    string SenderName,
    string Text,
    DateTimeOffset ReceivedAt,
    OutboundKind Kind = OutboundKind.Chat);
