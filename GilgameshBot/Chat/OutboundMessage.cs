namespace GilgameshBot.Chat;

/// <summary>A Free Company chat line captured from the game, ready to be relayed.</summary>
/// <param name="SenderName">Character name as shown in game (no world suffix).</param>
/// <param name="Text">Plain-text body of the message.</param>
/// <param name="ReceivedAt">Local time the game delivered the message.</param>
public sealed record OutboundMessage(string SenderName, string Text, DateTimeOffset ReceivedAt);
