using MacroDeck.Bridge;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;

// Identity, description and icon come from manifest.json at the content root.
var plugin = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.ConfigureServices((ctx, services) => services.AddSingleton<DeckBridge>())
	.Configure((ctx, app) => BridgeEndpoints.Map((WebApplication)app))
	.RegisterIntegration<PluginIntegration>()
	.Build();

var app = plugin.WebApplication;

// The plugin's own listener defaults to loopback with an ephemeral port (the host/supervisor owns
// the URL in managed mode, so nothing here must set UseUrls). Publish the address on which the
// custom REST endpoints actually answered - via IServerAddressesFeature, never by guessing URLs -
// so the Python watcher can find us without polluting the plugin config.
app.Lifetime.ApplicationStarted.Register(() =>
{
	var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
	var address = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
		?.Addresses.FirstOrDefault(a => a.StartsWith("http://127.0.0.1", StringComparison.Ordinal))
		?? server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
		?.Addresses.FirstOrDefault();

	var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
		?? Path.GetTempPath();
	var file = Environment.GetEnvironmentVariable("MACRO_DECK_BRIDGE_URL_FILE")
		?? Path.Combine(runtimeDir, "macrodeck-bridge.url");

	try
	{
		File.WriteAllText(file, address ?? string.Empty);
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"bridge: failed to write {file}: {ex.Message}");
	}
});

await plugin.RunAsync();