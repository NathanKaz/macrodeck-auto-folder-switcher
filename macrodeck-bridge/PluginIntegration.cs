using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace MacroDeck.Bridge;

/// <summary>
/// The plugin's single integration. It exposes no deck actions or other capabilities: its whole job
/// is to hold the <see cref="IIntegrationContext"/> for the loopback REST surface in
/// <see cref="DeckBridge"/> and clear it when the Macro Deck session goes away.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration
{
	private readonly DeckBridge _bridge;
	private readonly ILogger _logger;

	public PluginIntegration(DeckBridge bridge, ILogger logger)
	{
		_bridge = bridge;
		_logger = logger.ForContext<PluginIntegration>();
		Actions = [];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public Task InitializeAsync(IIntegrationContext context)
	{
		// Runs after every established session, so it is safe to call repeatedly.
		_logger.Information("Bridge integration initialized.");
		_bridge.Attach(context);
		return Task.CompletedTask;
	}

	public Task ShutdownAsync()
	{
		_bridge.Detach();
		return Task.CompletedTask;
	}
}