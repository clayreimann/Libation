using AudibleUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class MasterKeyPreflightTests
{
	private const string Encrypted = """{"Accounts":[{"AccountId":"a","IdentityTokens":{"ExistingAccessToken":{"IsEncrypted":true,"Value":"x"}}}]}""";
	private const string Plain = """{"Accounts":[{"AccountId":"a","IdentityTokens":{"ExistingAccessToken":{"IsEncrypted":false,"Value":"x"}}}]}""";

	private string? _savedFile, _savedKey;

	[TestInitialize]
	public void Init()
	{
		_savedFile = Environment.GetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyFileEnvVar);
		_savedKey = Environment.GetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyEnvVar);
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyFileEnvVar, null);
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyEnvVar, null);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyFileEnvVar, _savedFile);
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyEnvVar, _savedKey);
	}

	private static string WithAccounts(TempDir dir, string json)
	{
		File.WriteAllText(dir.File(AccountsImporter.AccountsFileName), json);
		return dir.Path;
	}

	private static void WriteKey(string path) => File.WriteAllBytes(path, new byte[32]);

	private static bool OsStoreMustNotBeProbed() => throw new AssertFailedException("OS store probed");

	[TestMethod]
	public void Encrypted_without_key_or_os_store_refuses()
	{
		using var dir = new TempDir();
		var error = MasterKeyPreflight.Check(WithAccounts(dir, Encrypted), () => false);

		Assert.IsNotNull(error);
		StringAssert.Contains(error, IdentityTokenStorageWiring.MasterKeyFileEnvVar);
		StringAssert.Contains(error, IdentityTokenStorageWiring.MasterKeyEnvVar);
		StringAssert.Contains(error, IdentityTokenStorageWiring.DefaultMasterKeyFileName);
		Assert.IsFalse(File.Exists(dir.File(IdentityTokenStorageWiring.DefaultMasterKeyFileName)));
	}

	[TestMethod]
	public void Encrypted_with_env_key_file_is_ok()
	{
		using var dir = new TempDir();
		var keyFile = dir.File("k.key");
		WriteKey(keyFile);
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyFileEnvVar, keyFile);

		Assert.IsNull(MasterKeyPreflight.Check(WithAccounts(dir, Encrypted), OsStoreMustNotBeProbed));
	}

	[TestMethod]
	public void Encrypted_with_default_key_file_is_ok()
	{
		using var dir = new TempDir();
		WriteKey(dir.File(IdentityTokenStorageWiring.DefaultMasterKeyFileName));

		Assert.IsNull(MasterKeyPreflight.Check(WithAccounts(dir, Encrypted), OsStoreMustNotBeProbed));
	}

	[TestMethod]
	public void Encrypted_with_env_key_is_ok()
	{
		using var dir = new TempDir();
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyEnvVar, Convert.ToBase64String(new byte[32]));

		Assert.IsNull(MasterKeyPreflight.Check(WithAccounts(dir, Encrypted), OsStoreMustNotBeProbed));
	}

	[TestMethod]
	public void Encrypted_with_os_store_is_ok()
	{
		using var dir = new TempDir();
		Assert.IsNull(MasterKeyPreflight.Check(WithAccounts(dir, Encrypted), () => true));
	}

	[TestMethod]
	public void Unreadable_env_key_file_refuses()
	{
		using var dir = new TempDir();
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyFileEnvVar, dir.File("missing.key"));

		Assert.IsNotNull(MasterKeyPreflight.Check(WithAccounts(dir, Encrypted), OsStoreMustNotBeProbed));
	}

	[TestMethod]
	public void Invalid_env_key_file_refuses()
	{
		using var dir = new TempDir();
		var keyFile = dir.File("bad.key");
		File.WriteAllBytes(keyFile, new byte[5]);
		Environment.SetEnvironmentVariable(IdentityTokenStorageWiring.MasterKeyFileEnvVar, keyFile);

		Assert.IsNotNull(MasterKeyPreflight.Check(WithAccounts(dir, Encrypted), OsStoreMustNotBeProbed));
	}

	[TestMethod]
	public void Plaintext_tokens_are_ok()
	{
		using var dir = new TempDir();
		Assert.IsNull(MasterKeyPreflight.Check(WithAccounts(dir, Plain), OsStoreMustNotBeProbed));
	}

	[TestMethod]
	public void No_accounts_file_is_ok()
	{
		using var dir = new TempDir();
		Assert.IsNull(MasterKeyPreflight.Check(dir.Path, OsStoreMustNotBeProbed));
	}
}
