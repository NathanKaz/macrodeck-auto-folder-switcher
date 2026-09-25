using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Ui;

namespace MacroDeck.Bridge;

/// <summary>
/// In-app settings for the switcher: every config entry is exactly one app→folder rule, so the
/// app's own add/edit-entry list is the rule editor. Fields are plain declared inputs
/// (ServesConfigUiTree is false), so the stock renderer draws one form per entry: name, app id
/// (with suggestions from the focus history), target folder (from the live deck), and the
/// return-on-focus-loss toggle. The host persists the four values into the entry store, and the
/// watcher reads them back from <c>GET /rules</c>.
/// </summary>
public sealed class AutoFolderConfigFlowProvider : IUiConfigFlowProvider
{
	private readonly DeckBridge _bridge;

	public AutoFolderConfigFlowProvider(DeckBridge bridge) => _bridge = bridge;

	public bool AllowsMultipleConfigurations => true;

	public bool ServesConfigUiTree => false;

	public IConfigFlow CreateConfigFlow() => new AutoFolderConfigFlow(_bridge);
}

public sealed class AutoFolderConfigFlow : IUiConfigFlow
{
	private const string StepId = "rule";
	private const string KeyName = "name";
	private const string KeyApp = "application";
	private const string KeyFolder = "folder";
	private const string KeyReturn = "returnOnFocusLoss";

	private readonly DeckBridge _bridge;

	public AutoFolderConfigFlow(DeckBridge bridge) => _bridge = bridge;

	// Declared fields only: the app's own renderer draws the form, so no UI tree session.
	public Task<IUiSession?> CreateUiSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
		=> Task.FromResult<IUiSession?>(null);

	public async Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
	{
		var prefill = await LoadExistingAsync(context, cancellationToken);
		return ConfigFlowResult.Step(await BuildStepAsync(prefill, cancellationToken));
	}

	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		if (stepId != StepId)
		{
			return ConfigFlowResult.Error(
				new ConfigFlowStep { StepId = StepId, Fields = [] },
				LocalizedText.FromLiteral("unknown configuration step"),
				new Dictionary<string, LocalizedText>());
		}

		var name = ReadText(input, KeyName);
		var app = ReadText(input, KeyApp);
		var folder = ReadText(input, KeyFolder);
		var returnOnFocusLoss = ReadBool(input, KeyReturn);

		var problems = new Dictionary<string, LocalizedText>();
		if (string.IsNullOrWhiteSpace(name))
		{
			problems[KeyName] = LocalizedText.FromLiteral("rule name is required");
		}
		if (string.IsNullOrWhiteSpace(app))
		{
			problems[KeyApp] = LocalizedText.FromLiteral("application app_id is required");
		}
		if (string.IsNullOrWhiteSpace(folder))
		{
			problems[KeyFolder] = LocalizedText.FromLiteral("choose a folder");
		}
		else
		{
			var labels = await _bridge.FolderOptionLabelsAsync(cancellationToken);
			if (!labels.Contains(folder, StringComparer.Ordinal))
			{
				problems[KeyFolder] = LocalizedText.FromLiteral("that folder no longer exists in Macro Deck - refresh the list");
			}
		}

		if (problems.Count > 0)
		{
			return ConfigFlowResult.Error(
				await BuildStepAsync(new Prefill(name, app, folder, returnOnFocusLoss), cancellationToken),
				LocalizedText.FromLiteral("check the rule fields"),
				problems);
		}

		_bridge.InvalidateRulesCache();

		return ConfigFlowResult.Complete(
			title: name!,
			values: new Dictionary<string, ConfigFlowValue>
			{
				[KeyName] = ConfigFlowValue.Plain(name!),
				[KeyApp] = ConfigFlowValue.Plain(app!),
				[KeyFolder] = ConfigFlowValue.Plain(folder!),
				[KeyReturn] = ConfigFlowValue.Plain(returnOnFocusLoss ? "true" : "false"),
			});
	}

	// ------------------------------------------------------------ building

	private sealed record Prefill(string? Name, string? App, string? Folder, bool ReturnOnFocusLoss);

	private async Task<ConfigFlowStep> BuildStepAsync(Prefill? prefill, CancellationToken ct)
	{
		var appOptions = FocusHints.RunningOptions();
		var folderLabels = await _bridge.FolderOptionLabelsAsync(ct);

		return new ConfigFlowStep
		{
			StepId = StepId,
			Title = LocalizedText.FromLiteral("Auto Folder Switcher"),
			Description = LocalizedText.FromLiteral("A rule: when the application gets focus, the Macro Deck client switches to the selected folder."),
			Fields =
			[
				ActionParameter.Text(
					KeyName,
					LocalizedText.FromLiteral("Rule name"),
					LocalizedText.FromLiteral("Label shown in the settings list and in logs."),
					placeholder: LocalizedText.FromLiteral("Blender"),
					defaultValue: prefill?.Name ?? string.Empty,
					required: true),
				ActionParameter.Autocomplete(
					KeyApp,
					LocalizedText.FromLiteral("Application (app_id)"),
					LocalizedText.FromLiteral("Unique application id taken from focus. Suggestions are applications seen recently; you can also type your own id."),
					options: appOptions,
					optionsSourceId: null,
					placeholder: LocalizedText.FromLiteral("blender"),
					required: true),
				ActionParameter.Choice(
					KeyFolder,
					folderLabels.Select(label => new ActionParameterOption
					{
						Value = label,
						Label = LocalizedText.FromLiteral(label),
					}).ToList(),
					LocalizedText.FromLiteral("Folder in Macro Deck"),
					LocalizedText.FromLiteral("Where to switch the client when the application is focused."),
					defaultValue: prefill?.Folder ?? (folderLabels.Count > 0 ? folderLabels[0] : string.Empty),
					required: true),
				ActionParameter.Toggle(
					KeyReturn,
					LocalizedText.FromLiteral("Return on focus loss"),
					LocalizedText.FromLiteral("When focus leaves the application, return the client to the previous folder (same as ReturnOnFocusLoss)."),
					defaultValue: prefill?.ReturnOnFocusLoss ?? false),
			],
		};
	}

	private async Task<Prefill?> LoadExistingAsync(IConfigFlowContext context, CancellationToken ct)
	{
		// Editing a stored entry: the host hands us its title so we can match a snapshot.
		var title = (context as IConfigFlowEntryContext)?.EntryTitle;
		if (string.IsNullOrWhiteSpace(title))
		{
			return null;
		}
		var entryId = await _bridge.FindEntryIdAsync(title!, ct);
		var config = _bridge.Config;
		if (entryId is null || config is null)
		{
			return null;
		}
		var name = await config.GetStringAsync(entryId.Value, KeyName, ct);
		var app = await config.GetStringAsync(entryId.Value, KeyApp, ct);
		var folder = await config.GetStringAsync(entryId.Value, KeyFolder, ct);
		var raw = await config.GetStringAsync(entryId.Value, KeyReturn, ct);
		return new Prefill(name, app, folder, string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase));
	}

	private static string ReadText(IReadOnlyDictionary<string, object?> input, string key)
	{
		if (!input.TryGetValue(key, out var value) || value is null)
		{
			return string.Empty;
		}
		return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
	}

	private static bool ReadBool(IReadOnlyDictionary<string, object?> input, string key)
	{
		if (!input.TryGetValue(key, out var value) || value is null)
		{
			return false;
		}
		return value switch
		{
			bool b => b,
			string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
			_ => false,
		};
	}
}