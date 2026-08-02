using System;
using System.IO;

namespace Pinta.Core.AI;

internal static class PromptFileReader
{
	public static string ReadRequired (string path, string promptName)
	{
		string fullPath = Path.GetFullPath (path);
		if (!File.Exists (fullPath))
			throw new InvalidOperationException ($"{promptName} file was not found: {fullPath}");

		string prompt = File.ReadAllText (fullPath).Trim ();
		if (string.IsNullOrWhiteSpace (prompt))
			throw new InvalidOperationException ($"{promptName} file is empty: {fullPath}");
		return prompt;
	}
}
