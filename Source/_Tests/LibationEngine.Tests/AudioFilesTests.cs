using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LibationEngine.Tests;

[TestClass]
public class AudioFilesTests
{
	internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio", name);

	private static string Copy(TempDir dir, string fixture, string name)
	{
		var path = dir.File(name);
		File.Copy(Fixture(fixture), path);
		return path;
	}

	private static IReadOnlyList<BookChapter> Chapters(string cue, params string[] paths)
		=> AudioChapters.Build(AudioFiles.Describe(paths), cue);

	[TestMethod]
	[DataRow("plain.m4b", 2.05)]
	[DataRow("chapters.m4b", 4.04)]
	[DataRow("plain.mp3", 2.0)]
	[DataRow("chapters.mp3", 4.0)]
	[DataRow("cbr.mp3", 2.0)]
	public void Duration_is_read_from_the_file(string fixture, double seconds)
		=> Assert.AreEqual(seconds, AudioFiles.ReadDuration(Fixture(fixture))!.Value, 0.1);

	[TestMethod]
	public void Duration_is_null_for_unreadable_or_unsupported_files()
	{
		using var dir = new TempDir();
		File.WriteAllText(dir.File("broken.m4b"), "not audio at all");
		File.WriteAllText(dir.File("broken.mp3"), "not audio at all");
		File.WriteAllText(dir.File("empty.mp3"), "");
		File.WriteAllText(dir.File("some.flac"), "fLaC");

		foreach (var name in new[] { "broken.m4b", "broken.mp3", "empty.mp3", "some.flac" })
			Assert.IsNull(AudioFiles.ReadDuration(dir.File(name)), name);
	}

	[TestMethod]
	[DataRow("a.m4b", "audio/mp4")]
	[DataRow("a.M4A", "audio/mp4")]
	[DataRow("a.mp4", "audio/mp4")]
	[DataRow("a.mp3", "audio/mpeg")]
	[DataRow("a.ogg", "audio/ogg")]
	[DataRow("a.xyz", "application/octet-stream")]
	public void Content_type_follows_the_extension(string name, string expected)
		=> Assert.AreEqual(expected, AudioFiles.ContentType(name));

	[TestMethod]
	public void Play_order_is_the_natural_order_of_the_primary_files_folder_and_extension()
	{
		var books = Path.Combine(Path.GetTempPath(), "books");
		string[] all =
		[
			Path.Combine(books, "t", "Book - 10.m4b"),
			Path.Combine(books, "t", "Book - 2.m4b"),
			Path.Combine(books, "t", "Book - 1.m4b"),
			Path.Combine(books, "t", "Book - 1.mp3"),
			Path.Combine(books, "other", "Book - 3.m4b")
		];

		var order = AudioFiles.PlayOrder(all[1], all);

		CollectionAssert.AreEqual(new[] { "Book - 1.m4b", "Book - 2.m4b", "Book - 10.m4b" }, order.Select(Path.GetFileName).ToArray());
	}

	[TestMethod]
	public void Describe_reports_name_type_size_and_duration_in_order()
	{
		using var dir = new TempDir();
		var a = Copy(dir, "plain.m4b", "one.m4b");
		var b = Copy(dir, "plain.mp3", "two.mp3");

		var files = AudioFiles.Describe([a, b]);

		Assert.AreEqual(2, files.Count);
		Assert.AreEqual((0, "one.m4b", "audio/mp4", new FileInfo(a).Length), (files[0].Index, files[0].Name, files[0].ContentType, files[0].SizeBytes));
		Assert.AreEqual((1, "two.mp3", "audio/mpeg"), (files[1].Index, files[1].Name, files[1].ContentType));
		Assert.IsNotNull(files[1].DurationSec);
	}

