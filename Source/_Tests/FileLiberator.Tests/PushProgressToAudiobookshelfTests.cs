using System;
using System.Threading.Tasks;
using ApplicationServices;
using DataLayer;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace FileLiberator.Tests;

[TestClass]
public class PushProgressToAudiobookshelfTests
{
	private static LibraryBook Book(LiberatedStatus status)
	{
		var book = new Book(new AudibleProductId("B0PUSHSTEP1"), "Step", "", "", 600, ContentType.Product, [new Contributor("a")], [new Contributor("n")], "us");
		book.UserDefinedItem.BookStatus = status;
		return new LibraryBook(book, new DateTime(2020, 1, 1), "acct");
	}

	private static PushProgressToAudiobookshelf Step(Func<LibraryBook, bool, Task<BookPushResult>> pusher, bool force = false)
		=> new() { Configuration = Configuration.CreateMockInstance(), Force = force, Pusher = pusher };

	[TestMethod]
	public async Task Passes_force_reports_the_result_and_succeeds()
	{
		var seenForce = false;
		var step = Step((lb, force) =>
		{
			seenForce = force;
			return Task.FromResult(new BookPushResult("B0PUSHSTEP1", "Step", Outcome.ServerIsNewer, "newer", null, null));
		}, force: true);
		BookPushResult? reported = null;
		step.ResultDetermined += (_, r) => reported = r;

		var status = await step.ProcessAsync(Book(LiberatedStatus.Liberated));

		Assert.IsTrue(seenForce);
		Assert.AreEqual(Outcome.ServerIsNewer, reported!.Outcome);
		Assert.IsFalse(status.HasErrors);
	}

	[TestMethod]
	public async Task A_throwing_pusher_is_reported_as_failed_and_still_succeeds()
	{
		var step = Step((_, _) => throw new InvalidOperationException("kaput"));
		BookPushResult? reported = null;
		step.ResultDetermined += (_, r) => reported = r;

		var status = await step.ProcessAsync(Book(LiberatedStatus.Liberated));

		Assert.IsTrue(reported!.Failed);
		StringAssert.Contains(reported.Message, "kaput");
		Assert.IsFalse(status.HasErrors);
	}

	[TestMethod]
	public void Validate_needs_a_server_and_a_liberated_book()
	{
		var config = Configuration.CreateMockInstance();
		var step = new PushProgressToAudiobookshelf { Configuration = config };
		Assert.IsFalse(step.Validate(Book(LiberatedStatus.Liberated)));

		config.AudiobookshelfServerUrl = "https://abs.example.com";
		config.AudiobookshelfApiToken = "token";
		Assert.IsTrue(step.Validate(Book(LiberatedStatus.Liberated)));
		Assert.IsFalse(step.Validate(Book(LiberatedStatus.NotLiberated)));
	}
}
