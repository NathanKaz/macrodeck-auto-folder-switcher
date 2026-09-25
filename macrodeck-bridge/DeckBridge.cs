using System.Collections.Concurrent;
using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Plugin.Hosting.Transport;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Decks;
using Serilog;

namespace MacroDeck.Bridge;

/// <summary>
/// Bridges the loopback REST surface to <see cref="IIntegrationContext.Deck"/>. On top of plain
/// navigation it remembers where each targeted client (or all clients) was before a change, so a
/// "restore" can put it back - the Wayland stand-in for Macro Deck's own ReturnOnFocusLoss.
/// </summary>
public sealed class DeckBridge
{
	private const string AllKey = "\u0000all\u0000";
	private readonly ILogger _logger;
	private readonly object _sync = new();
	private IDeckNavigator? _deck;
	private IIntegrationConfig? _config;
	private readonly ConcurrentDictionary<string, (string? FolderId, string? ProfileId)> _previous = new();

	public DeckBridge(ILogger logger) => _logger = logger.ForContext<DeckBridge>();

	public bool IsReady
	{
		get { lock (_sync) return _deck is not null; }
	}

	public IIntegrationConfig? Config
	{
		get { lock (_sync) return _config; }
	}

	public void Attach(IIntegrationContext context)
	{
		lock (_sync)
		{
			_deck = context.Deck;
			_config = context.Config;
		}
	}

	public void Detach()
	{
		lock (_sync)
		{
			_deck = null;
			_config = null;
			_previous.Clear();
		}
	}

	private static IDeckNavigator Required(DeckBridge bridge, string what)
	{
		lock (bridge._sync)
		{
			if (bridge._deck is null)
			{
				throw new InvalidOperationException($"not ready: {what} needs an active Macro Deck session");
			}
			return bridge._deck;
		}
	}

	private IDeckNavigator Deck(string what) => Required(this, what);

	// ------------------------------------------------------------------ list

	public IReadOnlyList<DeckFolder> Folders() => Deck("folders").GetFolders();
	public IReadOnlyList<DeckProfile> Profiles() => Deck("profiles").GetProfiles();
	public IReadOnlyList<DeckClient> Clients() => Deck("clients").GetClients();

	// ----------------------------------------------------------- navigation

	public async Task<NavigateResult> NavigateAsync(NavigateRequest request, CancellationToken ct)
	{
		var deck = Deck("navigation");
		var clients = deck.GetClients();
		var targets = ResolveTargets(request.Client, clients);

		var folderId = !string.IsNullOrWhiteSpace(request.FolderId)
			? request.FolderId
			: !string.IsNullOrWhiteSpace(request.Folder) ? ResolveFolder(deck.GetFolders(), request.Folder) : null;
		var profileId = !string.IsNullOrWhiteSpace(request.ProfileId)
			? request.ProfileId
			: !string.IsNullOrWhiteSpace(request.Profile) ? ResolveProfile(deck.GetProfiles(), request.Profile) : null;

		if (folderId is null && profileId is null)
		{
			throw new InvalidOperationException("navigate: nothing to do (give folder and/or profile)");
		}
		if (folderId is not null && profileId is not null)
		{
			throw new InvalidOperationException("navigate: pick a folder OR a profile, not both");
		}

		var navigated = new List<string>(targets.Count);
		foreach (var target in targets)
		{
			RecordPrevious(target, clients);
			if (profileId is not null)
			{
				await deck.ChangeProfileAsync(profileId, target, ct);
			}
			else
			{
				await deck.ChangeFolderAsync(folderId!, target, ct);
			}
			_logger.Information("Navigated client {Client} to {What} {Target}",
				target ?? "all", profileId is null ? "folder" : "profile", profileId ?? folderId);
			navigated.Add(target ?? "all");
		}

		return new NavigateResult(navigated, folderId, profileId);
	}

	public async Task<RestoreResult> RestoreAsync(TargetRequest request, CancellationToken ct)
	{
		var deck = Deck("restore");
		var clients = deck.GetClients();
		var targets = ResolveTargets(request.Client, clients);
		var folderIds = deck.GetFolders().Select(f => f.Id).ToHashSet();
		var profileIds = deck.GetProfiles().Select(p => p.Id).ToHashSet();

		var restored = new List<string>();
		foreach (var target in targets)
		{
			var key = target ?? AllKey;
			if (!_previous.TryRemove(key, out var previous))
			{
				continue;
			}

			var folderId = previous.FolderId is not null && folderIds.Contains(previous.FolderId) ? previous.FolderId : null;
			var profileId = previous.ProfileId is not null && profileIds.Contains(previous.ProfileId) ? previous.ProfileId : null;

			if (profileId is not null)
			{
				await deck.ChangeProfileAsync(profileId, target, ct);
				restored.Add(target ?? "all");
			}
			else if (folderId is not null)
			{
				await deck.ChangeFolderAsync(folderId, target, ct);
				restored.Add(target ?? "all");
			}
		}

		return new RestoreResult(restored);
	}

