using ApplicationServices;
using DataLayer;
using LibationFileManager;
using LibationUiBase.Forms;
using LibationUiBase.ProcessQueue;
using System.Collections.Concurrent;

namespace LibationUiBase.Tests;

/// <summary>Records every message box and lets a test decide when, or whether, each one is dismissed.</summary>
public sealed class DialogLog
{
	private readonly ConcurrentQueue<string> captions = new();

	public Func<string, Task<DialogResult>>? Respond { get; set; }
	public string[] Captions => captions.ToArray();
	public int Count(string caption) => captions.Count(c => c == caption);

	public Task<DialogResult> Show(object? owner, string message, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, bool saveAndRestorePosition = true)
	{
		captions.Enqueue(caption);
		return Respond?.Invoke(caption) ?? Task.FromResult(DialogResult.OK);
	}
}

/// <summary>A gate per book, so a test decides when and in what order each one finishes.</summary>
public sealed class GatedBooks
{
	private readonly ConcurrentDictionary<string, TaskCompletionSource<ProcessBookResult>> gates = new();
	private readonly object countLock = new();
	private int running;

	public int HighWaterMark { get; private set; }
	public ConcurrentQueue<string> Started { get; } = new();

	public static string Id(ProcessBookViewModel book) => book.LibraryBook.Book.AudibleProductId.ToString()!;

	public Task<ProcessBookResult> Handle(ProcessBookViewModel book)
	{
		var id = Id(book);

		// Counted before Started is published so a waiter never sees a book the high-water mark missed.
		lock (countLock)
		{
			running++;
			if (running > HighWaterMark)
				HighWaterMark = running;
		}

		Started.Enqueue(id);

		return GateFor(id).Task.ContinueWith(t =>
		{
			lock (countLock) running--;
			// The real ProcessOneAsync records its own outcome on the book, and the loop reads it back.
			book.Result = t.Result;
			return t.Result;
		});
	}

	private TaskCompletionSource<ProcessBookResult> GateFor(string id)
		=> gates.GetOrAdd(id, _ => new TaskCompletionSource<ProcessBookResult>(TaskCreationOptions.RunContinuationsAsynchronously));

	public void Finish(string id, ProcessBookResult result = ProcessBookResult.Success)
		=> GateFor(id).TrySetResult(result);

	public void FinishAll(params string[] ids)
	{
		foreach (var id in ids)
			Finish(id);
	}

	public Task WaitForStarted(int count)
		=> QueueTestBase.Eventually(() => Started.Count >= count, $"{count} books to start (saw {Started.Count})");
}

/// <summary>
/// Temp Libation files dir, a mock Configuration and recorded message boxes. The temp dir keeps the
/// recovery-guidance lookups and book-status writes off the real accounts file and library database.
/// </summary>
public abstract class QueueTestBase
{
	public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private string tempDir = "";

	protected DialogLog Dialogs { get; private set; } = new();

	[TestInitialize]
	public void InitializeQueueTest()
	{
		// ProcessQueueViewModel raises property changes through the current SynchronizationContext.
		SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
		tempDir = Path.Combine(Path.GetTempPath(), $"libation-queue-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, tempDir);
		Configuration.CreateMockInstance();
		Dialogs = new DialogLog();
		MessageBoxBase.ShowAsyncImpl = Dialogs.Show;
	}

	[TestCleanup]
	public void CleanupQueueTest()
	{
		MessageBoxBase.ShowAsyncImpl = null!;
		BadBookActionDialogBase.ShowAsyncImpl = null!;
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, null);
		SynchronizationContext.SetSynchronizationContext(null);
		try
		{
			Directory.Delete(tempDir, recursive: true);
		}
		catch
		{
		}
	}

	/// <remarks>Must be called before the test's first <c>await</c>; see ReactiveObject's captured context.</remarks>
	protected static ProcessBookViewModel Book(
		string asin,
		bool plus = false,
		bool download = false,
		Configuration? config = null,
		BadBookSessionContext? session = null)
	{
		var contributor = Contributor.GetEmpty();
		var book = new Book(new AudibleProductId(asin), asin, null, null, 1, ContentType.Product, [contributor], [contributor], "us");
		var libraryBook = new LibraryBook(book, new DateTime(2026, 8, 10), "account");
		libraryBook.SetIsAudiblePlus(plus);
		return Wrap(libraryBook, config, session, download);
	}

	/// <summary>A book that exists in the temp library database, for code that writes its status.</summary>
	protected static ProcessBookViewModel PersistedBook(
		string title,
		bool plus = false,
		BadBookSessionContext? session = null)
	{
		var mock = MockLibraryBook.CreateBook(title: title, isAudiblePlus: plus, bookStatus: LiberatedStatus.NotLiberated);
		using (var context = DbContexts.GetContext())
		{
			context.LibraryBooks.Add(new LibraryBook(mock.Book, mock.DateAdded, mock.Account));
			context.SaveChanges();
		}

		var stored = DbContexts.GetLibraryBook_Flat_NoTracking(mock.Book.AudibleProductId)!;
		return Wrap(stored, null, session, download: false);
	}

	protected static LiberatedStatus StoredStatus(ProcessBookViewModel book)
		=> DbContexts.GetLibraryBook_Flat_NoTracking(book.LibraryBook.Book.AudibleProductId)!.Book.UserDefinedItem.BookStatus;

	private static ProcessBookViewModel Wrap(LibraryBook libraryBook, Configuration? config, BadBookSessionContext? session, bool download)
	{
		var vm = new ProcessBookViewModel(libraryBook, config ?? Configuration.Instance, session);
		if (download)
			vm.AddDownloadDecryptBook();
		return vm;
	}

	/// <param name="atOnce">
	/// Machine capability is pinned to the same number so a small CI runner cannot quietly clamp the
	/// setting under test.
	/// </param>
	protected static (ProcessQueueViewModel Queue, GatedBooks Books) NewQueue(int atOnce)
	{
		var books = new GatedBooks();
		var queue = new ProcessQueueViewModel { MaxConcurrentDownloads = atOnce, MachineCeilingOverride = atOnce };
		queue.ProcessBookHandler = books.Handle;
		return (queue, books);
	}

	public static async Task Eventually(Func<bool> condition, string description)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
				Assert.Fail($"Timed out after {Patience.TotalSeconds}s waiting for {description}.");
			await Task.Delay(15);
		}
	}

	protected static async Task RunToCompletion(ProcessQueueViewModel queue)
	{
		var runner = queue.QueueRunner;
		Assert.IsNotNull(runner, "The queue loop never started.");
		var finished = await Task.WhenAny(runner, Task.Delay(Patience));
		Assert.AreSame(runner, finished, $"The queue loop did not finish within {Patience.TotalSeconds}s.");
		await runner;
	}

	/// <summary>Snapshot of the queue's log, tolerating the loop appending while it is read.</summary>
	protected static string[] Log(ProcessQueueViewModel queue)
	{
		for (var attempt = 0; ; attempt++)
		{
			try
			{
				return queue.LogEntries.Select(e => e.LogMessage).ToArray();
			}
			catch (Exception) when (attempt < 50)
			{
				Thread.Yield();
			}
		}
	}
}
