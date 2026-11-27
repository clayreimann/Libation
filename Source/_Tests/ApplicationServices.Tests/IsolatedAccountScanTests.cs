using ApplicationServices;
using AudibleApi;
using AudibleApi.Authorization;
using AudibleUtilities;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace IsolatedAccountScanTests;

/// <summary>
/// The daemon scans every account each cycle. One account that needs a new login must not stop the others,
/// and the caller has to be told which account it was.
/// </summary>
[TestClass]
[DoNotParallelize]
public class IsolatedAccountScan
{
	private string _dir = "";

	[TestInitialize]
	public void Init()
	{
		_dir = Path.Combine(Path.GetTempPath(), $"libation-isolated-scan-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_dir);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, _dir);
		Configuration.CreateMockInstance();
	}

	[TestCleanup]
	public void Cleanup()
	{
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, null);
		try { Directory.Delete(_dir, recursive: true); } catch { }
	}

	private static Account NeedsLogin(string id, string? name = null)
		=> new(id) { AccountName = name, IdentityTokens = new Identity(Localization.Get("us")), RequiresReRegistration = true };

	[TestMethod]
	public async Task every_account_that_needs_a_login_is_reported_and_nothing_is_thrown()
	{
		var result = await LibraryCommands.ImportAccountsIsolatedAsync(
			[NeedsLogin("first@example.com", "Orig"), NeedsLogin("second@example.com")], allowInteractiveLogin: false);

		Assert.AreEqual(0, result.TotalCount);
		Assert.IsFalse(result.AnySucceeded);
		CollectionAssert.AreEqual(new[] { "first@example.com", "second@example.com" }, result.Accounts.Select(a => a.AccountId).ToArray());
		Assert.IsTrue(result.Accounts.All(a => a.NeedsLogin && !a.Succeeded));
		Assert.AreEqual("us", result.Accounts[0].Locale);
	}

	[TestMethod]
	public async Task the_old_entry_point_still_throws_so_desktop_auto_scan_can_prompt()
	{
		await Assert.ThrowsAsync<AuthenticationRequiredException>(
			() => LibraryCommands.ImportAccountAsync([NeedsLogin("first@example.com")], allowInteractiveLogin: false));
	}

	[TestMethod]
	public void the_label_is_the_nickname_unless_it_would_reveal_the_login()
	{
		Assert.AreEqual("Orig", AccountScanOutcome.LabelFor(NeedsLogin("first@example.com", "Orig")));

		var defaulted = AccountScanOutcome.LabelFor(NeedsLogin("first@example.com", "first@example.com - us"));
		Assert.IsFalse(defaulted.Contains("first@example.com"), defaulted);

		var unnamed = AccountScanOutcome.LabelFor(NeedsLogin("first@example.com"));
		Assert.IsFalse(unnamed.Contains("first@example.com"), unnamed);
	}
}
