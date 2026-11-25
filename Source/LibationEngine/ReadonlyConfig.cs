namespace LibationEngine;

public sealed class DaemonConfigException(string message) : Exception(message);

/// <summary>A read-only config source dir whose files seed the writable LibationFiles dir.</summary>
public static class ReadonlyConfig
{
	public const string SettingsFileName = "Settings.json";

	public static string DefaultAccountsPath(string readonlyDir)
		=> Path.Combine(readonlyDir, AccountsImporter.AccountsFileName);

	/// <summary>Copies the source's Settings.json over the state dir's; returns true if there was one.</summary>
	public static bool Apply(string readonlyDir, string libationFilesDir)
	{
		if (!Directory.Exists(readonlyDir))
			throw new DaemonConfigException($"Read-only config directory '{readonlyDir}' does not exist.");
		if (IsSameDirectory(readonlyDir, libationFilesDir))
			throw new DaemonConfigException($"The read-only config directory and the LibationFiles directory are both '{Path.GetFullPath(libationFilesDir)}'. Use a separate writable directory for LibationFiles.");

		var source = Path.Combine(readonlyDir, SettingsFileName);
		if (!File.Exists(source))
			return false;

		Directory.CreateDirectory(libationFilesDir);
		var target = Path.Combine(libationFilesDir, SettingsFileName);
		var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.Copy(source, tmp, overwrite: true);
			File.Move(tmp, target, overwrite: true);
		}
		finally
		{
			if (File.Exists(tmp))
				File.Delete(tmp);
		}
		return true;
	}

	private static bool IsSameDirectory(string a, string b)
	{
		static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
		return string.Equals(Normalize(a), Normalize(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
	}
}