	public async Task<BackResult> GoBackAsync(TargetRequest request, CancellationToken ct)
	{
		var deck = Deck("back");
		var clients = deck.GetClients();
		var targets = ResolveTargets(request.Client, clients);

		var went = new List<string>();
		foreach (var target in targets)
		{
			await deck.GoBackAsync(target, ct);
			went.Add(target ?? "all");
		}

		return new BackResult(went);
	}

	// ------------------------------------------------------------- resolvers

	/// <summary>Null in the returned list means "all clients"; otherwise a concrete origin client id.</summary>
	private static List<string?> ResolveTargets(string? client, IReadOnlyList<DeckClient> clients)
	{
		if (string.IsNullOrWhiteSpace(client) || client == "all")
		{
			return [null];
		}

		var matched = clients
			.Where(c => c.ClientId == client || (c.DeviceId is not null && c.DeviceId == client))
			.Select(c => (string?)c.ClientId)
			.Distinct()
			.ToList();
		return matched.Count > 0 ? matched : [client];
	}

	private static string ResolveFolder(IReadOnlyList<DeckFolder> items, string name)
	{
		var byId = items.FirstOrDefault(i => i.Id == name);
		if (byId is not null)
		{
			return byId.Id;
		}
		var byLabel = items.FirstOrDefault(i => i.Label == name);
		if (byLabel is not null)
		{
			return byLabel.Id;
		}
		throw new InvalidOperationException($"unknown folder '{name}' (see /folders)");
	}

	private static string ResolveProfile(IReadOnlyList<DeckProfile> items, string name)
	{
		var byId = items.FirstOrDefault(i => i.Id == name);
		if (byId is not null)
		{
			return byId.Id;
		}
		var byLabel = items.FirstOrDefault(i => i.Label == name);
		if (byLabel is not null)
		{
			return byLabel.Id;
		}
		throw new InvalidOperationException($"unknown profile '{name}' (see /profiles)");
	}

	private void RecordPrevious(string? target, IReadOnlyList<DeckClient> clients)
	{
		var key = target ?? AllKey;
		if (target is null)
		{
			var c = clients.Count > 0 ? clients[0] : null;
			_previous[key] = (c?.FolderId, c?.ProfileId);
			return;
		}
		var matched = clients.FirstOrDefault(c => c.ClientId == target);
		_previous[key] = (matched?.FolderId, matched?.ProfileId);
	}

	// ---------------------------------------------------------- rule storage

	/// <summary>Each config entry is one rule; entries are returned in creation order.</summary>
	// The Macro Deck host rate-limits plugin→host invocations ("calling back into the host too
	// quickly"), so rules are read from the host at most once per TTL and served from memory in
	// between; a transient rate-limit answer returns the previous snapshot instead of a 500.
	private const int RulesCacheTtlMs = 10_000;
	private readonly object _rulesSync = new();
	private IReadOnlyList<ConfiguredRule>? _rulesCache;
	private DateTime _rulesCachedAtUtc = DateTime.MinValue;

	public void InvalidateRulesCache()
	{
		lock (_rulesSync)
		{
			_rulesCache = null;
		}
	}

	public async Task<IReadOnlyList<ConfiguredRule>> RulesAsync(CancellationToken ct)
	{
		IReadOnlyList<ConfiguredRule>? snapshot;
		lock (_rulesSync)
		{
			snapshot = _rulesCache;
			if (snapshot is not null)
			{
				var age = DateTime.UtcNow - _rulesCachedAtUtc;
				if (age.TotalMilliseconds < RulesCacheTtlMs)
				{
					return snapshot;
				}
			}
		}

		IReadOnlyList<ConfiguredRule>? loaded;
		try
		{
			loaded = await LoadRulesAsync(ct);
		}
		catch (HostInvocationException ex)
		{
			_logger.Warning("Rules refresh throttled by the host ({Message}); serving cached rules", ex.Message);
			return snapshot ?? [];
		}

		lock (_rulesSync)
		{
			_rulesCache = loaded;
			_rulesCachedAtUtc = DateTime.UtcNow;
		}
		return loaded;
	}

