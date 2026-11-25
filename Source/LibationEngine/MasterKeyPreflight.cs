using AudibleUtilities;
using Newtonsoft.Json.Linq;

namespace LibationEngine;

/// <summary>Startup guard: encrypted tokens with no usable master key.</summary>
public static class MasterKeyPreflight
{
	/// <summary>Returns an error message, or null when startup may proceed. <paramref name="osStoreAvailable"/> is consulted last.</summary>
	public static string? Check(string libationFilesDir, Func<bool>? osStoreAvailable = null)
	{
		var accountsFile = Path.Combine(libationFilesDir, AccountsImporter.AccountsFileName);
		if (!HasEncryptedTokens(accountsFile))
			return null;

		var supplied = IdentityTokenStorageWiring.TryResolveSuppliedKeyStore(libationFilesDir);
		if (supplied is not null)
			return supplied.IsAvailable
				? null
				: $"The supplied master key is unusable: {supplied.UnavailableReason}";

		if ((osStoreAvailable ?? (() => IdentityTokenStorageWiring.IsOsSecretStoreAvailable(out _)))())
			return null;

		return $"""
			{AccountsImporter.AccountsFileName} contains encrypted tokens but no master key was supplied, and no OS secret store is available.
			Without the original key they cannot be decrypted, and Libation will not create a new one. Supply the key by one of:
			  - copying {IdentityTokenStorageWiring.DefaultMasterKeyFileName} (from 'LibationCli export-master-key') next to {AccountsImporter.AccountsFileName} in {libationFilesDir}
			  - setting {IdentityTokenStorageWiring.MasterKeyFileEnvVar} to the path of that key file
			  - setting {IdentityTokenStorageWiring.MasterKeyEnvVar} to the Base64-encoded key
			Or convert the tokens to plaintext on the desktop first. Details: {AccountSettingsDecryptFailure.FaqUrl}
			""";
	}

	private static bool HasEncryptedTokens(string accountsFile)
	{
		if (!File.Exists(accountsFile))
			return false;

		try
		{
			return JObject.Parse(File.ReadAllText(accountsFile))
				.SelectTokens("$..IsEncrypted")
				.Any(t => t.Type == JTokenType.Boolean && t.Value<bool>());
		}
		catch (Exception)
		{
			return false;
		}
	}
}
