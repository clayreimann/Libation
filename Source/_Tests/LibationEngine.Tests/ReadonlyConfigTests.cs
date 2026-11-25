using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class ReadonlyConfigTests
{
	[TestMethod]
	public void Settings_are_copied_and_the_source_is_not_touched()
	{
		using var dir = new TempDir();
		var ro = Directory.CreateDirectory(dir.File("ro")).FullName;
		var state = dir.File("state");
		File.WriteAllText(Path.Combine(ro, "Settings.json"), """{"Books":"/b"}""");
		var before = File.GetLastWriteTimeUtc(Path.Combine(ro, "Settings.json"));

		Assert.IsTrue(ReadonlyConfig.Apply(ro, state));

		Assert.AreEqual("""{"Books":"/b"}""", File.ReadAllText(Path.Combine(state, "Settings.json")));
		Assert.AreEqual(before, File.GetLastWriteTimeUtc(Path.Combine(ro, "Settings.json")));
	}

	[TestMethod]
	public void The_source_always_wins_over_existing_state()
	{
		using var dir = new TempDir();
		var ro = Directory.CreateDirectory(dir.File("ro")).FullName;
		var state = Directory.CreateDirectory(dir.File("state")).FullName;
		File.WriteAllText(Path.Combine(ro, "Settings.json"), "new");
		File.WriteAllText(Path.Combine(state, "Settings.json"), "old");

		ReadonlyConfig.Apply(ro, state);

		Assert.AreEqual("new", File.ReadAllText(Path.Combine(state, "Settings.json")));
		Assert.AreEqual(1, Directory.GetFiles(state).Length);
	}

	[TestMethod]
	public void Missing_source_settings_leaves_state_alone()
	{
		using var dir = new TempDir();
		var ro = Directory.CreateDirectory(dir.File("ro")).FullName;
		Assert.IsFalse(ReadonlyConfig.Apply(ro, dir.File("state")));
	}

	[TestMethod]
	public void The_same_directory_is_rejected()
	{
		using var dir = new TempDir();
		Assert.ThrowsExactly<DaemonConfigException>(() => ReadonlyConfig.Apply(dir.Path, dir.Path + Path.DirectorySeparatorChar));
	}

	[TestMethod]
	public void A_missing_source_directory_is_rejected()
	{
		using var dir = new TempDir();
		Assert.ThrowsExactly<DaemonConfigException>(() => ReadonlyConfig.Apply(dir.File("nope"), dir.File("state")));
	}

	[TestMethod]
	public void Accounts_default_to_the_source_dir_and_reimport_when_it_changes()
	{
		using var dir = new TempDir();
		var ro = Directory.CreateDirectory(dir.File("ro")).FullName;
		var state = dir.File("state");
		const string accounts = """{"Accounts":[{"IdentityTokens":{}}]}""";
		File.WriteAllText(ReadonlyConfig.DefaultAccountsPath(ro), accounts);
		var importer = new AccountsImporter(ReadonlyConfig.DefaultAccountsPath(ro), state);

		Assert.IsTrue(importer.Import());
		File.WriteAllText(Path.Combine(state, "AccountsSettings.json"), "refreshed");
		Assert.IsFalse(importer.Import());
		Assert.AreEqual("refreshed", File.ReadAllText(Path.Combine(state, "AccountsSettings.json")));

		File.WriteAllText(ReadonlyConfig.DefaultAccountsPath(ro), accounts.Replace("{}", """{"k":1}"""));
		Assert.IsTrue(importer.Import());
		StringAssert.Contains(File.ReadAllText(Path.Combine(state, "AccountsSettings.json")), "\"k\"");
	}
}
