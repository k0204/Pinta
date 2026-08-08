using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Pinta.Core;
using IOPath = System.IO.Path;

namespace Pinta.PsdAddin;

internal sealed partial class PsdToolsImporter
{
	private static PythonCommand EnsurePythonEnvironment ()
	{
		foreach (PythonCommand command in EnumeratePythonCommands ()) {
			if (IsEnvironmentReady (command))
				return command;
		}

		PythonCommand? bootstrap = FindPythonBootstrap ();
		if (bootstrap is null)
			throw new InvalidOperationException (Translations.GetString (
				"Python 3 is required to import PSD files. Install Python 3 and try again."));

		if (!ConfirmPythonInstallation ())
			throw new OperationCanceledException (Translations.GetString ("PSD import setup was canceled."));

		return InstallPythonEnvironment (bootstrap.Value);
	}

	private static IEnumerable<PythonCommand> EnumeratePythonCommands ()
	{
		string? configuredPython = Environment.GetEnvironmentVariable (python_env_var);
		if (!string.IsNullOrWhiteSpace (configuredPython))
			yield return new PythonCommand (configuredPython.Trim (), string.Empty, configuredPython.Trim ());

		yield return new PythonCommand ("py", "-3", "py -3");
		yield return new PythonCommand ("python", string.Empty, "python");
		yield return new PythonCommand ("python3", string.Empty, "python3");
	}

	private static bool IsEnvironmentReady (PythonCommand command)
	{
		const string check_code = "import PIL, psd_tools";
		if (!TryRunProcess (command, $"-c {Quote (check_code)}", AppContext.BaseDirectory, out ProcessResult result, out _))
			return false;

		return result.ExitCode == 0;
	}

	private static PythonCommand? FindPythonBootstrap ()
	{
		foreach (PythonCommand command in EnumeratePythonCommands ()) {
			if (TryRunProcess (command, "--version", AppContext.BaseDirectory, out _, out _))
				return command;
		}

		return null;
	}

	private static bool ConfirmPythonInstallation ()
	{
		using Adw.MessageDialog dialog = Adw.MessageDialog.New (
			PintaCore.Chrome.MainWindow,
			Translations.GetString ("Set Up PSD Import"),
			Translations.GetString (
				"Python packages are required to import PSD files. Pinta can create a private environment and download them now.\n\nEnvironment: {0}\nConfiguration: {1}",
				ResolvePythonEnvironmentDirectory (),
				ResolveDotEnvPath ()));
		const string cancel_response = "cancel";
		const string install_response = "install";
		dialog.AddResponse (cancel_response, Translations.GetString ("_Cancel"));
		dialog.AddResponse (install_response, Translations.GetString ("Install"));
		dialog.SetResponseAppearance (install_response, Adw.ResponseAppearance.Suggested);
		dialog.Modal = true;
		dialog.DefaultResponse = install_response;
		dialog.CloseResponse = cancel_response;

		return dialog.RunBlocking () == install_response;
	}

	private static PythonCommand InstallPythonEnvironment (PythonCommand bootstrap)
	{
		string environmentDirectory = ResolvePythonEnvironmentDirectory ();
		string pythonPath = ResolvePythonExecutablePath (environmentDirectory);
		string requirementsPath = ResolveRequirementsPath ();
		IProgressDialog progress = PintaCore.Chrome.ProgressDialog;
		bool previousBusy = PintaCore.Chrome.MainWindowBusy;
		progress.Title = Translations.GetString ("Installing PSD Import Support");
		progress.Text = Translations.GetString ("Preparing Python environment...");
		progress.Progress = 0.05;
		progress.Cancellable = false;
		PintaCore.Chrome.MainWindowBusy = true;
		progress.Show ();

		try {
			ProcessResult environmentResult = RunProcess (
				bootstrap,
				$"-m venv {Quote (environmentDirectory)}",
				AppContext.BaseDirectory,
				CreateProgressUpdater (progress, 0.25));
			EnsureCommandSucceeded (
				environmentResult,
				Translations.GetString ("Could not create the Python environment for PSD import."));

			progress.Text = Translations.GetString ("Downloading PSD import dependencies...");
			progress.Progress = 0.35;
			ProcessResult installResult = RunProcess (
				new PythonCommand (pythonPath, string.Empty, pythonPath),
				$"-m pip install --disable-pip-version-check --no-input -r {Quote (requirementsPath)}",
				IOPath.GetDirectoryName (requirementsPath) ?? AppContext.BaseDirectory,
				CreateProgressUpdater (progress, 0.9));
			EnsureCommandSucceeded (
				installResult,
				Translations.GetString ("Could not download the PSD import dependencies."));

			PythonCommand environment = new (pythonPath, string.Empty, pythonPath);
			if (!IsEnvironmentReady (environment))
				throw new InvalidOperationException (Translations.GetString (
					"The PSD Python environment was installed but could not be verified."));

			SaveDotEnvValue (pythonPath);
			progress.Progress = 1;
			progress.Text = Translations.GetString ("PSD import support is ready.");
			return environment;
		} finally {
			progress.Hide ();
			PintaCore.Chrome.MainWindowBusy = previousBusy;
		}
	}

	private static Action CreateProgressUpdater (IProgressDialog progress, double maximum)
	{
		double current = progress.Progress;
		return () => progress.Progress = Math.Min (maximum, current += 0.01);
	}