	[TestMethod]
	public void An_m4b_gives_its_embedded_chapters()
	{
		var chapters = Chapters("", Fixture("chapters.m4b"));

		CollectionAssert.AreEqual(new[] { "Opening", "Middle", "Ending" }, chapters.Select(c => c.Title).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 1, 2.5 }, chapters.Select(c => Math.Round(c.StartSec, 2)).ToArray());
		CollectionAssert.AreEqual(new[] { 1d, 1.5, 1.5 }, chapters.Select(c => Math.Round(c.DurationSec!.Value, 2)).ToArray());
		CollectionAssert.AreEqual(new[] { 0, 1, 2 }, chapters.Select(c => c.Index).ToArray());
		Assert.IsTrue(chapters.All(c => c.FileIndex == 0));
	}

	[TestMethod]
	public void An_mp3_gives_its_CHAP_frames()
	{
		var chapters = Chapters("", Fixture("chapters.mp3"));

		CollectionAssert.AreEqual(new[] { "Opening", "Middle", "Ending" }, chapters.Select(c => c.Title).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 1, 2.5 }, chapters.Select(c => c.StartSec).ToArray());
		CollectionAssert.AreEqual(new[] { 1d, 1.5, 1.5 }, chapters.Select(c => c.DurationSec!.Value).ToArray());
	}

	[TestMethod]
	public void An_mp3_with_an_embedded_CUESHEET_gives_those_chapters()
	{
		var chapters = Chapters("", Fixture("cuesheet.mp3"));

		CollectionAssert.AreEqual(new[] { "Opening", "Middle", "Ending" }, chapters.Select(c => c.Title).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 1, 2.49 }, chapters.Select(c => Math.Round(c.StartSec, 2)).ToArray());
		Assert.AreEqual(AudioFiles.ReadDuration(Fixture("cuesheet.mp3"))!.Value - 2.4933, chapters[2].DurationSec!.Value, 0.01);
	}

	[TestMethod]
	public void A_cue_file_next_to_the_book_is_used_when_the_file_has_no_chapters()
	{
		using var dir = new TempDir();
		var book = Copy(dir, "plain.mp3", "Book.mp3");
		var cue = dir.File("Book.cue");
		File.WriteAllText(cue, "FILE \"Book.mp3\" MP3\r\nTRACK 1 AUDIO\r\n  TITLE \"Intro \"quoted\"\"\r\n  INDEX 01 0:00:00\r\nTRACK 2 AUDIO\r\n  TITLE \"Part two\"\r\n  INDEX 01 0:01:37\r\n");

		var chapters = Chapters(cue, book);

		CollectionAssert.AreEqual(new[] { "Intro \"quoted\"", "Part two" }, chapters.Select(c => c.Title).ToArray());
		Assert.AreEqual(1 + 37 / 75d, chapters[1].StartSec, 1e-9);
		Assert.AreEqual(1 + 37 / 75d, chapters[0].DurationSec!.Value, 1e-9);
		Assert.AreEqual(AudioFiles.ReadDuration(book)!.Value - (1 + 37 / 75d), chapters[1].DurationSec!.Value, 1e-9);
	}

	[TestMethod]
	public void A_cue_start_past_an_hour_counts_minutes_beyond_59()
	{
		using var dir = new TempDir();
		var book = Copy(dir, "plain.mp3", "Book.mp3");
		var cue = dir.File("Book.cue");
		File.WriteAllText(cue, "TRACK 1 AUDIO\n  TITLE \"A\"\n  INDEX 01 0:00:00\nTRACK 2 AUDIO\n  TITLE \"B\"\n  INDEX 01 75:30:00\n");

		Assert.AreEqual(75 * 60 + 30, Chapters(cue, book)[1].StartSec);
	}

	[TestMethod]
	public void Embedded_chapters_win_over_a_cue_file()
	{
		using var dir = new TempDir();
		var book = Copy(dir, "chapters.m4b", "Book.m4b");
		var cue = dir.File("Book.cue");
		File.WriteAllText(cue, "TRACK 1 AUDIO\n  TITLE \"From cue\"\n  INDEX 01 0:00:00\n");

		Assert.AreEqual("Opening", Chapters(cue, book)[0].Title);
	}

	[TestMethod]
	public void A_file_with_no_chapters_and_no_cue_is_one_chapter_named_for_the_file()
	{
		using var dir = new TempDir();
		var book = Copy(dir, "plain.m4b", "My Book [B0TEST].m4b");

		foreach (var cue in new[] { null, dir.File("missing.cue") })
		{
			var chapters = AudioChapters.Build(AudioFiles.Describe([book]), cue);

			Assert.AreEqual(1, chapters.Count);
			Assert.AreEqual("My Book [B0TEST]", chapters[0].Title);
			Assert.AreEqual((0d, 0, 0d), (chapters[0].StartSec, chapters[0].FileIndex, chapters[0].FileStartSec));
			Assert.AreEqual(2.05, chapters[0].DurationSec!.Value, 0.1);
		}
	}

	[TestMethod]
	public void An_unparseable_cue_falls_back_to_one_chapter_per_file()
	{
		using var dir = new TempDir();
		var book = Copy(dir, "plain.mp3", "Book.mp3");
		var cue = dir.File("Book.cue");
		File.WriteAllText(cue, "nothing useful here");

		Assert.AreEqual("Book", Chapters(cue, book).Single().Title);
	}

	[TestMethod]
	public void A_split_book_has_one_chapter_per_part_with_cumulative_starts()
	{
		using var dir = new TempDir();
		var one = Copy(dir, "plain.m4b", "Book - 01 - Intro.m4b");
		var two = Copy(dir, "plain.m4b", "Book - 02 - Middle.m4b");
		var three = Copy(dir, "plain.m4b", "Book - 03 - End.m4b");
		var cue = dir.File("Book.cue");
		File.WriteAllText(cue, "TRACK 1 AUDIO\n  TITLE \"ignored\"\n  INDEX 01 0:00:00\n");

		var chapters = Chapters(cue, one, two, three);
		var length = AudioFiles.ReadDuration(one)!.Value;

		CollectionAssert.AreEqual(new[] { "Book - 01 - Intro", "Book - 02 - Middle", "Book - 03 - End" }, chapters.Select(c => c.Title).ToArray());
		CollectionAssert.AreEqual(new[] { 0, 1, 2 }, chapters.Select(c => c.FileIndex).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, length, 2 * length }, chapters.Select(c => c.StartSec).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 0, 0 }, chapters.Select(c => c.FileStartSec).ToArray());
	}

	[TestMethod]
	public void A_split_book_offsets_embedded_chapters_by_the_files_before_them()
	{
		using var dir = new TempDir();
		var one = Copy(dir, "plain.mp3", "Book - 1.mp3");
		var two = Copy(dir, "chapters.mp3", "Book - 2.mp3");
		var length = AudioFiles.ReadDuration(one)!.Value;

		var chapters = Chapters("", one, two);

		CollectionAssert.AreEqual(new[] { "Book - 1", "Opening", "Middle", "Ending" }, chapters.Select(c => c.Title).ToArray());
		CollectionAssert.AreEqual(new[] { 0, 1, 1, 1 }, chapters.Select(c => c.FileIndex).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, 0, 1, 2.5 }, chapters.Select(c => c.FileStartSec).ToArray());
		CollectionAssert.AreEqual(new[] { 0d, length, length + 1, length + 2.5 }, chapters.Select(c => c.StartSec).ToArray());
		CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, chapters.Select(c => c.Index).ToArray());
	}

	[TestMethod]
	public void A_corrupt_file_still_gets_a_chapter_with_no_duration()
	{
		using var dir = new TempDir();
		File.WriteAllText(dir.File("Bad.m4b"), "garbage");

		var chapter = Chapters("", dir.File("Bad.m4b")).Single();

		Assert.AreEqual("Bad", chapter.Title);
		Assert.IsNull(chapter.DurationSec);
	}
}
