using LibationFileManager;

namespace LibationEngine;

public static class LocatePaths
{
	/// <summary>Resolves an absolute path that lies under the Books folder, with no <c>..</c> segment.</summary>
	public static bool TryResolve(string? path, string? books, out string full, out string error)
	{
		full = "";
		error = "";
		if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
			error = "path is required.";
		else if (!Path.IsPathRooted(path))
			error = "path must be absolute.";
		else if (path.Split('/', '\\').Contains(".."))
			error = "path must not contain '..'.";
		else if (string.IsNullOrWhiteSpace(books))
			error = "The Books folder is not set.";
		else
		{
			full = Path.GetFullPath(path);
			var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(books));
			var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
			if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison) && !string.Equals(full, root, comparison))
				error = "path must be inside the Books folder.";
		}
		return error.Length == 0;
	}

	public static bool TryResolveAudioFile(string? path, string? books, out string full, out string error)
	{
		if (!TryResolve(path, books, out full, out error))
			return false;
		if (!File.Exists(full))
			error = "No such file.";
		else if (FileTypes.GetFileTypeFromPath(full) != FileType.Audio)
			error = "Not an audio file.";
		return error.Length == 0;
	}

	public static bool TryResolveFolder(string? path, string? books, out string full, out string error)
	{
		if (!TryResolve(path, books, out full, out error))
			return false;
		if (!Directory.Exists(full))
			error = "No such folder.";
		return error.Length == 0;
	}
}
