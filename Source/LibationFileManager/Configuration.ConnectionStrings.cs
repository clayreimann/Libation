using System;
using System.ComponentModel;
using System.IO;

namespace LibationFileManager;

public partial class Configuration
{
	[Description("Connection string for Postgresql")]
	public string? PostgresqlConnectionString
	{
		get => GetString(EnvironmentConnectionString.Value);
		set => SetString(value);
	}

	private static readonly Lazy<string?> EnvironmentConnectionString = new(() => ResolveConnectionString(
		Environment.GetEnvironmentVariable("LIBATION_CONNECTION_STRING"),
		Environment.GetEnvironmentVariable("LIBATION_CONNECTION_STRING_FILE")));

	/// <summary>The env value wins; otherwise the trimmed contents of the file, if there is one.</summary>
	internal static string? ResolveConnectionString(string? fromEnvironment, string? filePath)
	{
		if (!string.IsNullOrEmpty(fromEnvironment))
			return fromEnvironment;
		if (string.IsNullOrWhiteSpace(filePath))
			return null;
		return File.ReadAllText(filePath).Trim() is { Length: > 0 } fromFile ? fromFile : null;
	}
}
