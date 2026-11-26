using DataLayer;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static ApplicationServices.AudiobookshelfProgressSync;
using StubServer = ApplicationServices.Tests.AudiobookshelfProgressPushTests.StubServer;

namespace ApplicationServices.Tests;

[TestClass]
[DoNotParallelize]
public class AudiobookshelfProgressPushServiceTests
{
	private static LibraryBook Book(string asin, string title, string account = "acct")
	{
		var book = new Book(new AudibleProductId(asin), title, "", "", 600, ContentType.Product, [new Contributor("Author")], [new Contributor("Narrator")], "us");
		return new LibraryBook(book, new DateTime(2020, 1, 1), account);
	}

	private static ListeningProgress Position(LibraryBook lb)
		=> new(lb.Book.AudibleProductId, lb.Book.TitleWithSubtitle, null, lb.Account, "us", 250_000, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, false);

	private static AudiblePositionReader Reader(params string[] asinsWithPosition) => (books, _, _)
		=> Task.FromResult(books.Where(b => asinsWithPosition.Contains(b.Book.AudibleProductId)).Select(Position).ToList());

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_returns_one_result_per_book_in_order()
	{
		using var stub = new StubServer();
		var books = new[] { Book("B0NEW", "New"), Book("B0NEWER", "Newer On Server"), Book("B0MISSING", "Missing"), Book("B0NOPOS", "No Position") };
		var connection = new AbsConnection(stub.BaseUrl, "t", "lib_1");

		var results = await AudiobookshelfProgressPush.PushAsync(books, connection, force: false, readPositions: Reader("B0NEW", "B0NEWER", "B0MISSING"));

		CollectionAssert.AreEqual(
			new[] { Outcome.Updated, Outcome.ServerIsNewer, Outcome.NotOnServer, Outcome.NoAudiblePosition },
			results.Select(r => r.Outcome).ToArray());
		CollectionAssert.AreEqual(new[] { "B0NEW", "B0NEWER", "B0MISSING", "B0NOPOS" }, results.Select(r => r.Asin).ToArray());
		Assert.AreEqual(250d, results[0].PositionSeconds);
		Assert.AreEqual("li_new", results[0].AbsItemId);
		Assert.IsTrue(results[0].Pushed);
		Assert.IsTrue(results[1].Skipped && results[1].CanForce);
		Assert.IsFalse(results[2].CanForce);
		Assert.IsTrue(results[3].Skipped);
		Assert.AreEqual(1, stub.Requests.Count(r => r.Method == "PATCH"));
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_force_overwrites_server_newer_progress()
	{
		using var stub = new StubServer();
		var connection = new AbsConnection(stub.BaseUrl, "t", "lib_1");

		var results = await AudiobookshelfProgressPush.PushAsync([Book("B0NEWER", "Newer On Server")], connection, force: true, readPositions: Reader("B0NEWER"));

		Assert.AreEqual(Outcome.Updated, results.Single().Outcome);
		Assert.AreEqual("/api/me/progress/li_newer", stub.Requests.Single(r => r.Method == "PATCH").Path);
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_dry_run_writes_nothing()
	{
		using var stub = new StubServer();
		var connection = new AbsConnection(stub.BaseUrl, "t", "lib_1");

		var results = await AudiobookshelfProgressPush.PushAsync([Book("B0NEW", "New")], connection, force: false, dryRun: true, readPositions: Reader("B0NEW"));

		Assert.AreEqual(Outcome.WouldUpdate, results.Single().Outcome);
		Assert.IsFalse(stub.Requests.Any(r => r.Method == "PATCH"));
	}

	[TestMethod]
	public async Task PushAsync_without_a_library_fails_every_book_without_calling_audible()
	{
		var called = false;
		AudiblePositionReader reader = (_, _, _) => { called = true; return Task.FromResult(new List<ListeningProgress>()); };

		var results = await AudiobookshelfProgressPush.PushAsync([Book("B0NEW", "New")], new AbsConnection("http://x", "t", null), force: false, readPositions: reader);

		Assert.AreEqual(Outcome.Failed, results.Single().Outcome);
		StringAssert.Contains(results.Single().Message, "library");
		Assert.IsFalse(called);
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_reports_books_of_a_failing_account_as_failed_and_still_pushes_the_rest()
	{
		using var stub = new StubServer();
		var books = new[] { Book("B0NEW", "New", "good"), Book("B0NOPOS", "No Position", "bad") };
		AudiblePositionReader reader = (bs, onAccountError, _) =>
		{
			onAccountError(AudibleListeningProgressService.AccountLabel("bad", "us"), new InvalidOperationException("login expired"));
			return Task.FromResult(new List<ListeningProgress> { Position(books[0]) });
		};

		var results = await AudiobookshelfProgressPush.PushAsync(books, new AbsConnection(stub.BaseUrl, "t", "lib_1"), force: false, readPositions: reader);

		Assert.AreEqual(Outcome.Updated, results[0].Outcome);
		Assert.AreEqual(Outcome.Failed, results[1].Outcome);
		StringAssert.Contains(results[1].Message, "login expired");
	}

	[TestMethod]
	public async Task PushAsync_turns_a_reader_exception_into_failed_results()
	{
		AudiblePositionReader reader = (_, _, _) => throw new InvalidOperationException("no network");

		var results = await AudiobookshelfProgressPush.PushAsync([Book("B0NEW", "New")], new AbsConnection("http://x", "t", "lib_1"), force: false, readPositions: reader);

		Assert.AreEqual(Outcome.Failed, results.Single().Outcome);
		StringAssert.Contains(results.Single().Message, "no network");
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_turns_an_unreachable_server_into_failed_results()
	{
		var results = await AudiobookshelfProgressPush.PushAsync([Book("B0NEW", "New")], new AbsConnection("http://127.0.0.1:1", "t", "lib_1"), force: false, readPositions: Reader("B0NEW"));

		Assert.AreEqual(Outcome.Failed, results.Single().Outcome);
	}

	[TestMethod]
	public void AbsConnection_needs_server_url_and_token_but_not_a_library()
	{
		var config = Configuration.CreateMockInstance();
		try
		{
			Assert.IsNull(AbsConnection.From(config));

			config.AudiobookshelfServerUrl = "http://abs";
			Assert.IsNull(AbsConnection.From(config));

			config.AudiobookshelfApiToken = "token";
			var connection = AbsConnection.From(config);
			Assert.IsNotNull(connection);
			Assert.IsNull(connection.LibraryId);

			config.AudiobookshelfLibraryId = "lib";
			Assert.AreEqual("lib", AbsConnection.From(config)!.LibraryId);
		}
		finally
		{
			Configuration.RestoreSingletonInstance();
		}
	}
}
