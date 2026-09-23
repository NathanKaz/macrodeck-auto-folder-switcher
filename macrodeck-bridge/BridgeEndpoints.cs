using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MacroDeck.Bridge;

/// <summary>
/// Loopback-only REST surface the Python watcher talks to. Everything lives outside the reserved
/// <c>/_macrodeck/*</c> namespace. All endpoints except /health bail with 503 until a Macro Deck
/// session exists. When the environment variable <c>MACRO_DECK_BRIDGE_TOKEN</c> is set, every route
/// but /health additionally requires an <c>X-Bridge-Token</c> header.
/// </summary>
public static class BridgeEndpoints
{
	public static void Map(WebApplication app)
	{
		var token = app.Configuration["MACRO_DECK_BRIDGE_TOKEN"];

		app.Use(async (context, next) =>
		{
			var secured = !string.IsNullOrEmpty(token)
				&& !context.Request.Path.StartsWithSegments("/_macrodeck");
			if (secured)
			{
				var provided = context.Request.Headers["X-Bridge-Token"].ToString();
				if (!string.Equals(provided, token, StringComparison.Ordinal))
				{
					context.Response.StatusCode = StatusCodes.Status401Unauthorized;
					await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
					return;
				}
			}
			await next();
		});

		app.MapGet("/health", () => Results.Ok(new
		{
			ok = true,
			ready = app.Services.GetRequiredService<DeckBridge>().IsReady,
			time = DateTimeOffset.UtcNow,
		}));

		app.MapGet("/apps", () =>
		{
			return Results.Ok(new { apps = FocusHints.RunningOptions().Select(a => a.Value) });
		});

		app.MapGet("/rules", async (DeckBridge bridge, CancellationToken ct) =>
		{
			if (bridge.Config is null) return Results.Json(NotReady("rules"), statusCode: 503);
			var rules = await bridge.RulesAsync(ct);
			return Results.Ok(new { rules = rules.Select(RuleDto.From) });
		});

		app.MapGet("/clients", (DeckBridge bridge) =>
		{
			if (!bridge.IsReady) return Results.Json(NotReady("clients"), statusCode: 503);
			return Results.Ok(new { clients = bridge.Clients().Select(ClientDto.From) });
		});

		app.MapGet("/folders", (DeckBridge bridge) =>
		{
			if (!bridge.IsReady) return Results.Json(NotReady("folders"), statusCode: 503);
			return Results.Ok(new { folders = bridge.Folders().Select(f => new { f.Id, f.Label }) });
		});

		app.MapGet("/profiles", (DeckBridge bridge) =>
		{
			if (!bridge.IsReady) return Results.Json(NotReady("profiles"), statusCode: 503);
			return Results.Ok(new { profiles = bridge.Profiles().Select(p => new { p.Id, p.Label }) });
		});

		app.MapPost("/navigate", async (NavigateRequest request, DeckBridge bridge, CancellationToken ct) =>
		{
			if (!bridge.IsReady) return Results.Json(NotReady("navigate"), statusCode: 503);
			try
			{
				return Results.Ok(await bridge.NavigateAsync(request, ct));
			}
			catch (InvalidOperationException ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		});

		app.MapPost("/restore", async (TargetRequest request, DeckBridge bridge, CancellationToken ct) =>
		{
			if (!bridge.IsReady) return Results.Json(NotReady("restore"), statusCode: 503);
			return Results.Ok(await bridge.RestoreAsync(request, ct));
		});

		app.MapPost("/back", async (TargetRequest request, DeckBridge bridge, CancellationToken ct) =>
		{
			if (!bridge.IsReady) return Results.Json(NotReady("back"), statusCode: 503);
			return Results.Ok(await bridge.GoBackAsync(request, ct));
		});
	}

	private static object NotReady(string what)
		=> new { error = $"not ready: {what} needs an active Macro Deck session" };
}

internal static class ClientDto
{
	public static object From(MacroDeck.Sdk.Decks.DeckClient c) => new
	{
		clientId = c.ClientId,
		deviceId = c.DeviceId,
		profileId = c.ProfileId,
		folderId = c.FolderId,
	};
}

internal static class RuleDto
{
	public static object From(ConfiguredRule r) => new
	{
		name = r.Name,
		match = new { app_id = r.AppId },
		folder = r.Folder,
		profile = (object?)null,
		return_on_focus_loss = r.ReturnOnFocusLoss,
		client = (object?)null,
	};
}