using DataLayer;
using LibationFileManager;
using LibationUiBase.Forms;
using LibationUiBase.ProcessQueue;
using System.Collections.Concurrent;

namespace LibationUiBase.Tests;

[TestClass]
[DoNotParallelize]
public class ProcessQueueResultTests : QueueTestBase
{
	[TestMethod]
	public async Task a_validation_failure_removes_the_row_from_the_queue()
	{
		var (queue, books) = NewQueue(atOnce: 1);
		ProcessBookViewModel a = Book("A"), b = Book("B");

		queue.AddToQueue([a, b]);
		await books.WaitForStarted(1);
		books.Finish("A", ProcessBookResult.ValidationFail);
		await books.WaitForStarted(2);
		books.Finish("B");

		await RunToCompletion(queue);
		CollectionAssert.DoesNotContain(queue.Queue.Completed.ToList(), a);
		CollectionAssert.Contains(queue.Queue.Completed.ToList(), b);
		Assert.AreEqual(1, queue.Queue.Count);
	}

	[TestMethod]
	[DataRow(ProcessBookResult.Success)]
	[DataRow(ProcessBookResult.Cancelled)]
	[DataRow(ProcessBookResult.FailedRetry)]
	[DataRow(ProcessBookResult.FailedAbort)]
	[DataRow(ProcessBookResult.LicenseDenied)]
	[DataRow(ProcessBookResult.LicenseDeniedPossibleOutage)]
	[DataRow(ProcessBookResult.LicenseDeniedThrottled)]
	[DataRow(ProcessBookResult.LicenseDeniedThrottledRecovered)]
	[DataRow(ProcessBookResult.WidevineRecommended)]
	public async Task every_result_other_than_a_validation_failure_leaves_the_row_in_completed(ProcessBookResult result)
	{
		var (queue, books) = NewQueue(atOnce: 1);
		var a = Book("A");

		queue.AddToQueue([a]);
		await books.WaitForStarted(1);
		books.Finish("A", result);

		await RunToCompletion(queue);
		CollectionAssert.Contains(queue.Queue.Completed.ToList(), a);
		Assert.AreEqual(1, queue.Queue.Count);
	}

	[TestMethod]
	public async Task a_skipped_book_is_marked_as_an_error_in_the_library()
	{
		var (queue, books) = NewQueue(atOnce: 1);
		var a = PersistedBook("Skipped Book");
		Assert.AreEqual(LiberatedStatus.NotLiberated, StoredStatus(a));

		queue.AddToQueue([a]);
		await books.WaitForStarted(1);
		books.Finish(GatedBooks.Id(a), ProcessBookResult.FailedSkip);

		await RunToCompletion(queue);
		Assert.AreEqual(LiberatedStatus.Error, StoredStatus(a));
		CollectionAssert.Contains(queue.Queue.Completed.ToList(), a);
	}

	[TestMethod]
	public async Task a_retry_failure_completes_the_row_and_the_queue_moves_on_to_the_next_book()
	{
		var (queue, books) = NewQueue(atOnce: 1);
		ProcessBookViewModel a = Book("A"), b = Book("B");

		queue.AddToQueue([a, b]);
		await books.WaitForStarted(1);
		books.Finish("A", ProcessBookResult.FailedRetry);

		// Nothing re-queues A: the GUI loop has no retry or backoff, "retry" only means a later request may try again.
		await books.WaitForStarted(2);
		books.Finish("B");

		await RunToCompletion(queue);
		CollectionAssert.AreEqual(new[] { "A", "B" }, books.Started.ToArray());
		Assert.AreEqual(ProcessBookResult.FailedRetry, a.Result);
		Assert.AreEqual(ProcessBookResult.Success, b.Result);
		Assert.AreEqual(2, queue.Queue.Completed.Count);
	}

	private static Func<ProcessBookViewModel, Task<ProcessBookResult>> Scripted(
		ConcurrentQueue<string> started,
		Func<string, int, ProcessBookResult> outcome)
	{
		var calls = new ConcurrentDictionary<string, int>();
		return book =>
		{
			var id = GatedBooks.Id(book);
			started.Enqueue(id);
			var result = outcome(id, calls.AddOrUpdate(id, 1, (_, n) => n + 1));
			book.Result = result;
			return Task.FromResult(result);
		};
	}