	private async Task<IReadOnlyList<ConfiguredRule>> LoadRulesAsync(CancellationToken ct)
	{
		var config = Config;
		if (config is null)
		{
			return [];
		}

		var result = new List<ConfiguredRule>();
		var entries = await config.GetEntriesAsync(ct);
		foreach (var entry in entries)
		{
			// The host rejects calls that arrive while the previous plugin→host RPC is still
			// settling ("calling back into the host too quickly"). Pace each call so the
			// responses are strictly sequential instead of back-to-back.
			var name = await GetPacedStringAsync(config, entry.Id, "name", ct);
			var app = await GetPacedStringAsync(config, entry.Id, "application", ct);
			var folder = await GetPacedStringAsync(config, entry.Id, "folder", ct);
			var raw = await GetPacedStringAsync(config, entry.Id, "returnOnFocusLoss", ct);
			if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(folder))
			{
				continue;
			}
			result.Add(new ConfiguredRule(
				Name: string.IsNullOrWhiteSpace(name) ? app : name,
				AppId: app,
				Folder: folder,
				ReturnOnFocusLoss: string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)));
		}
		return result;
	}

	private static async Task<string> GetPacedStringAsync(
		IIntegrationConfig config,
		Guid entryId,
		string key,
		CancellationToken ct)
	{
		await Task.Delay(PaceMs, ct);
		return (await config.GetStringAsync(entryId, key, ct)) ?? string.Empty;
	}

	private static int PaceMs
	{
		get
		{
			var raw = Environment.GetEnvironmentVariable("MACRO_DECK_BRIDGE_RULES_PACE_MS");
			return int.TryParse(raw, out var ms) ? Math.Max(ms, 0) : 250;
		}
	}

	public async Task<Guid?> FindEntryIdAsync(string title, CancellationToken ct)
	{
		var config = Config;
		if (config is null)
		{
			return null;
		}
		foreach (var entry in await config.GetEntriesAsync(ct))
		{
			if (string.Equals(entry.Title, title, StringComparison.Ordinal))
			{
				return entry.Id;
			}
		}
		return null;
	}

	/// <summary>Distinct, non-empty deck folder labels in deck order (edit-time Choice options).</summary>
	public Task<IReadOnlyList<string>> FolderOptionLabelsAsync(CancellationToken ct)
	{
		if (!IsReady)
		{
			return Task.FromResult<IReadOnlyList<string>>([]);
		}
		return Task.FromResult<IReadOnlyList<string>>(
			Folders().Select(f => f.Label).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList());
	}
}

/// <summary>Running-app suggestions for the config-flow autocomplete, read from the focus history.</summary>
public static class FocusHints
{
	public static IReadOnlyList<ActionParameterOption> RunningOptions()
	{
		var ids = new List<string>();
		// Live list of open windows first; the focus history and current focus are the fallback.
		foreach (var source in new[] { AppsPath(), HistoryPath(), CurrentPath() })
		{
			foreach (var id in ReadAppIds(source, isJsonArray: source == AppsPath()))
			{
				if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal))
				{
					ids.Add(id);
				}
			}
		}
		ids.Sort(StringComparer.OrdinalIgnoreCase);
		return ids
			.Take(300)
			.Select(id => new ActionParameterOption { Value = id, Label = LocalizedText.FromLiteral(id) })
			.ToList();
	}

	private static List<string> ReadAppIds(string path, bool isJsonArray)
	{
		var ids = new List<string>();
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return ids;
		}
		if (isJsonArray)
		{
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(path));
				var root = doc.RootElement;
				if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
				{
					foreach (var item in apps.EnumerateArray())
					{
						if (item.ValueKind == JsonValueKind.String)
						{
							ids.Add(item.GetString() ?? string.Empty);
						}
					}
				}
			}
			catch (JsonException)
			{
				// File mid-rewrite; try again next refresh.
			}
			return ids;
		}

		// JSONL (one focus record per line); a torn last line is skipped.
		foreach (var line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}
			try
			{
				using var record = JsonDocument.Parse(line);
				if (record.RootElement.TryGetProperty("app_id", out var app) && app.ValueKind == JsonValueKind.String)
				{
					ids.Add(app.GetString() ?? string.Empty);
				}
			}
			catch (JsonException)
			{
				// A torn last line from a concurrent write; skip it.
			}
		}
		return ids;
	}

	private static string AppsPath()
		=> Environment.GetEnvironmentVariable("MACRO_DECK_APPS_FILE")
			?? Path.Combine(RuntimeDir(), "macrodeck-apps.json");

	private static string HistoryPath()
		=> Environment.GetEnvironmentVariable("MACRO_DECK_FOCUS_HISTORY")
			?? Path.Combine(RuntimeDir(), "macrodeck-focus.history.jsonl");

	private static string CurrentPath()
		=> Environment.GetEnvironmentVariable("MACRO_DECK_FOCUS_FILE")
			?? Path.Combine(RuntimeDir(), "macrodeck-focus.json");

	private static string RuntimeDir()
		=> Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
}

public sealed record ConfiguredRule(string Name, string AppId, string Folder, bool ReturnOnFocusLoss);

public sealed record NavigateRequest(string? Folder, string? Profile, string? FolderId, string? ProfileId, string? Client);
public sealed record TargetRequest(string? Client);
public sealed record NavigateResult(IReadOnlyList<string> Navigated, string? FolderId, string? ProfileId);
public sealed record RestoreResult(IReadOnlyList<string> Restored);
public sealed record BackResult(IReadOnlyList<string> WentBack);