	private static void EnsureCommandSucceeded (ProcessResult result, string message)
	{
		if (result.ExitCode == 0)
			return;

		string output = result.StandardError.Trim ();
		if (!string.IsNullOrWhiteSpace (result.StandardOutput))
			output += $"{Environment.NewLine}{result.StandardOutput.Trim ()}";

		if (string.IsNullOrWhiteSpace (output))
			throw new InvalidOperationException (message);

		throw new InvalidOperationException (message, new InvalidOperationException (output));
	}

	private static void RunHelper (PythonCommand command, string helperScript, string inputPath, string outputDirectory)
	{
		string arguments = JoinArguments (
			command.Arguments,
			$"{Quote (helperScript)} --input {Quote (inputPath)} --output-dir {Quote (outputDirectory)}");
		ProcessResult result;
		try {
			result = RunProcess (
				command with { Arguments = arguments },
				arguments: string.Empty,
				IOPath.GetDirectoryName (helperScript) ?? AppContext.BaseDirectory,
				updateProgress: null);
		} catch (Exception e) when (e is Win32Exception or InvalidOperationException) {
			throw new InvalidOperationException (
				Translations.GetString ("Failed to run the PSD helper with '{0}'.", command.DisplayName), e);
		}

		EnsureCommandSucceeded (
			result,
			Translations.GetString ("Failed to run the PSD helper with '{0}'.", command.DisplayName));
	}

	private static ProcessResult RunProcess (
		PythonCommand command,
		string arguments,
		string workingDirectory,
		Action? updateProgress)
	{
		ProcessStartInfo startInfo = new () {
			FileName = command.FileName,
			Arguments = JoinArguments (command.Arguments, arguments),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			StandardErrorEncoding = Encoding.UTF8,
			StandardOutputEncoding = Encoding.UTF8,
			WorkingDirectory = workingDirectory,
		};

		using Process process = Process.Start (startInfo)
			?? throw new InvalidOperationException (Translations.GetString ("Failed to start the PSD helper process."));
		Task<string> stdout = process.StandardOutput.ReadToEndAsync ();
		Task<string> stderr = process.StandardError.ReadToEndAsync ();

		while (!process.WaitForExit (100)) {
			updateProgress?.Invoke ();
			GLib.MainContext.Default ().Iteration (false);
		}

		process.WaitForExit ();
		return new ProcessResult (process.ExitCode, stdout.GetAwaiter ().GetResult (), stderr.GetAwaiter ().GetResult ());
	}

	private static string JoinArguments (string prefix, string arguments)
		=> string.IsNullOrWhiteSpace (prefix) ? arguments : $"{prefix} {arguments}";

	private static string ResolveDotEnvPath ()
		=> IOPath.Combine (AppContext.BaseDirectory, dot_env_file_name);

	private static string ResolvePythonEnvironmentDirectory ()
		=> IOPath.Combine (AppContext.BaseDirectory, python_environment_directory_name);

	private static string ResolvePythonExecutablePath (string environmentDirectory)
	{
		string executableDirectory = OperatingSystem.IsWindows () ? "Scripts" : "bin";
		string executableName = OperatingSystem.IsWindows () ? "python.exe" : "python3";
		return IOPath.Combine (environmentDirectory, executableDirectory, executableName);
	}

	private static void LoadDotEnv ()
	{
		string path = ResolveDotEnvPath ();
		if (!File.Exists (path))
			return;

		foreach (string line in File.ReadAllLines (path)) {
			string trimmed = line.Trim ();
			if (trimmed.Length == 0 || trimmed.StartsWith ('#'))
				continue;

			int separator = trimmed.IndexOf ('=');
			if (separator <= 0 || !trimmed[..separator].Trim ().Equals (python_env_var, StringComparison.Ordinal))
				continue;

			string value = trimmed[(separator + 1)..].Trim ().Trim ('"');
			if (!string.IsNullOrWhiteSpace (value))
				Environment.SetEnvironmentVariable (python_env_var, value);
		}
	}

	private static void SaveDotEnvValue (string pythonPath)
	{
		string path = ResolveDotEnvPath ();
		try {
			Directory.CreateDirectory (AppContext.BaseDirectory);
			string[] lines = File.Exists (path) ? File.ReadAllLines (path) : [];
			string value = $"{python_env_var}=\"{pythonPath}\"";
			bool replaced = false;

			for (int index = 0; index < lines.Length; index++) {
				string trimmed = lines[index].TrimStart ();
				if (!trimmed.StartsWith ($"{python_env_var}=", StringComparison.Ordinal))
					continue;

				lines[index] = value;
				replaced = true;
				break;
			}

			if (!replaced) {
				Array.Resize (ref lines, lines.Length + 1);
				lines[^1] = value;
			}

			File.WriteAllLines (path, lines, new UTF8Encoding (false));
			Environment.SetEnvironmentVariable (python_env_var, pythonPath);
		} catch (Exception e) {
			throw new InvalidOperationException (
				Translations.GetString ("Could not write the PSD import environment file '{0}'.", path), e);
		}
	}

	private static bool TryRunProcess (
		PythonCommand command,
		string arguments,
		string workingDirectory,
		out ProcessResult result,
		out string error)
	{
		try {
			result = RunProcess (command, arguments, workingDirectory, updateProgress: null);
			error = string.Empty;
			return true;
		} catch (Exception e) when (e is Win32Exception or InvalidOperationException) {
			result = default;
			error = e.Message;
			return false;
		}
	}

	private readonly record struct PythonCommand (string FileName, string Arguments, string DisplayName);
	private readonly record struct ProcessResult (int ExitCode, string StandardOutput, string StandardError);
}