	[TestMethod]
	public async Task a_disk_full_result_pauses_the_queue_and_resuming_runs_every_book_again()
	{
		Dialogs.Respond = _ => Task.FromResult(DialogResult.Yes);
		var queue = new ProcessQueueViewModel { MaxConcurrentDownloads = 1, MachineCeilingOverride = 1 };
		var started = new ConcurrentQueue<string>();
		queue.ProcessBookHandler = Scripted(started, (id, call) => id == "A" && call == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);
		ProcessBookViewModel a = Book("A"), b = Book("B"), c = Book("C");

		queue.AddToQueue([a, b, c]);
		await RunToCompletion(queue);

		CollectionAssert.AreEqual(new[] { "A", "B", "C", "A" }, started.ToArray());
		Assert.AreEqual(1, Dialogs.Count(DiskFullUserMessage.DialogCaption));
		Assert.AreEqual(3, queue.Queue.Completed.Count);
		Assert.IsTrue(queue.Queue.Completed.All(x => x.Result == ProcessBookResult.Success));
	}

	[TestMethod]
	public async Task stopping_at_a_disk_full_pause_clears_the_queue_without_starting_another_book()
	{
		Dialogs.Respond = _ => Task.FromResult(DialogResult.No);
		var queue = new ProcessQueueViewModel { MaxConcurrentDownloads = 1, MachineCeilingOverride = 1 };
		var started = new ConcurrentQueue<string>();
		queue.ProcessBookHandler = Scripted(started, (_, _) => ProcessBookResult.DiskFull);

		queue.AddToQueue([Book("A"), Book("B"), Book("C")]);
		await RunToCompletion(queue);

		Assert.AreEqual(1, started.Count, "A queued book started after the disk filled.");
		Assert.AreEqual(0, queue.Queue.Count);
		Assert.IsFalse(queue.ProgressBarVisible);
		Assert.AreEqual(1, Dialogs.Count(DiskFullUserMessage.DialogCaption));
	}

	[TestMethod]
	public async Task the_disk_full_dialog_is_shown_once_when_several_books_fill_the_disk()
	{
		Dialogs.Respond = _ => Task.FromResult(DialogResult.Yes);
		var queue = new ProcessQueueViewModel { MaxConcurrentDownloads = 2, MachineCeilingOverride = 2 };
		var started = new ConcurrentQueue<string>();
		var scripted = Scripted(started, (_, call) => call == 1 ? ProcessBookResult.DiskFull : ProcessBookResult.Success);
		queue.ProcessBookHandler = async book =>
		{
			var result = scripted(book);
			// Both books are in flight before either reports the full disk.
			await QueueTestBase.Eventually(() => started.Count >= 2, "both books to start");
			return await result;
		};

		queue.AddToQueue([Book("A"), Book("B")]);
		await RunToCompletion(queue);

		Assert.AreEqual(1, Dialogs.Count(DiskFullUserMessage.DialogCaption));
		Assert.AreEqual(2, queue.Queue.Completed.Count);
	}

	[TestMethod]
	public async Task a_skipped_bad_book_answered_in_the_dialog_is_marked_as_an_error_and_the_queue_moves_on()
	{
		var queue = new ProcessQueueViewModel { MaxConcurrentDownloads = 1, MachineCeilingOverride = 1 };
		var started = new ConcurrentQueue<string>();
		queue.ProcessBookHandler = Scripted(started, (id, _) => id == "A" ? ProcessBookResult.AwaitingDecision : ProcessBookResult.Success);
		BadBookActionDialogBase.ShowAsyncImpl = (_, _, _) => Task.FromResult(new BadBookDialogResult(DialogResult.Ignore, false, false));
		var a = PersistedBook("Bad One");
		var persistedId = GatedBooks.Id(a);
		queue.ProcessBookHandler = Scripted(started, (id, _) => id == persistedId ? ProcessBookResult.AwaitingDecision : ProcessBookResult.Success);

		queue.AddToQueue([a, Book("B")]);
		await RunToCompletion(queue);

		Assert.AreEqual(LiberatedStatus.Error, StoredStatus(a));
		Assert.AreEqual(ProcessBookResult.FailedSkip, a.Result);
		CollectionAssert.AreEqual(new[] { persistedId, "B" }, started.ToArray());
	}

