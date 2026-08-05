using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pinta.Core.AI;

public sealed record AiPromptHistoryItem (
	[property: JsonPropertyName ("chinese_prompt")] string ChinesePrompt,
	[property: JsonPropertyName ("english_prompt")] string EnglishPrompt);

public static class AiPromptHistory
{
	private const string history_key = "ai-prompt-history";
	private const int max_history = 20;
	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNameCaseInsensitive = true,
	};

	public static IReadOnlyList<AiPromptHistoryItem> Load (ISettingsService settings)
	{
		string json = settings.GetSetting (history_key, string.Empty);
		if (string.IsNullOrWhiteSpace (json))
			return [];

		try {
			return [.. (JsonSerializer.Deserialize<AiPromptHistoryItem[]> (json, json_options) ?? [])
				.Where (IsValid)
				.Select (Normalize)
				.GroupBy (item => item.ChinesePrompt, StringComparer.Ordinal)
				.Select (group => group.First ())
				.Take (max_history)];
		} catch (JsonException) {
			return [];
		}
	}

	public static void Add (
		ISettingsService settings,
		string chinesePrompt,
		string englishPrompt)
	{
		if (string.IsNullOrWhiteSpace (chinesePrompt))
			return;

		string normalizedChinese = chinesePrompt.Trim ();
		string normalizedEnglish = englishPrompt.Trim ();
		List<AiPromptHistoryItem> history = [.. Load (settings)];
		AiPromptHistoryItem? existing = history.FirstOrDefault (item => item.ChinesePrompt == normalizedChinese);
		if (string.IsNullOrWhiteSpace (normalizedEnglish) && existing is not null)
			normalizedEnglish = existing.EnglishPrompt;
		history.RemoveAll (item => item.ChinesePrompt == normalizedChinese);
		history.Insert (0, new (normalizedChinese, normalizedEnglish));
		if (history.Count > max_history)
			history.RemoveRange (max_history, history.Count - max_history);
		settings.PutSetting (history_key, JsonSerializer.Serialize (history, json_options));
	}

	private static bool IsValid (AiPromptHistoryItem item)
		=> !string.IsNullOrWhiteSpace (item.ChinesePrompt);

	private static AiPromptHistoryItem Normalize (AiPromptHistoryItem item)
		=> new (item.ChinesePrompt.Trim (), item.EnglishPrompt?.Trim () ?? string.Empty);
}
