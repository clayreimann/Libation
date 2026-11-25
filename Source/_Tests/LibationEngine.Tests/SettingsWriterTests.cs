using System.Text.Json;
using LibationEngine;
using LibationEngine.Contracts;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace LibationEngine.Tests;

[TestClass]
[DoNotParallelize]
public class SettingsWriterTests
{
	private string dir = "";
	private string file = "";
	private Configuration config = null!;

	[TestInitialize]
	public void Init()
	{
		dir = Directory.CreateTempSubdirectory("libation-settings-").FullName;
		file = Path.Combine(dir, "Settings.json");
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, dir);
		Environment.SetEnvironmentVariable(SettingsWriter.BooksEnvironmentVariable, null);
		File.WriteAllText(file, new JObject
		{
			["Books"] = Path.Combine(dir, "Books"),
			["Unrelated"] = "keep me",
			["Serilog"] = new JObject { ["MinimumLevel"] = "Information" },
		}.ToString());
		config = Configuration.Instance;
		config.LoadPersistentSettings(file);
	}

	[TestCleanup]
	public void Cleanup()
	{
		config.LoadEphemeralSettings([]);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, null);
		Environment.SetEnvironmentVariable(SettingsWriter.BooksEnvironmentVariable, null);
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	private SettingsUpdateResult Apply(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return SettingsWriter.Apply(config, doc.RootElement);
	}

	private JObject OnDisk() => JObject.Parse(File.ReadAllText(file));

	private static string Err(SettingsUpdateResult r, string key)
	{
		Assert.IsFalse(r.Ok);
		Assert.IsTrue(r.Errors!.TryGetValue(key, out var message), $"no error for {key}: {string.Join(", ", r.Errors.Keys)}");
		return message;
	}

	[TestMethod]
	public void A_partial_patch_is_persisted_and_leaves_other_keys_alone()
	{
		var result = Apply("""{"download":{"createCueSheet":true},"mp3":{"bitrate":96},"logLevel":"Debug"}""");

		Assert.IsTrue(result.Ok);
		Assert.IsTrue(result.Settings!.Download.CreateCueSheet);
		Assert.AreEqual(96, result.Settings.Mp3.Bitrate);
		Assert.AreEqual("Debug", result.Settings.LogLevel);
		var disk = OnDisk();
		Assert.AreEqual("keep me", (string?)disk["Unrelated"]);
		Assert.AreEqual("Debug", (string?)disk["Serilog"]!["MinimumLevel"]);
		Assert.AreEqual(true, (bool?)disk["CreateCueSheet"]);
		Assert.AreEqual(96, (int?)disk["LameBitrate"]);
		Assert.AreEqual(Path.Combine(dir, "Books"), (string?)disk["Books"]);
	}

	[TestMethod]
	public void An_empty_patch_changes_nothing()
	{
		var before = File.ReadAllText(file);

		Assert.IsTrue(Apply("{}").Ok);

		Assert.AreEqual(before, File.ReadAllText(file));
	}

	[TestMethod]
	public void One_invalid_key_rejects_the_whole_patch()
	{
		var books = Path.Combine(dir, "NewBooks");
		var before = File.ReadAllText(file);

		var result = Apply($$$"""{"books":{{{JsonSerializer.Serialize(books)}}},"download":{"createCueSheet":true},"mp3":{"bitrate":5},"naming":{"fileTemplate":""}}""");

		Assert.IsNotNull(Err(result, "mp3.bitrate"));
		Assert.IsNotNull(Err(result, "naming.fileTemplate"));
		Assert.AreEqual(before, File.ReadAllText(file));
		Assert.IsFalse(Directory.Exists(books));
		Assert.IsFalse(config.CreateCueSheet);
	}

	[TestMethod]
	public void Unknown_null_and_wrongly_typed_values_are_field_errors()
	{
		var result = Apply("""{"bogus":1,"library":{"importEpisodes":null,"nope":true},"download":"x"}""");

		Assert.AreEqual("Unknown setting.", Err(result, "bogus"));
		Assert.AreEqual("Unknown setting.", Err(result, "library.nope"));
		Assert.AreEqual("Must not be null.", Err(result, "library.importEpisodes"));
		Assert.IsNotNull(Err(result, "download"));
	}

	[TestMethod]
	public void A_wrongly_typed_value_is_a_field_error()
	{
		Assert.IsNotNull(Err(Apply("""{"mp3":{"bitrate":"fast"}}"""), "mp3.bitrate"));
	}

	[TestMethod]
	public void A_body_that_is_not_an_object_is_rejected()
	{
		var result = Apply("[1]");

		Assert.IsFalse(result.Ok);
		Assert.AreNotEqual(0, result.Errors!.Count);
	}

	[TestMethod]
	public void Templates_are_validated_like_the_desktop_editor()
	{
		var good = Apply("""{"naming":{"fileTemplate":"<title>","folderTemplate":"<first author>\\<title>"}}""");
		Assert.IsTrue(good.Ok, string.Join("; ", good.Errors?.Values ?? []));
		Assert.AreEqual("<title>", (string?)OnDisk()["FileTemplate"]);

		Assert.IsNotNull(Err(Apply("""{"naming":{"fileTemplate":""}}"""), "naming.fileTemplate"));
		Assert.IsNotNull(Err(Apply($$$"""{"naming":{"folderTemplate":{{{JsonSerializer.Serialize(Path.Combine(dir, "<title>"))}}}}}"""), "naming.folderTemplate"));
		Assert.IsNotNull(Err(Apply("""{"naming":{"fileTemplate":""}}"""), "naming.fileTemplate"));
		Assert.AreEqual("<title>", (string?)OnDisk()["FileTemplate"]);
	}

	[TestMethod]
	public void A_new_books_folder_is_created_only_when_everything_validates()
	{
		var books = Path.Combine(dir, "Library", "Books");

		var result = Apply($$"""{"books":{{JsonSerializer.Serialize(books)}}}""");

		Assert.IsTrue(result.Ok);
		Assert.IsTrue(Directory.Exists(books));
		Assert.AreEqual(books, result.Settings!.Books);
		Assert.IsTrue(result.Settings.Folders.Books.Exists);
		Assert.IsTrue(result.Settings.Folders.Books.Writable);
	}

	[TestMethod]
	[DataRow("relative/books")]
	[DataRow("")]
	public void Books_must_be_an_absolute_path(string path)
	{
		var result = Apply($$"""{"books":{{JsonSerializer.Serialize(path)}}}""");

		Assert.IsNotNull(Err(result, "books"));
	}

	[TestMethod]
	public void Books_cannot_be_a_file()
	{
		var path = Path.Combine(dir, "afile");
		File.WriteAllText(path, "x");

		Assert.IsNotNull(Err(Apply($$"""{"books":{{JsonSerializer.Serialize(path)}}}"""), "books"));
	}

	[TestMethod]
	public void Books_is_locked_while_the_environment_variable_is_set()
	{
		Environment.SetEnvironmentVariable(SettingsWriter.BooksEnvironmentVariable, Path.Combine(dir, "Env"));

		var result = Apply($$"""{"books":{{JsonSerializer.Serialize(Path.Combine(dir, "Other"))}}}""");

		StringAssert.Contains(Err(result, "books"), SettingsWriter.BooksEnvironmentVariable);
	}

	[TestMethod]
	public void A_null_in_progress_resets_to_the_default()
	{
		var custom = Path.Combine(dir, "Temp");
		Assert.IsTrue(Apply($$"""{"inProgress":{{JsonSerializer.Serialize(custom)}}}""").Ok);
		Assert.AreEqual(custom, config.InProgress);

		var result = Apply("""{"inProgress":null}""");

		Assert.IsTrue(result.Ok);
		Assert.AreEqual(Configuration.WinTemp, result.Settings!.InProgress);
	}

	[TestMethod]
	public void Fixup_dependent_options_require_allow_libation_fixup()
	{
		Assert.IsTrue(Apply("""{"download":{"allowLibationFixup":false}}""").Ok);

		Assert.IsNotNull(Err(Apply("""{"download":{"splitFilesByChapter":true}}"""), "download.splitFilesByChapter"));
		Assert.IsFalse(config.SplitFilesByChapter);
		Assert.IsTrue(Apply("""{"download":{"allowLibationFixup":true,"splitFilesByChapter":true}}""").Ok);
		Assert.IsTrue(config.SplitFilesByChapter);

		var off = Apply("""{"download":{"allowLibationFixup":false}}""");
		Assert.IsTrue(off.Ok);
		Assert.IsFalse(off.Settings!.Download.SplitFilesByChapter);
	}

	[TestMethod]
	public void Enums_are_case_insensitive_and_reject_unknown_values()
	{
		Assert.IsTrue(Apply("""{"queue":{"badBook":"ignore"}}""").Ok);
		Assert.IsNotNull(Err(Apply("""{"queue":{"badBook":"7"}}"""), "queue.badBook"));
		Assert.IsNotNull(Err(Apply("""{"logLevel":"Loud"}"""), "logLevel"));
		Assert.IsNotNull(Err(Apply("""{"queue":{"dailyDownloadLimitQuantity":0}}"""), "queue.dailyDownloadLimitQuantity"));
	}

	[TestMethod]
	public void Replacement_characters_round_trip_and_reject_bad_entries()
	{
		var current = SettingsMapper.From(config).ReplacementCharacters;
		var edited = current.Select(r => r.Mandatory ? new { r.Character, Replacement = r.Replacement, r.Description } : new { r.Character, Replacement = "_", r.Description }).ToList();
		var body = JsonSerializer.Serialize(new { replacementCharacters = edited }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

		var result = Apply(body);

		Assert.IsTrue(result.Ok, string.Join("; ", result.Errors?.Values ?? []));
		CollectionAssert.AreEqual(edited.Select(e => e.Replacement).ToArray(), result.Settings!.ReplacementCharacters.Select(r => r.Replacement).ToArray());

		Assert.IsNotNull(Err(Apply("""{"replacementCharacters":[]}"""), "replacementCharacters"));
	}

	[TestMethod]
	public void The_response_never_carries_secrets()
	{
		var json = JsonSerializer.Serialize(Apply("""{"download":{"createCueSheet":true}}""").Settings);

		foreach (var word in new[] { "token", "password", "secret", "apikey" })
			Assert.IsFalse(json.Contains(word, StringComparison.OrdinalIgnoreCase), word);
	}
}
