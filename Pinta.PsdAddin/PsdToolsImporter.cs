using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cairo;
using GdkPixbuf;
using Pinta.Core;
using IOPath = System.IO.Path;

namespace Pinta.PsdAddin;

internal sealed class PsdToolsImporter : IImageImporter
{
        private const string helper_directory_name = "python";
	private const string helper_script_name = "psd_tools_helper.py";
	private const string requirements_file_name = "requirements.txt";
	private const string python_env_var = "PINTA_PSDTOOLS_PYTHON";
	private const string helper_env_var = "PINTA_PSDTOOLS_HELPER";
        private const string keep_output_env_var = "PINTA_PSDTOOLS_KEEP_OUTPUT";

	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNameCaseInsensitive = true,
	};

	public Document Import (Gio.File file)
	{
		string? path = file.GetPath ();
		if (string.IsNullOrWhiteSpace (path))
			throw new InvalidOperationException (Translations.GetString ("PSD import currently requires a local file path."));

		string helperScript = ResolveHelperScriptPath ();
		string outputDirectory = IOPath.Combine (IOPath.GetTempPath (), $"pinta-psd-{Guid.NewGuid ():N}");

		try {
			Directory.CreateDirectory (outputDirectory);
			RunHelper (helperScript, path, outputDirectory);

			PsdImportManifest manifest = LoadManifest (outputDirectory);
			ValidateManifest (manifest);

			Document document = new (
				PintaCore.Actions,
				PintaCore.Tools,
				PintaCore.Workspace,
				new Size (manifest.Width, manifest.Height),
				file,
				"psd");

			Dictionary<string, UserLayer> layersById = [];
			ImportLayers (document, manifest.Layers, outputDirectory, parent: null, layersById);

			if (layersById.Count == 0) {
				UserLayer layer = document.Layers.AddNewLayer (Translations.GetString ("Background"));
				layer.Name = Translations.GetString ("Background");
			} else if (manifest.SelectedLayerId is not null && layersById.TryGetValue (manifest.SelectedLayerId, out var selected)) {
				document.Layers.SetCurrentUserLayer (selected);
			} else {
				document.Layers.SetCurrentUserLayer (document.Layers.AllLayers[^1]);
			}

			return document;
                } finally {
                        if (ShouldKeepOutputDirectory ()) {
                                Console.Error.WriteLine ($"PSD helper output kept at: {outputDirectory}");
                        } else {
                                TryDeleteDirectory (outputDirectory);
                        }
		}
	}

	private static void ImportLayers (
		Document document,
		IReadOnlyList<PsdImportLayerNode> nodes,
		string outputDirectory,
		UserLayer? parent,
		Dictionary<string, UserLayer> layersById)
	{
		for (int index = 0; index < nodes.Count; index++) {
			PsdImportLayerNode node = nodes[index];
			UserLayer layer = document.Layers.CreateLayer (string.IsNullOrWhiteSpace (node.Name) ? Translations.GetString ("Layer") : node.Name);

			layer.Hidden = node.Hidden;
			layer.Opacity = Math.Clamp (node.Opacity, 0.0, 1.0);
			layer.BlendMode = ToBlendMode (node.BlendMode);
			layer.Expanded = true;

			LoadSurface (IOPath.Combine (outputDirectory, node.Surface), layer);
			document.Layers.Insert (layer, new LayerPosition (parent, index));
			layersById[node.Id] = layer;

			ImportLayers (document, node.Children, outputDirectory, layer, layersById);
		}
	}

	private static void LoadSurface (string surfacePath, UserLayer layer)
	{
		if (!File.Exists (surfacePath))
			throw new InvalidDataException (Translations.GetString ("Missing rendered PSD layer surface '{0}'.", surfacePath));

		try {
			using Pixbuf pixbuf = Pixbuf.NewFromFile (surfacePath)
				?? throw new InvalidDataException (Translations.GetString ("Rendered PSD layer surface '{0}' could not be decoded as PNG.", surfacePath));

			if (pixbuf.Width != layer.Surface.Width || pixbuf.Height != layer.Surface.Height)
				throw new InvalidDataException (Translations.GetString ("Rendered PSD layer surface '{0}' does not match the document dimensions.", surfacePath));

			using Context context = new (layer.Surface);
			context.DrawPixbuf (pixbuf, PointD.Zero);
		} catch (GLib.GException e) {
			throw new InvalidDataException (Translations.GetString ("Rendered PSD layer surface '{0}' is not a valid PNG.", surfacePath), e);
		}
	}

	private static BlendMode ToBlendMode (string value)
	{
		string normalized = value.Trim ().ToLowerInvariant ().Replace ("-", string.Empty).Replace ("_", string.Empty);
		return normalized switch {
			"normal" => BlendMode.Normal,
			"passthrough" => BlendMode.Normal,
			"multiply" => BlendMode.Multiply,
			"colorburn" => BlendMode.ColorBurn,
			"colordodge" => BlendMode.ColorDodge,
			"overlay" => BlendMode.Overlay,
			"difference" => BlendMode.Difference,
			"lighten" => BlendMode.Lighten,
			"darken" => BlendMode.Darken,
			"screen" => BlendMode.Screen,
			"xor" => BlendMode.Xor,
			"hardlight" => BlendMode.HardLight,
			"softlight" => BlendMode.SoftLight,
			"color" => BlendMode.Color,
			"luminosity" => BlendMode.Luminosity,
			"hue" => BlendMode.Hue,
			"saturation" => BlendMode.Saturation,
			_ => BlendMode.Normal,
		};
	}

	private static PsdImportManifest LoadManifest (string outputDirectory)
	{
		string manifestPath = IOPath.Combine (outputDirectory, "manifest.json");
		if (!File.Exists (manifestPath))
			throw new InvalidDataException (Translations.GetString ("The PSD helper did not produce manifest.json."));

		using FileStream stream = File.OpenRead (manifestPath);
		return JsonSerializer.Deserialize<PsdImportManifest> (stream, json_options)
			?? throw new InvalidDataException (Translations.GetString ("The PSD helper produced an empty manifest."));
	}

	private static void ValidateManifest (PsdImportManifest manifest)
	{
		if (manifest.Width <= 0 || manifest.Height <= 0)
			throw new InvalidDataException (Translations.GetString ("The PSD helper reported invalid document dimensions."));

		ValidateLayers (manifest.Layers, ids: []);
	}

	private static void ValidateLayers (IReadOnlyList<PsdImportLayerNode> nodes, HashSet<string> ids)
	{
		foreach (PsdImportLayerNode node in nodes) {
			if (string.IsNullOrWhiteSpace (node.Id) || !ids.Add (node.Id))
				throw new InvalidDataException (Translations.GetString ("The PSD helper reported an invalid or duplicate layer id."));

			if (string.IsNullOrWhiteSpace (node.Surface))
				throw new InvalidDataException (Translations.GetString ("The PSD helper reported a layer without a rendered surface."));

			if (!double.IsFinite (node.Opacity))
				throw new InvalidDataException (Translations.GetString ("The PSD helper reported an invalid layer opacity."));

			ValidateLayers (node.Children, ids);
		}
	}

	private static void RunHelper (string helperScript, string inputPath, string outputDirectory)
	{
		var attempts = EnumeratePythonCommands (helperScript, inputPath, outputDirectory);
		List<string> errors = [];

		foreach (var command in attempts) {
			try {
				ProcessStartInfo startInfo = new () {
					FileName = command.FileName,
					Arguments = command.Arguments,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardError = true,
					RedirectStandardOutput = true,
					StandardErrorEncoding = Encoding.UTF8,
					StandardOutputEncoding = Encoding.UTF8,
					WorkingDirectory = IOPath.GetDirectoryName (helperScript) ?? AppContext.BaseDirectory,
				};

				using Process process = Process.Start (startInfo)
					?? throw new InvalidOperationException ("Failed to start the PSD helper process.");

				string stdout = process.StandardOutput.ReadToEnd ();
				string stderr = process.StandardError.ReadToEnd ();
				process.WaitForExit ();

				if (process.ExitCode == 0)
					return;

				errors.Add ($"{command.DisplayName}: {stderr.Trim ()}{(string.IsNullOrWhiteSpace (stdout) ? string.Empty : Environment.NewLine + stdout.Trim ())}");
			} catch (Exception e) when (e is Win32Exception or InvalidOperationException) {
				errors.Add ($"{command.DisplayName}: {e.Message}");
			}
		}

		string requirementsPath = ResolveRequirementsPath ();
		string details = string.Join (Environment.NewLine + Environment.NewLine, errors);
		throw new InvalidOperationException (
			Translations.GetString (
				"Failed to run the PSD helper. Set '{0}' to a Python executable with psd-tools installed, or install the dependencies listed in '{1}'.{2}{3}",
				python_env_var,
				requirementsPath,
				string.IsNullOrWhiteSpace (details) ? string.Empty : Environment.NewLine + Environment.NewLine,
				details));
	}

	private static IEnumerable<PythonCommand> EnumeratePythonCommands (string helperScript, string inputPath, string outputDirectory)
	{
		string helperArgument = Quote (helperScript);
		string inputArgument = Quote (inputPath);
		string outputArgument = Quote (outputDirectory);
		string commandTail = $"{helperArgument} --input {inputArgument} --output-dir {outputArgument}";

		string? configuredPython = Environment.GetEnvironmentVariable (python_env_var);
		if (!string.IsNullOrWhiteSpace (configuredPython))
			yield return new PythonCommand (configuredPython, commandTail, configuredPython);

		yield return new PythonCommand ("py", $"-3 {commandTail}", "py -3");
		yield return new PythonCommand ("python", commandTail, "python");
		yield return new PythonCommand ("python3", commandTail, "python3");
	}

	private static string ResolveHelperScriptPath ()
	{
		string? configured = Environment.GetEnvironmentVariable (helper_env_var);
		if (!string.IsNullOrWhiteSpace (configured) && File.Exists (configured))
			return configured;

		string assemblyDirectory = IOPath.GetDirectoryName (Assembly.GetExecutingAssembly ().Location) ?? AppContext.BaseDirectory;
                string buildOutputPath = IOPath.Combine (assemblyDirectory, helper_directory_name, helper_script_name);
		if (File.Exists (buildOutputPath))
			return buildOutputPath;

                string sourcePath = IOPath.GetFullPath (IOPath.Combine (assemblyDirectory, "..", "..", "..", "..", "Pinta.PsdAddin", helper_directory_name, helper_script_name));
		if (File.Exists (sourcePath))
			return sourcePath;

		throw new FileNotFoundException (Translations.GetString ("Could not locate '{0}'.", helper_script_name), helper_script_name);
	}

	private static string ResolveRequirementsPath ()
	{
		string assemblyDirectory = IOPath.GetDirectoryName (Assembly.GetExecutingAssembly ().Location) ?? AppContext.BaseDirectory;
                string buildOutputPath = IOPath.Combine (assemblyDirectory, helper_directory_name, requirements_file_name);
		if (File.Exists (buildOutputPath))
			return buildOutputPath;

                return IOPath.GetFullPath (IOPath.Combine (assemblyDirectory, "..", "..", "..", "..", "Pinta.PsdAddin", helper_directory_name, requirements_file_name));
	}

	private static string Quote (string value)
		=> "\"" + value.Replace ("\"", "\\\"") + "\"";

        private static bool ShouldKeepOutputDirectory ()
        {
                string? value = Environment.GetEnvironmentVariable (keep_output_env_var);
                if (string.IsNullOrWhiteSpace (value))
                        return false;

                value = value.Trim ();
                return value.Equals ("1", StringComparison.OrdinalIgnoreCase)
                        || value.Equals ("true", StringComparison.OrdinalIgnoreCase)
                        || value.Equals ("yes", StringComparison.OrdinalIgnoreCase);
        }

	private static void TryDeleteDirectory (string path)
	{
		try {
			if (Directory.Exists (path))
				Directory.Delete (path, recursive: true);
		} catch { }
	}

	private readonly record struct PythonCommand (string FileName, string Arguments, string DisplayName);
}
