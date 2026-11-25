using Newtonsoft.Json.Linq;
using Serilog;
using System.Security.Cryptography;

namespace LibationEngine;

public sealed class AccountsImportException(string message) : Exception(message);

/// <summary>Copies an externally managed AccountsSettings.json into the LibationFiles dir when its sha256 changes.</summary>
public sealed class AccountsImporter(string? importPath, string libationFilesDir)
{
	public const string HashFileName = ".accounts-vault.sha256";
	public const string AccountsFileName = "AccountsSettings.json";
	public const string ExpectedShape = """{"Accounts":[{"IdentityTokens":{...}}, ...]} with at least one account""";

	/// <summary>Returns true when the live accounts file was replaced.</summary>
	public bool Import()
	{
		if (string.IsNullOrWhiteSpace(importPath))
			return false;

		if (!File.Exists(importPath))
		{
			Log.Logger.Warning("Accounts import file {Path} not found; keeping the current accounts", importPath);
			return false;
		}

		var bytes = File.ReadAllBytes(importPath);
		var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
		var hashFile = Path.Combine(libationFilesDir, HashFileName);
		if (File.Exists(hashFile) && File.ReadAllText(hashFile).Trim().Equals(hash, StringComparison.OrdinalIgnoreCase))
			return false;

		if (CheckShape(bytes) is { } problem)
			throw new AccountsImportException($"{importPath} is not a valid accounts file: {problem}. Expected {ExpectedShape}.");

		Directory.CreateDirectory(libationFilesDir);
		var target = Path.Combine(libationFilesDir, AccountsFileName);
		var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllBytes(tmp, bytes);
			File.Move(tmp, target, overwrite: true);
		}
		finally
		{
			if (File.Exists(tmp))
				File.Delete(tmp);
		}
		File.WriteAllText(hashFile, hash + "\n");
		Log.Logger.Information("Imported accounts from {Path}", importPath);
		return true;
	}

	private static string? CheckShape(byte[] bytes)
	{
		JToken root;
		try
		{
			root = JToken.Parse(System.Text.Encoding.UTF8.GetString(bytes));
		}
		catch (Exception ex) when (ex is Newtonsoft.Json.JsonException)
		{
			return "not valid JSON";
		}

		if (root is not JObject obj || obj["Accounts"] is not JArray accounts)
			return "missing 'Accounts' array";
		if (accounts.Count == 0)
			return "'Accounts' is empty";
		if (accounts.Any(a => a is not JObject { } o || o["IdentityTokens"] is not JObject))
			return "an account has no 'IdentityTokens' object";
		return null;
	}
}
