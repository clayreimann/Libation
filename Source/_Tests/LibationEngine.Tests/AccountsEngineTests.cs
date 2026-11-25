using AudibleApi;
using AudibleApi.Authorization;
using AudibleApi.Cryptography;
using AudibleUtilities;
using Dinah.Core.Security;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Text.Json;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class AccountsEngineTests
{
	private string directory = "";
	private string? previousDirectory;
	private readonly DbEngineData data = new();

	[TestInitialize]
	public void Init()
	{
		directory = Path.Combine(Path.GetTempPath(), $"libation-engine-accounts-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		previousDirectory = Environment.GetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, directory);
		Configuration.CreateMockInstance();
		AudibleApiStorage.EnsureAccountsSettingsFileExists();
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, previousDirectory);
		try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
	}

	private static Account Registered(string id, string locale, bool modernDevice = true, bool refresh = true, DateTime? expires = null)
	{
		var identity = new Identity(Localization.Get(locale));
		identity.Update(
			new PrivateKey(RSA.Create(2048).ExportRSAPrivateKeyPem()),
			new AdpToken("{enc:abcdefg}{key:1234}{iv:56789}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}"),
			new AccessToken("Atna|SECRET_ACCESS", expires ?? new DateTime(2200, 1, 1, 12, 0, 0, DateTimeKind.Utc)),
			new RefreshToken(refresh ? "Atnr|SECRET_REFRESH" : ""),
			new List<KeyValuePair<string, SecretString>> { new("session-id", "cookie-value") },
			deviceSerialNumber: modernDevice ? "0123456789abcdef0123" : "device-serial",
			deviceType: modernDevice ? AudibleApi.Resources.DeviceType : "device-type",
			amazonAccountId: "amzn-account",
			deviceName: "device-name",
			storeAuthenticationCookie: "store-auth-cookie");
		return new Account(id) { AccountName = id + " name", IdentityTokens = identity, LibraryScan = true };
	}

	private static void Seed(params Account[] accounts)
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		foreach (var account in accounts)
			persister.AccountsSettings.Add(account);
	}

	[TestMethod]
	public void Signing_in_again_under_an_import_survives_until_the_import_file_changes()
	{
		var vault = Path.Combine(directory, "vault.json");
		var live = Path.Combine(directory, AccountsImporter.AccountsFileName);
		var importer = new AccountsImporter(vault, directory);

		Seed(new Account("imported@example.com") { IdentityTokens = new Identity(Localization.Get("us")) });
		File.Copy(live, vault);
		Assert.IsTrue(importer.Import());
		Assert.AreEqual(AuthStates.NeedsLogin, data.GetAccounts().Single().AuthState);

		using (var persister = AudibleApiStorage.GetAccountsSettingsPersister())
		{
			persister.AccountsSettings.Delete(persister.AccountsSettings.GetAccount("imported@example.com", "us")!);
			persister.AccountsSettings.Add(Registered("imported@example.com", "us"));
		}
		Assert.AreEqual(AuthStates.SignedIn, data.GetAccounts().Single().AuthState);

		Assert.IsFalse(importer.Import());
		Assert.AreEqual(AuthStates.SignedIn, data.GetAccounts().Single().AuthState);

		File.WriteAllText(vault, File.ReadAllText(vault).Replace("imported@example.com", "imported@example.com "));
		Assert.IsTrue(importer.Import());
		Assert.AreEqual(AuthStates.NeedsLogin, data.GetAccounts().Single().AuthState);
	}

	[TestMethod]
	public void Auth_state_follows_the_desktop_rules()
	{
		var reRegister = Registered("rereg@example.com", "us");
		reRegister.RequiresReRegistration = true;
		Seed(Registered("modern@example.com", "us"), Registered("legacy@example.com", "us", modernDevice: false), reRegister, new Account("fresh@example.com") { IdentityTokens = new Identity(Localization.Get("us")) });

		var byId = data.GetAccounts().ToDictionary(a => a.AccountId);

		Assert.AreEqual((AuthStates.SignedIn, CredentialKinds.Modern), (byId["modern@example.com"].AuthState, byId["modern@example.com"].Credentials));
		Assert.AreEqual((AuthStates.SignedIn, CredentialKinds.Legacy), (byId["legacy@example.com"].AuthState, byId["legacy@example.com"].Credentials));
		Assert.AreEqual((AuthStates.NeedsLogin, CredentialKinds.None), (byId["rereg@example.com"].AuthState, byId["rereg@example.com"].Credentials));
		Assert.AreEqual((AuthStates.NeedsLogin, CredentialKinds.None), (byId["fresh@example.com"].AuthState, byId["fresh@example.com"].Credentials));
	}

	[TestMethod]
	public void An_expired_access_token_with_a_refresh_token_is_still_signed_in()
	{
		Seed(Registered("expired@example.com", "us", expires: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

		Assert.AreEqual(AuthStates.SignedIn, data.GetAccounts().Single().AuthState);
	}

	[TestMethod]
	public void Account_json_carries_no_token_material()
	{
		Seed(Registered("modern@example.com", "us"));

		var json = JsonSerializer.Serialize(data.GetAccounts(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

		foreach (var secret in new[] { "SECRET_ACCESS", "SECRET_REFRESH", "cookie-value", "store-auth-cookie", "amzn-account", "0123456789abcdef0123" })
			Assert.DoesNotContain(secret, json);
	}

	[TestMethod]
	public void Update_persists_name_and_scan_flag_and_keeps_the_tokens()
	{
		Seed(Registered("a@example.com", "us"));

		var result = data.UpdateAccount("A@example.com", null, "Renamed", false);

		Assert.AreEqual(AccountOutcome.Ok, result.Outcome);
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		var account = persister.AccountsSettings.Accounts.Single();
		Assert.AreEqual("Renamed", account.AccountName);
		Assert.IsFalse(account.LibraryScan);
		Assert.AreEqual(AccountSignInStatus.Modern, account.SignInStatus);
	}

	[TestMethod]
	public void Update_of_one_field_leaves_the_other()
	{
		Seed(Registered("a@example.com", "us"));

		data.UpdateAccount("a@example.com", null, null, false);

		var account = data.GetAccounts().Single();
		Assert.AreEqual("a@example.com name", account.AccountName);
		Assert.IsFalse(account.LibraryScan);
	}

	[TestMethod]
	public void Delete_removes_only_the_account_and_keeps_its_books()
	{
		Seed(Registered("a@example.com", "us"), Registered("b@example.com", "us"));
		using (var context = ApplicationServices.DbContexts.GetContext())
		{
			var book = new DataLayer.Book(new DataLayer.AudibleProductId("B0ACCOUNT01"), "T", "", "D", 60,
				DataLayer.ContentType.Product, [new DataLayer.Contributor("A")], [new DataLayer.Contributor("N")], "us");
			context.LibraryBooks.Add(new DataLayer.LibraryBook(book, new DateTime(2020, 1, 1), "a@example.com"));
			context.SaveChanges();
		}

		var result = data.DeleteAccount("a@example.com", null);

		Assert.AreEqual(AccountOutcome.Ok, result.Outcome);
		Assert.AreEqual("b@example.com", data.GetAccounts().Single().AccountId);
		Assert.IsNotNull(data.GetBook("B0ACCOUNT01"));
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
	}

	[TestMethod]
	public void A_login_shared_by_two_marketplaces_needs_the_locale()
	{
		Seed(Registered("a@example.com", "us"), Registered("a@example.com", "uk"));

		Assert.AreEqual(AccountOutcome.Ambiguous, data.UpdateAccount("a@example.com", null, "x", null).Outcome);
		Assert.AreEqual(AccountOutcome.NotFound, data.UpdateAccount("a@example.com", "germany", "x", null).Outcome);
		Assert.AreEqual(AccountOutcome.NotFound, data.UpdateAccount("a@example.com", "nonsense", "x", null).Outcome);
		Assert.AreEqual(AccountOutcome.Ok, data.UpdateAccount("a@example.com", "uk", "UK one", null).Outcome);
		Assert.AreEqual("UK one", data.GetAccounts().Single(a => a.Locale == "uk").AccountName);
		Assert.AreEqual("a@example.com name", data.GetAccounts().Single(a => a.Locale == "us").AccountName);
	}

	[TestMethod]
	public void Unknown_account_is_not_found()
	{
		Assert.AreEqual(AccountOutcome.NotFound, data.DeleteAccount("nobody@example.com", null).Outcome);
	}

	[TestMethod]
	public void Login_backend_resolves_targets_and_rejects_bad_requests()
	{
		Seed(Registered("a@example.com", "us"), Registered("b@example.com", "uk"));
		var backend = new AudibleLoginBackend();

		var known = backend.Resolve("A@example.com", "us", null);
		Assert.AreEqual(("a@example.com", "us", true), (known.AccountId, known.LocaleName, known.Exists));
		Assert.IsFalse(backend.Resolve("new@example.com", "us", null).Exists);
		Assert.AreEqual("b@example.com", backend.Resolve(null, "uk", null).AccountId);

		Assert.AreEqual(400, Assert.ThrowsExactly<LoginException>(() => backend.Resolve("a@example.com", "nowhere", null)).Status);
		Assert.AreEqual(400, Assert.ThrowsExactly<LoginException>(() => backend.Resolve(null, "germany", null)).Status);
		Assert.AreEqual(400, Assert.ThrowsExactly<LoginException>(() => backend.Resolve("a@example.com", "us", "Nope")).Status);
	}

	[TestMethod]
	public void Discarding_an_unfinished_login_removes_only_accounts_without_tokens()
	{
		Seed(Registered("kept@example.com", "us"), new Account("empty@example.com") { IdentityTokens = new Identity(Localization.Get("us")) });

		ExternalLoginFlow.DiscardUnfinished("kept@example.com", "us");
		ExternalLoginFlow.DiscardUnfinished("empty@example.com", "us");

		CollectionAssert.AreEqual(new[] { "kept@example.com" }, data.GetAccounts().Select(a => a.AccountId).ToArray());
	}
}