	[TestMethod]
	public async Task apply_to_all_in_the_bad_book_dialog_answers_every_waiting_book_with_one_dialog()
	{
		var queue = new ProcessQueueViewModel { MaxConcurrentDownloads = 3, MachineCeilingOverride = 3 };
		var started = new ConcurrentQueue<string>();
		queue.ProcessBookHandler = Scripted(started, (_, _) => ProcessBookResult.AwaitingDecision);
		var dialogs = 0;
		BadBookActionDialogBase.ShowAsyncImpl = (_, _, _) =>
		{
			Interlocked.Increment(ref dialogs);
			return Task.FromResult(new BadBookDialogResult(DialogResult.Retry, true, false));
		};
		ProcessBookViewModel a = Book("A"), b = Book("B"), c = Book("C");

		queue.AddToQueue([a, b, c]);
		await RunToCompletion(queue);

		Assert.AreEqual(1, dialogs);
		Assert.IsTrue(new[] { a, b, c }.All(x => x.Result == ProcessBookResult.FailedRetry));
	}

	[TestMethod]
	public async Task the_license_notice_is_shown_once_per_run_for_throttled_denials()
	{
		var (queue, books) = NewQueue(atOnce: 2);

		queue.AddToQueue([Book("A"), Book("B")]);
		await books.WaitForStarted(2);
		books.Finish("A", ProcessBookResult.LicenseDeniedThrottled);
		books.Finish("B", ProcessBookResult.LicenseDeniedThrottled);

		await RunToCompletion(queue);
		Assert.AreEqual(1, Dialogs.Count(ContentLicenseDeniedUserMessage.DialogCaption));
		Assert.AreEqual(1, Dialogs.Captions.Length);
	}

	[TestMethod]
	public async Task every_kind_of_license_guidance_shares_one_notice_per_run()
	{
		var (queue, books) = NewQueue(atOnce: 3);

		queue.AddToQueue([Book("A"), Book("B"), Book("C")]);
		await books.WaitForStarted(3);
		books.Finish("A", ProcessBookResult.LicenseDeniedThrottled);
		books.Finish("B", ProcessBookResult.LicenseDeniedPossibleOutage);
		books.Finish("C", ProcessBookResult.LicenseDeniedThrottledRecovered);

		await RunToCompletion(queue);
		Assert.AreEqual(1, Dialogs.Count(ContentLicenseDeniedUserMessage.DialogCaption));
	}

	[TestMethod]
	public async Task the_widevine_notice_is_shown_once_per_run()
	{
		var (queue, books) = NewQueue(atOnce: 2);

		queue.AddToQueue([Book("A"), Book("B")]);
		await books.WaitForStarted(2);
		books.Finish("A", ProcessBookResult.WidevineRecommended);
		books.Finish("B", ProcessBookResult.WidevineRecommended);

		await RunToCompletion(queue);
		Assert.AreEqual(1, Dialogs.Count(WidevineRecommendationUserMessage.DialogCaption));
		Assert.AreEqual(1, Dialogs.Captions.Length);
	}

	[TestMethod]
	public async Task a_license_denial_on_an_owned_book_shows_nothing()
	{
		var (queue, books) = NewQueue(atOnce: 1);

		queue.AddToQueue([Book("A", plus: false)]);
		await books.WaitForStarted(1);
		books.Finish("A", ProcessBookResult.LicenseDenied);

		await RunToCompletion(queue);
		Assert.AreEqual(0, Dialogs.Captions.Length);
	}

	[TestMethod]
	public async Task a_license_denial_on_a_plus_book_shows_the_license_notice()
	{
		var (queue, books) = NewQueue(atOnce: 1);

		queue.AddToQueue([Book("A", plus: true)]);
		await books.WaitForStarted(1);
		books.Finish("A", ProcessBookResult.LicenseDenied);

		await RunToCompletion(queue);
		Assert.AreEqual(1, Dialogs.Count(ContentLicenseDeniedUserMessage.DialogCaption));
	}

	[TestMethod]
	public async Task the_once_per_run_notices_are_shown_again_on_the_next_run()
	{
		var (queue, books) = NewQueue(atOnce: 1);
		ProcessBookViewModel a = Book("A"), b = Book("B"), c = Book("C"), d = Book("D");

		queue.AddToQueue([a]);
		await books.WaitForStarted(1);
		books.Finish("A", ProcessBookResult.LicenseDeniedThrottled);
		await RunToCompletion(queue);
		Assert.AreEqual(1, Dialogs.Count(ContentLicenseDeniedUserMessage.DialogCaption));

		queue.AddToQueue([b, c]);
		await books.WaitForStarted(2);
		books.Finish("B", ProcessBookResult.LicenseDeniedThrottled);
		await books.WaitForStarted(3);
		books.Finish("C", ProcessBookResult.LicenseDeniedThrottled);
		await RunToCompletion(queue);

		Assert.AreEqual(2, Dialogs.Count(ContentLicenseDeniedUserMessage.DialogCaption));

		queue.AddToQueue([d]);
		await books.WaitForStarted(4);
		books.Finish("D", ProcessBookResult.LicenseDeniedThrottled);
		await RunToCompletion(queue);
		Assert.AreEqual(3, Dialogs.Count(ContentLicenseDeniedUserMessage.DialogCaption));
	}

