using LibationEngine;
using System.Security.Cryptography;
using System.Text;

namespace LibationEngine.Host;

public static class ApiToken
{
	/// <summary>Reads the trimmed token; throws <see cref="DaemonConfigException"/> if the file is missing or empty.</summary>
	public static string Load(string path)
	{
		string text;
		try
		{
			text = File.ReadAllText(path).Trim();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new DaemonConfigException($"Cannot read API token file '{path}': {ex.Message}");
		}

		return text.Length > 0 ? text : throw new DaemonConfigException($"API token file '{path}' is empty.");
	}

	public static bool Matches(string expected, string? presented)
	{
		if (presented is null)
			return false;
		var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
		var b = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
		return CryptographicOperations.FixedTimeEquals(a, b);
	}
}
