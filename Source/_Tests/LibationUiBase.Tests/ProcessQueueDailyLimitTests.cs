using LibationFileManager;
using LibationUiBase.Forms;
using LibationUiBase.ProcessQueue;
using Microsoft.Extensions.Time.Testing;

namespace LibationUiBase.Tests;

[TestClass]
[DoNotParallelize]
public class ProcessQueueDailyLimitTests : QueueTestBase
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(16);

	private sealed class Harness
	{
		private readonly DateTimeOffset usedAt;
		private volatile IReadOnlyList<DownloadHistoryEntry> history = [];

		public ProcessQueueViewModel Queue { get; }
		public GatedBooks Books { get; }
		public FakeTimeProvider Clock { get; }

		public Harness(Configuration.DailyLimitScope scope, int atOnce)
		{
			var config = Configuration.Instance;
			config.DailyDownloadLimit = scope;
			config.DailyDownloadLimitUnit = Configuration.DailyLimitUnit.Books;
			config.DailyDownloadLimitQuantity = 1;

			(Queue, Books) = NewQueue(atOnce);
			Clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
			usedAt = Clock.GetUtcNow() - TimeSpan.FromHours(1);
			Queue.Clock = Clock;
			Queue.DownloadHistory = _ => history;
		}

		public void Block(bool plus) => history = [new DownloadHistoryEntry(usedAt, "USED", plus, 1)];
		public void Unblock() => history = [];

		/// <summary>
		/// Advances in a loop because the poll timer only exists once the loop has reached its delay,
		/// and an advance that lands before that is lost.
		/// </summary>
		public async Task AdvanceUntil(Func<bool> condition, string description)
		{
			var deadline = DateTime.UtcNow + Patience;
			while (!condition())
			{
				if (DateTime.UtcNow > deadline)
					Assert.Fail($"Timed out after {Patience.TotalSeconds}s advancing the clock until {description}.");
				Clock.Advance(PollInterval);
				await Task.Delay(15);
			}
		}

		public async Task AdvanceSeveralPolls(int polls)
		{
			for (var i = 0; i < polls; i++)
			{
				Clock.Advance(PollInterval);
				await Task.Delay(30);
			}
		}
	}

	[TestMethod]
	public async Task a_book_at_the_limit_is_parked_and_starts_once_capacity_returns()
	{
		var h = new Harness(Configuration.DailyLimitScope.AllBooks, atOnce: 1);
		var a = Book("A", download: true);
		h.Block(plus: false);

		h.Queue.AddToQueue([a]);
		await Eventually(() => a.StatusOverride is not null, "the book to be parked");

		StringAssert.StartsWith(a.StatusOverride, "Daily limit");
		Assert.AreEqual(a.StatusOverride, a.StatusText);
		Assert.AreEqual(0, h.Books.Started.Count);
		Assert.AreEqual(1, Dialogs.Count(DailyDownloadLimitUserMessage.DialogCaption));
		await Eventually(() => Log(h.Queue).Any(m => m.StartsWith("Daily download limit reached.")), "the pause log entry");

		await h.AdvanceSeveralPolls(3);
		Assert.AreEqual(0, h.Books.Started.Count, "The book started while the history still blocked it.");

		h.Unblock();
		await h.AdvanceUntil(() => h.Books.Started.Count == 1, "the book to start");

		Assert.IsNull(a.StatusOverride);
		await Eventually(() => Log(h.Queue).Any(m => m.Contains("capacity is available again")), "the resume log entry");
		Assert.AreEqual(1, Dialogs.Count(DailyDownloadLimitUserMessage.DialogCaption));

		h.Books.Finish("A");
		await RunToCompletion(h.Queue);
	}

	[TestMethod]
	public async Task the_daily_limit_message_is_not_awaited_so_the_queue_resumes_while_it_is_still_open()
	{
		var h = new Harness(Configuration.DailyLimitScope.AllBooks, atOnce: 1);
		var a = Book("A", download: true);
		Dialogs.Respond = caption => caption == DailyDownloadLimitUserMessage.DialogCaption
			? new TaskCompletionSource<DialogResult>().Task
			: null;
		h.Block(plus: false);

		h.Queue.AddToQueue([a]);
		await Eventually(() => a.StatusOverride is not null, "the book to be parked");

		h.Unblock();
		h.Books.Finish("A");
		await h.AdvanceUntil(() => h.Queue.QueueRunner!.IsCompleted, "the queue to finish");

		await RunToCompletion(h.Queue);
		Assert.AreEqual(ProcessBookResult.Success, a.Result);
	}

	[TestMethod]
	public async Task a_second_pause_in_the_same_run_logs_again_but_does_not_repeat_the_message()
	{
		var h = new Harness(Configuration.DailyLimitScope.AllBooks, atOnce: 1);
		ProcessBookViewModel a = Book("A", download: true), b = Book("B", download: true);
		h.Block(plus: false);

		h.Queue.AddToQueue([a, b]);
		await Eventually(() => a.StatusOverride is not null, "A to be parked");

		h.Unblock();
		await h.AdvanceUntil(() => h.Books.Started.Count == 1, "A to start");

		// B is only looked at once A has finished, so the history changes before B reaches the gate.
		h.Block(plus: false);
		h.Books.Finish("A");
		await Eventually(() => b.StatusOverride is not null, "B to be parked");
		Assert.AreEqual(1, Dialogs.Count(DailyDownloadLimitUserMessage.DialogCaption));
		Assert.AreEqual(2, Log(h.Queue).Count(m => m.StartsWith("Daily download limit reached.")));

		h.Unblock();
		h.Books.Finish("B");
		await h.AdvanceUntil(() => h.Queue.QueueRunner!.IsCompleted, "the queue to finish");
	}

	[TestMethod]
	public async Task under_plus_only_an_owned_book_starts_first_and_the_blocked_plus_book_goes_to_the_end()
	{
		var h = new Harness(Configuration.DailyLimitScope.PlusOnly, atOnce: 1);
		ProcessBookViewModel gate = Book("GATE"), plus = Book("PLUS", plus: true, download: true), owned = Book("OWNED", download: true);
		h.Block(plus: true);

		// GATE holds the only slot until both real books are queued and counted, so the loop sees a settled queue.
		h.Queue.AddToQueue([gate]);
		await h.Books.WaitForStarted(1);
		h.Queue.AddToQueue([plus, owned]);
		await Eventually(() => h.Queue.QueuedCount == 2, "both books to be counted as queued");
		h.Books.Finish("GATE");

		await h.Books.WaitForStarted(2);
		CollectionAssert.AreEqual(new[] { "GATE", "OWNED" }, h.Books.Started.ToArray());
		await Eventually(() => Log(h.Queue).Any(m => m.StartsWith("Daily download limit reached for Audible Plus titles.")), "the deferral log entry");
		Assert.AreEqual(0, Dialogs.Count(DailyDownloadLimitUserMessage.DialogCaption), "A deferral is not a pause.");

		h.Books.Finish("OWNED");
		await Eventually(() => plus.StatusOverride is not null, "the Plus book to be parked");
		Assert.AreEqual(2, h.Books.Started.Count);
		Assert.AreEqual(1, Dialogs.Count(DailyDownloadLimitUserMessage.DialogCaption));

		h.Unblock();
		h.Books.Finish("PLUS");
		await h.AdvanceUntil(() => h.Queue.QueueRunner!.IsCompleted, "the queue to finish");
		CollectionAssert.AreEqual(new[] { "GATE", "OWNED", "PLUS" }, h.Books.Started.ToArray());
	}

	[TestMethod]
	public async Task every_blocked_book_in_front_of_an_allowed_one_is_deferred_before_it_starts()
	{
		var h = new Harness(Configuration.DailyLimitScope.PlusOnly, atOnce: 1);
		ProcessBookViewModel gate = Book("GATE");
		ProcessBookViewModel p1 = Book("P1", plus: true, download: true), p2 = Book("P2", plus: true, download: true), owned = Book("OWNED", download: true);
		h.Block(plus: true);

		h.Queue.AddToQueue([gate]);
		await h.Books.WaitForStarted(1);
		h.Queue.AddToQueue([p1, p2, owned]);
		await Eventually(() => h.Queue.QueuedCount == 3, "all three books to be counted as queued");
		h.Books.Finish("GATE");

		await h.Books.WaitForStarted(2);
		Assert.AreEqual("OWNED", h.Books.Started.ToArray()[1]);
		await Eventually(() => Log(h.Queue).Count(m => m.StartsWith("Daily download limit reached for Audible Plus titles.")) == 2, "two deferral log entries");

		h.Unblock();
		h.Books.FinishAll("OWNED", "P1", "P2");
		await h.AdvanceUntil(() => h.Queue.QueueRunner!.IsCompleted, "the queue to finish");
	}

	[TestMethod]
	public async Task when_every_book_is_blocked_the_loop_parks_instead_of_rotating()
	{
		var h = new Harness(Configuration.DailyLimitScope.AllBooks, atOnce: 1);
		ProcessBookViewModel a = Book("A", download: true), b = Book("B", download: true), c = Book("C", download: true);
		h.Block(plus: false);

		h.Queue.AddToQueue([a, b, c]);
		await Eventually(() => a.StatusOverride is not null, "the first book to be parked");

		await h.AdvanceSeveralPolls(5);

		Assert.AreEqual(0, h.Books.Started.Count);
		Assert.IsNotNull(a.StatusOverride);
		Assert.IsNull(b.StatusOverride, "A book behind the parked one was polled, so the loop is rotating.");
		Assert.IsNull(c.StatusOverride);
		Assert.AreEqual(0, Log(h.Queue).Count(m => m.Contains("to the end of the queue")), "A blocked book was deferred although nothing else could start.");
		Assert.AreEqual(1, Dialogs.Count(DailyDownloadLimitUserMessage.DialogCaption));

		h.Unblock();
		h.Books.FinishAll("A", "B", "C");
		await h.AdvanceUntil(() => h.Queue.QueueRunner!.IsCompleted, "the queue to finish");
	}

	[TestMethod]
	public async Task cancelling_while_a_book_is_parked_cancels_it_and_lets_the_loop_finish()
	{
		var h = new Harness(Configuration.DailyLimitScope.AllBooks, atOnce: 1);
		ProcessBookViewModel a = Book("A", download: true), b = Book("B", download: true);
		h.Block(plus: false);

		h.Queue.AddToQueue([a, b]);
		await Eventually(() => a.StatusOverride is not null, "the book to be parked");

		await h.Queue.CancelAllAsync();
		await h.AdvanceUntil(() => h.Queue.QueueRunner!.IsCompleted, "the queue to finish");

		await RunToCompletion(h.Queue);
		Assert.AreEqual(ProcessBookResult.Cancelled, a.Result);
		Assert.AreEqual(ProcessBookStatus.Cancelled, a.Status);
		Assert.IsNull(a.StatusOverride);
		CollectionAssert.Contains(h.Queue.Queue.Completed.ToList(), a);
		Assert.AreEqual(0, h.Books.Started.Count);
		Assert.IsFalse(h.Queue.ProgressBarVisible);
	}

	[TestMethod]
	public async Task a_pdf_only_book_is_not_held_by_the_limit()
	{
		var h = new Harness(Configuration.DailyLimitScope.AllBooks, atOnce: 1);
		var pdf = Book("PDF");
		pdf.AddDownloadPdf();
		Assert.IsFalse(pdf.IncludesBookDownload);
		h.Block(plus: false);

		h.Queue.AddToQueue([pdf]);
		await h.Books.WaitForStarted(1);

		Assert.IsNull(pdf.StatusOverride);
		Assert.AreEqual(0, Dialogs.Count(DailyDownloadLimitUserMessage.DialogCaption));
		h.Books.Finish("PDF");
		await RunToCompletion(h.Queue);
	}
}