	[TestMethod]
	public async Task process_start_and_end_fire_once_for_each_book_that_starts()
	{
		var (queue, books) = NewQueue(atOnce: 1);
		ProcessBookViewModel a = Book("A"), b = Book("B"), c = Book("C");
		var starts = new List<ProcessBookViewModel>();
		var ends = new List<ProcessBookViewModel>();
		queue.ProcessStart += (_, book) => { lock (starts) starts.Add(book); };
		queue.ProcessEnd += (_, book) => { lock (ends) ends.Add(book); };

		queue.AddToQueue([a, b, c]);
		await books.WaitForStarted(1);
		books.Finish("A", ProcessBookResult.FailedAbort);
		await RunToCompletion(queue);

		// C and B were cleared by the abort before they could start.
		CollectionAssert.AreEqual(new[] { a }, starts.ToArray());
		CollectionAssert.AreEqual(new[] { a }, ends.ToArray());
	}

	[TestMethod]
	public async Task process_start_and_end_fire_for_every_started_book_including_validation_failures()
	{
		var (queue, books) = NewQueue(atOnce: 2);
		ProcessBookViewModel a = Book("A"), b = Book("B");
		var starts = new List<ProcessBookViewModel>();
		var ends = new List<ProcessBookViewModel>();
		queue.ProcessStart += (_, book) => { lock (starts) starts.Add(book); };
		queue.ProcessEnd += (_, book) => { lock (ends) ends.Add(book); };

		queue.AddToQueue([a, b]);
		await books.WaitForStarted(2);
		books.Finish("A", ProcessBookResult.ValidationFail);
		books.Finish("B");
		await RunToCompletion(queue);

		Assert.AreEqual(2, starts.Count);
		Assert.AreEqual(2, ends.Count);
		CollectionAssert.AreEquivalent(new[] { a, b }, ends.ToArray());
	}

	[TestMethod]
	public async Task changing_the_speed_limit_mid_run_is_pushed_onto_every_active_books_configuration()
	{
		var (queue, books) = NewQueue(atOnce: 2);
		var aConfig = Configuration.Instance.CreateEphemeralCopy();
		var bConfig = Configuration.Instance.CreateEphemeralCopy();
		var cConfig = Configuration.Instance.CreateEphemeralCopy();
		var a = Book("A", config: aConfig);
		var b = Book("B", config: bConfig);
		var c = Book("C", config: cConfig);

		queue.AddToQueue([a, b, c]);
		await books.WaitForStarted(2);

		queue.SpeedLimit = 2m;

		const long TwoMiB = 2 * 1024 * 1024;
		Assert.AreEqual(TwoMiB, Configuration.Instance.DownloadSpeedLimit);
		Assert.AreEqual(TwoMiB, aConfig.DownloadSpeedLimit);
		Assert.AreEqual(TwoMiB, bConfig.DownloadSpeedLimit);
		Assert.AreEqual(0, cConfig.DownloadSpeedLimit, "A book still waiting in the queue was given the limit.");

		books.FinishAll("A", "B");
		await books.WaitForStarted(3);
		books.Finish("C");
		await RunToCompletion(queue);
	}

	[TestMethod]
	public void constructing_the_queue_does_not_write_the_concurrency_setting_back()
	{
		// Stored raw and out of range: a write-back through the clamping setter would change it to the hard limit.
		Configuration.Instance.SetNonString(50, nameof(Configuration.MaxConcurrentDownloads));

		var queue = new ProcessQueueViewModel();

		Assert.AreEqual(Configuration.ConcurrentDownloadsHardLimit, queue.MaxConcurrentDownloads);
		Assert.AreEqual(50, Configuration.Instance.GetNonString(0, nameof(Configuration.MaxConcurrentDownloads)));
	}
}
