using System;
using System.IO;

namespace LibationFileManager;

public static class SqliteStorage
{
	public const string PathEnvironmentVariable = "LIBATION_SQLITE_PATH";

	// only the environment variable can move the database; not a setting
	public static string DatabasePath => Resolve(Configuration.Instance.LibationFiles.Location, Environment.GetEnvironmentVariable(PathEnvironmentVariable));

	internal static string Resolve(string libationFilesLocation, string? environmentOverride)
		=> string.IsNullOrWhiteSpace(environmentOverride)
			? Path.Combine(libationFilesLocation, "LibationContext.db")
			: Path.GetFullPath(environmentOverride);
	public static string ConnectionString => $"Data Source={DatabasePath};Foreign Keys=False;Pooling=False;";
}
