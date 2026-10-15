namespace PiSharp.Runtime.Providers;

/// <summary>Namespaced request metadata that is safe to carry through shared chat middleware.</summary>
public static class ProviderSessionMetadata
{
    /// <summary>The stable application conversation ID used for provider session affinity.</summary>
    public const string ConversationSessionIdProperty = "pisharp.conversationSessionId";
}
