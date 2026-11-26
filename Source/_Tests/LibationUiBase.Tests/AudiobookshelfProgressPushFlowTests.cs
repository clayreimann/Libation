using ApplicationServices;
using DataLayer;
using LibationUiBase.Forms;
using LibationUiBase.GridView;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace LibationUiBase.Tests;

[TestClass]
[DoNotParallelize]
public class AudiobookshelfProgressPushFlowTests
{
	private List<(string Message, MessageBoxButtons Buttons)> Dialogs { get; } = [];
	private DialogResult Answer { get; set; } = DialogResult.OK;

	[TestInitialize]
	public void Initialize()
		=> MessageBoxBase.ShowAsyncImpl = (_, message, _, buttons, _, _, _) =>
		{
			Dialogs.Add((message, buttons));
			return Task.FromResult(Answer);
		};

	[TestCleanup]
	public void Cleanup() => MessageBoxBase.ShowAsyncImpl = null!;

	private static LibraryBook Book(string asin, LiberatedStatus status = LiberatedStatus.Liberated)
	{
		var book = new Book(new AudibleProductId(asin), "Title " + asin, "", "", 600, ContentType.Product, [new Contributor("Author")], [new Contributor("Narrator")], "us");
		book.UserDefinedItem.BookStatus = status;
		return new LibraryBook(book, new DateTime(2020, 1, 1), "acct");
	}

	private static BookPushResult Result(LibraryBook lb, Outcome outcome, string message = "")
		=> new(lb.Book.AudibleProductId, lb.Book.TitleWithSubtitle, outcome, message, null, null);

	[TestMethod]
	public async Task Only_liberated_books_are_pushed_and_nothing_happens_when_there_are_none()
	{
		var pushed = new List<string>();
		AudiobookshelfProgressPushFlow.PushBooks push = (books, _) =>
		{
			pushed.AddRange(books.Select(b => b.Book.AudibleProductId));
			return Task.FromResult(books.Select(b => Result(b, Outcome.Updated)).ToList());
		};

		await AudiobookshelfProgressPushFlow.RunAsync([Book("B1", LiberatedStatus.NotLiberated)], push);
		Assert.AreEqual(0, Dialogs.Count);

		await AudiobookshelfProgressPushFlow.RunAsync([Book("B1"), Book("B2", LiberatedStatus.NotLiberated), Book("B3")], push);
		CollectionAssert.AreEqual(new[] { "B1", "B3" }, pushed);
		Assert.AreEqual(1, Dialogs.Count);
		Assert.AreEqual(MessageBoxButtons.OK, Dialogs[0].Buttons);
		StringAssert.Contains(Dialogs[0].Message, "Pushed progress for 2 books");
	}

	[TestMethod]
	public async Task Server_newer_books_offer_force_and_only_those_are_retried_when_accepted()
	{
		var calls = new List<(string[] Asins, bool Force)>();
		AudiobookshelfProgressPushFlow.PushBooks push = (books, force) =>
		{
			calls.Add((books.Select(b => b.Book.AudibleProductId).ToArray(), force));
			return Task.FromResult(books.Select(b => Result(b, force || b.Book.AudibleProductId == "B1" ? Outcome.Updated : Outcome.ServerIsNewer)).ToList());
		};
		Answer = DialogResult.Yes;

		await AudiobookshelfProgressPushFlow.RunAsync([Book("B1"), Book("B2")], push);

		Assert.AreEqual(2, calls.Count);
		CollectionAssert.AreEqual(new[] { "B2" }, calls[1].Asins);
		Assert.IsTrue(calls[1].Force);
		Assert.AreEqual(MessageBoxButtons.YesNo, Dialogs[0].Buttons);
		StringAssert.Contains(Dialogs[0].Message, "Audiobookshelf is newer: 1");
		Assert.AreEqual(MessageBoxButtons.OK, Dialogs[1].Buttons);
	}

	[TestMethod]
	public async Task Declining_force_leaves_the_server_alone()
	{
		var calls = 0;
		AudiobookshelfProgressPushFlow.PushBooks push = (books, _) =>
		{
			calls++;
			return Task.FromResult(books.Select(b => Result(b, Outcome.ServerIsNewer)).ToList());
		};
		Answer = DialogResult.No;

		await AudiobookshelfProgressPushFlow.RunAsync([Book("B1")], push);

		Assert.AreEqual(1, calls);
		Assert.AreEqual(1, Dialogs.Count);
	}

	[TestMethod]
	public async Task A_throwing_push_is_shown_as_failed_instead_of_escaping()
	{
		AudiobookshelfProgressPushFlow.PushBooks push = (_, _) => throw new InvalidOperationException("kaboom");

		await AudiobookshelfProgressPushFlow.RunAsync([Book("B1")], push);

		StringAssert.Contains(Dialogs.Single().Message, "Failed: 1");
		StringAssert.Contains(Dialogs.Single().Message, "kaboom");
	}

	[TestMethod]
	public void Summary_counts_each_skip_reason_and_lists_failures()
	{
		var b = Book("B1");
		var summary = AudiobookshelfProgressPushFlow.BuildSummary(
		[
			Result(b, Outcome.Updated),
			Result(b, Outcome.AlreadyInSync),
			Result(b, Outcome.FinishedOnServer),
			Result(b, Outcome.NotOnServer),
			Result(b, Outcome.NoAudiblePosition),
			Result(b, Outcome.Failed, "HTTP 500"),
		]);

		StringAssert.Contains(summary, "Pushed progress for 1 book.");
		StringAssert.Contains(summary, "Already in sync: 1");
		StringAssert.Contains(summary, "Skipped, finished on Audiobookshelf: 1");
		StringAssert.Contains(summary, "Not found in Audiobookshelf: 1");
		StringAssert.Contains(summary, "No Audible listening position: 1");
		StringAssert.Contains(summary, "- Title B1: HTTP 500");
	}
}
