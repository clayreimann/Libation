using System;
using System.IO;
using System.Linq;
using DataLayer;
using LibationSearchEngine;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Directory = System.IO.Directory;

namespace SearchEngineTests;

[TestClass]
public class NarratorFieldTests
{
	private string indexDirectory = null!;

	[TestInitialize]
	public void Initialize()
	{
		indexDirectory = Path.Combine(Path.GetTempPath(), "LibationSearchEngineTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(indexDirectory);

		var narrated = new Book(new AudibleProductId("B0NARRATED1"), "Plain Title", null, null, 1, ContentType.Product,
			[Contributor.GetEmpty()], [new Contributor("Zebulon Quark")], "us");
		var serial = new Book(new AudibleProductId("B0INSERIES1"), "Another Title", null, null, 1, ContentType.Product,
			[Contributor.GetEmpty()], [Contributor.GetEmpty()], "us");
		serial.UpsertSeries(new Series(new AudibleSeriesId("B0SERIES001"), "Zebulon Chronicles"), "1");

		new SearchEngine(indexDirectory).CreateNewIndex(
		[
			new LibraryBook(narrated, new DateTime(2026, 8, 15), "account"),
			new LibraryBook(serial, new DateTime(2026, 8, 15), "account")
		]);
	}

	[TestCleanup]
	public void Cleanup()
	{
		try { Directory.Delete(indexDirectory, recursive: true); } catch (IOException) { }
	}

	private string[] search(string query)
		=> [.. new SearchEngine(indexDirectory).Search(query).Docs.Select(d => d.ProductId)];

	[TestMethod]
	public void Narrator_matches_narrators_and_not_series_names()
	{
		CollectionAssert.AreEqual(new[] { "B0NARRATED1" }, search("narrator:zebulon"));
		CollectionAssert.AreEqual(new[] { "B0INSERIES1" }, search("series:zebulon"));
	}
}
