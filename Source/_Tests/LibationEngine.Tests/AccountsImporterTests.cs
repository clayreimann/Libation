using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class AccountsImporterTests
{
	private const string Valid = """{"Accounts":[{"AccountId":"a","IdentityTokens":{"x":1}}]}""";

	private static (AccountsImporter importer, string import, string live, string hash) Arrange(TempDir dir)
	{
		var import = dir.File("vault.json");
		var files = Directory.CreateDirectory(dir.File("files")).FullName;
		return (new AccountsImporter(import, files), import,
			Path.Combine(files, AccountsImporter.AccountsFileName), Path.Combine(files, AccountsImporter.HashFileName));
	}

	[TestMethod]
	public void Missing_import_file_is_a_noop()
	{
		using var dir = new TempDir();
		var (importer, _, live, hash) = Arrange(dir);
		File.WriteAllText(live, "keep");

		Assert.IsFalse(importer.Import());
		Assert.AreEqual("keep", File.ReadAllText(live));
		Assert.IsFalse(File.Exists(hash));
	}

	[TestMethod]
	public void No_import_path_is_a_noop()
	{
		using var dir = new TempDir();
		Assert.IsFalse(new AccountsImporter(null, dir.Path).Import());
	}

	[TestMethod]
	public void New_file_is_copied_and_hash_recorded()
	{
		using var dir = new TempDir();
		var (importer, import, live, hash) = Arrange(dir);
		File.WriteAllText(import, Valid);

		Assert.IsTrue(importer.Import());
		Assert.AreEqual(Valid, File.ReadAllText(live));
		Assert.AreEqual(64, File.ReadAllText(hash).Trim().Length);
	}

	[TestMethod]
	public void Unchanged_hash_does_not_copy()
	{
		using var dir = new TempDir();
		var (importer, import, live, _) = Arrange(dir);
		File.WriteAllText(import, Valid);
		importer.Import();
		File.WriteAllText(live, "refreshed tokens");

		Assert.IsFalse(importer.Import());
		Assert.AreEqual("refreshed tokens", File.ReadAllText(live));
	}

	[TestMethod]
	public void Changed_hash_copies_again()
	{
		using var dir = new TempDir();
		var (importer, import, live, hash) = Arrange(dir);
		File.WriteAllText(import, Valid);
		importer.Import();
		var firstHash = File.ReadAllText(hash);
		var changed = Valid.Replace("\"a\"", "\"b\"");
		File.WriteAllText(import, changed);

		Assert.IsTrue(importer.Import());
		Assert.AreEqual(changed, File.ReadAllText(live));
		Assert.AreNotEqual(firstHash, File.ReadAllText(hash));
	}

	[TestMethod]
	[DataRow("not json")]
	[DataRow("[]")]
	[DataRow("{}")]
	[DataRow("""{"Accounts":[]}""")]
	[DataRow("""{"Accounts":[{"AccountId":"a"}]}""")]
	[DataRow("""{"Accounts":[{"IdentityTokens":"x"}]}""")]
	[DataRow("""{"Accounts":[{"IdentityTokens":{}},{"AccountId":"b"}]}""")]
	public void Bad_shape_throws_and_changes_nothing(string json)
	{
		using var dir = new TempDir();
		var (importer, import, live, hash) = Arrange(dir);
		File.WriteAllText(import, json);

		var ex = Assert.ThrowsExactly<AccountsImportException>(() => importer.Import());
		StringAssert.Contains(ex.Message, "IdentityTokens");
		Assert.IsFalse(File.Exists(live));
		Assert.IsFalse(File.Exists(hash));
	}

	[TestMethod]
	public void Copy_leaves_no_temp_files()
	{
		using var dir = new TempDir();
		var (importer, import, _, _) = Arrange(dir);
		File.WriteAllText(import, Valid);
		importer.Import();

		CollectionAssert.AreEquivalent(
			new[] { AccountsImporter.AccountsFileName, AccountsImporter.HashFileName },
			Directory.GetFiles(dir.File("files")).Select(Path.GetFileName).ToArray());
	}
}
