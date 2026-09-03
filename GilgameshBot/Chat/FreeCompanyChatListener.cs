using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using GilgameshBot.Relay;

namespace GilgameshBot.Chat;

/// <summary>
/// Listens to the game's chat log and hands Free Company messages to the Discord bridge.
/// </summary>
/// <remarks>
/// <see cref="IChatGui.ChatMessage"/> fires on the game's main thread, so this class must
/// never block or await: it only extracts text and enqueues. Everything Discord-related
/// happens on background threads inside <see cref="DiscordBridge"/>.
/// </remarks>
public sealed class FreeCompanyChatListener : IDisposable
{
    private readonly IChatGui chatGui;
    private readonly IPlayerState playerState;
    private readonly Configuration config;
    private readonly DiscordBridge bridge;
    private readonly IPluginLog log;

    public FreeCompanyChatListener(
        IChatGui chatGui,
        IPlayerState playerState,
        Configuration config,
        DiscordBridge bridge,
        IPluginLog log)
    {
        this.chatGui = chatGui;
        this.playerState = playerState;
        this.config = config;
        this.bridge = bridge;
        this.log = log;

        this.chatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (message.LogKind != XivChatType.FreeCompany)
                return;

            if (!config.RelayEnabled)
                return;

            var senderName = ExtractSenderName(message.Sender);
            var text = message.Message.TextValue?.Trim();

            if (string.IsNullOrEmpty(senderName) || string.IsNullOrEmpty(text))
                return;

            var isOwnMessage = playerState.IsLoaded
                               && string.Equals(senderName, playerState.CharacterName, StringComparison.Ordinal);

            if (isOwnMessage && !config.RelayOwnMessages)
                return;

            bridge.Enqueue(new OutboundMessage(senderName, text, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the game's chat pipeline.
            log.Error(ex, "Failed to process a Free Company chat message.");
        }
    }

    /// <summary>
    /// The sender of an FC message usually carries a <see cref="PlayerPayload"/> with the clean
    /// character name. Messages from the local player may only carry raw text, so fall back to it.
    /// </summary>
    private static string ExtractSenderName(SeString sender)
    {
        var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is not null && !string.IsNullOrWhiteSpace(player.PlayerName))
            return player.PlayerName;

        return sender.TextValue.Trim();
    }
}
