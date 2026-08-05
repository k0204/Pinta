using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

public sealed class AiPromptOptimizationService
{
	private readonly AiJobService jobs;

	public AiPromptOptimizationService (AiAuthService auth)
	{
		jobs = new (auth);
	}

	public async Task<AiPromptOptimizationResult> OptimizeAndTranslateAsync (
		string prompt,
		string provider,
		IReadOnlyList<byte[]> referenceImages,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace (prompt))
			throw new ArgumentException ("Prompt is required.", nameof (prompt));
		if (string.IsNullOrWhiteSpace (provider))
			throw new ArgumentException ("Chat provider is required.", nameof (provider));

		var request = new {
			text = BuildInstruction (prompt),
			image_base64 = referenceImages.Select (Convert.ToBase64String).ToArray (),
			provider,
		};
		using JsonDocument result = await jobs.RunChatAsync (request, cancellationToken: cancellationToken);
		if (!result.RootElement.TryGetProperty ("text", out JsonElement text)
			|| text.ValueKind != JsonValueKind.String)
			return new AiPromptOptimizationResult (string.Empty, string.Empty);

		string response = text.GetString ()?.Trim () ?? string.Empty;
		if (response.Length == 0)
			return new AiPromptOptimizationResult (string.Empty, string.Empty);

		try {
			using JsonDocument optimized = JsonDocument.Parse (ExtractJsonObject (response));
			return new AiPromptOptimizationResult (
				ReadText (optimized.RootElement, "optimized_chinese", "chinese_prompt"),
				ReadText (optimized.RootElement, "optimized_english", "english_prompt"));
		} catch (JsonException) {
			return new AiPromptOptimizationResult (string.Empty, string.Empty);
		}
	}

	private static string BuildInstruction (string prompt)
		=> "Optimize the ORIGINAL PROMPT below; do not invent a different prompt. First "
			+ "preserve its subject, action, purpose, composition, colors, style, and explicit "
			+ "constraints, then improve wording and add only precise visual details that are "
			+ "compatible with that prompt. Attached reference images are supporting evidence "
			+ "only: inspect them for useful appearance, identity, composition, or style details, "
			+ "but never let a reference replace, contradict, or introduce a subject, action, "
			+ "or constraint absent from the original prompt. If the prompt and a reference "
			+ "conflict, the original prompt always wins. Return a Chinese version for the user "
			+ "and an English version for the image-generation model; both must have the same "
			+ "meaning and must describe the optimized original prompt. Return only valid JSON "
			+ "with exactly these string fields: optimized_chinese and optimized_english. "
			+ "Do not use Markdown or add explanations.\n\nORIGINAL PROMPT (authoritative):\n---\n"
			+ prompt.Trim ()
			+ "\n---";

	private static string ReadText (JsonElement root, params string[] names)
	{
		foreach (string name in names)
			if (root.TryGetProperty (name, out JsonElement value)
				&& value.ValueKind == JsonValueKind.String)
				return value.GetString ()?.Trim () ?? string.Empty;
		return string.Empty;
	}

	private static string ExtractJsonObject (string response)
	{
		int start = response.IndexOf ('{');
		if (start < 0)
			return response;

		int depth = 0;
		bool inString = false;
		bool escaped = false;
		for (int index = start; index < response.Length; index++) {
			char character = response[index];
			if (inString) {
				if (escaped)
					escaped = false;
				else if (character == '\\')
					escaped = true;
				else if (character == '"')
					inString = false;
				continue;
			}

			if (character == '"')
				inString = true;
			else if (character == '{')
				depth++;
			else if (character == '}' && --depth == 0)
				return response[start..(index + 1)];
		}

		return response;
	}
}

public sealed record AiPromptOptimizationResult (string ChinesePrompt, string EnglishPrompt);
