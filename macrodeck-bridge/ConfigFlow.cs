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
				LocalizedText.FromLiteral("неизвестный шаг настройки"),
				new Dictionary<string, LocalizedText>());
		}

		var name = ReadText(input, KeyName);
		var app = ReadText(input, KeyApp);
		var folder = ReadText(input, KeyFolder);
		var returnOnFocusLoss = ReadBool(input, KeyReturn);

		var problems = new Dictionary<string, LocalizedText>();
		if (string.IsNullOrWhiteSpace(name))
		{
			problems[KeyName] = LocalizedText.FromLiteral("укажите имя правила");
		}
		if (string.IsNullOrWhiteSpace(app))
		{
			problems[KeyApp] = LocalizedText.FromLiteral("укажите app_id приложения");
		}
		if (string.IsNullOrWhiteSpace(folder))
		{
			problems[KeyFolder] = LocalizedText.FromLiteral("выберите папку");
		}
		else
		{
			var labels = await _bridge.FolderOptionLabelsAsync(cancellationToken);
			if (!labels.Contains(folder, StringComparer.Ordinal))
			{
				problems[KeyFolder] = LocalizedText.FromLiteral("этой папки больше нет в Macro Deck — обновите список");
			}
		}

		if (problems.Count > 0)
		{
			return ConfigFlowResult.Error(
				await BuildStepAsync(new Prefill(name, app, folder, returnOnFocusLoss), cancellationToken),
				LocalizedText.FromLiteral("проверьте поля правила"),
				problems);
		}

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
			Description = LocalizedText.FromLiteral("Правило: когда приложение получает фокус, клиент Macro Deck переходит в выбранную папку."),
			Fields =
			[
				ActionParameter.Text(
					KeyName,
					LocalizedText.FromLiteral("Имя правила"),
					LocalizedText.FromLiteral("Подпись записи в списке настроек и в логах."),
					placeholder: LocalizedText.FromLiteral("Blender"),
					defaultValue: prefill?.Name ?? string.Empty,
					required: true),
				ActionParameter.Autocomplete(
					KeyApp,
					LocalizedText.FromLiteral("Приложение (app_id)"),
					LocalizedText.FromLiteral("Уникальный id приложения из фокуса. Подсказки — приложения, замеченные недавно; можно ввести и свой id."),
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
					LocalizedText.FromLiteral("Папка в Macro Deck"),
					LocalizedText.FromLiteral("Куда переключать клиент, когда приложение в фокусе."),
					defaultValue: prefill?.Folder ?? (folderLabels.Count > 0 ? folderLabels[0] : string.Empty),
					required: true),
				ActionParameter.Toggle(
					KeyReturn,
					LocalizedText.FromLiteral("Возврат при потере фокуса"),
					LocalizedText.FromLiteral("Когда фокус ушёл с приложения, вернуть клиент в предыдущую папку (аналог ReturnOnFocusLoss)."),
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