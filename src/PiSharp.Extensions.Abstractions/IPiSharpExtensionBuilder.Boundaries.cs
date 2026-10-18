namespace PiSharp.Extensions.Abstractions;

public partial interface IPiSharpExtensionBuilder
{
    /// <summary>Registers a handler at each finalized model-turn boundary.</summary>
    void AddTurnEndBoundaryHandler(PiSharpExtensionTurnEndBoundaryHandler handler);

    /// <summary>Registers a handler before a logical agent run settles.</summary>
    void AddAgentBeforeSettleHandler(PiSharpExtensionAgentBeforeSettleHandler handler);

    /// <summary>Registers an observer after a logical agent run fully settles.</summary>
    void AddAgentSettledHandler(PiSharpExtensionAgentSettledHandler handler);
}
