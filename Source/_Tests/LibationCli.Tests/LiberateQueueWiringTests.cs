using ApplicationServices;
using AssertionHelper;
using DataLayer;
using Dinah.Core.ErrorHandling;
using FileLiberator;
using LibationEngine;
using LibationFileManager;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace LibationCli.Tests;

[TestClass]
[DoNotParallelize]
public class LiberateQueueWiringTests
{
	private string tempLibationFiles = string.Empty;
	private TextWriter originalOut = Console.Out;
	private TextWriter originalError = Console.Error;
	private StringWriter capturedError = new();

	[TestInitialize]
	public void Initialize()
	{
		tempLibationFiles = Path.Combine(Path.GetTempPath(), $"libation-queue-wiring-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempLibationFiles);
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, tempLibationFiles);
		Configuration.CreateMockInstance();

		originalOut = Console.Out;
		originalError = Console.Error;
		capturedError = new StringWriter();
		Console.SetOut(new StringWriter());
		Console.SetError(capturedError);
		Environment.ExitCode = 0;
	}

	[TestCleanup]
	public void Cleanup()
	{
		Console.SetOut(originalOut);
		Console.SetError(originalError);
		Environment.ExitCode = 0;
		Configuration.RestoreSingletonInstance();
		Environment.SetEnvironmentVariable(LibationFiles.LIBATION_FILES_DIR, null);

		try
		{
			Directory.Delete(tempLibationFiles, recursive: true);
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test over.
		}
	}

	private static void Seed(int count)
	{
		var author = new Contributor("Test Author");
		var narrator = new Contributor("Test Narrator");
		using var context = DbContexts.GetContext();
		for (var i = 0; i < count; i++)
		{
			var book = new Book(new AudibleProductId($"B0WIRING{i:0000}"), $"Title {i}", "", "", 600, ContentType.Product, [author], [narrator], "us");
			context.LibraryBooks.Add(new LibraryBook(book, new DateTime(2026, 1, 1).AddMinutes(i), "test-account"));
		}
		context.SaveChanges();
	}

	private sealed class Step : Processable
	{
		public override string Name => nameof(Step);
		public override bool Validate(LibraryBook libraryBook) => true;
		public override Task<StatusHandler> ProcessAsync(LibraryBook libraryBook) => Task.FromResult(new StatusHandler());
	}

	private static (StubbedLiberate Run, LiberationQueue Queue, ConcurrentQueue<string> Ran) Arrange(Func<string, ProcessBookResult> outcome)
	{
		var queue = LiberateOptions.CreateCliQueue();
		var ran = new ConcurrentQueue<string>();
		queue.RunJob = job =>
		{
			var asin = job.LibraryBook.Book.AudibleProductId;
			ran.Enqueue(asin);
			var result = outcome(asin);
			job.Result = result;
			job.Status = result == ProcessBookResult.Success ? ProcessBookStatus.Completed : ProcessBookStatus.Failed;
			return Task.FromResult(result);
		};
		return (new StubbedLiberate(new Step { Configuration = Configuration.Instance }), queue, ran);
	}

	[TestMethod]
	public async Task A_full_disk_stops_a_command_line_run_and_fails_its_exit_code()
	{
		Seed(3);
		var (run, queue, ran) = Arrange(asin => asin.EndsWith("0000") ? ProcessBookResult.DiskFull : ProcessBookResult.Success);

		await run.Go(queue);

		ran.Should().HaveCount(1);
		Assert.AreEqual((int)ExitCode.RunTimeError, Environment.ExitCode);
	}

	[TestMethod]
	public async Task A_bad_book_is_left_for_the_next_run_and_the_rest_carry_on()
	{
		Seed(3);
		var (run, queue, ran) = Arrange(asin => asin.EndsWith("0000") ? ProcessBookResult.AwaitingDecision : ProcessBookResult.Success);

		await run.Go(queue);

		ran.Should().HaveCount(3);
		Assert.AreEqual(0, Environment.ExitCode);
		StringAssert.Contains(capturedError.ToString(), "leaving it to retry on the next run");
	}

	[TestMethod]
	public async Task A_run_over_the_daily_limit_skips_its_books_and_summarises_them()
	{
		Seed(2);
		Configuration.Instance.DailyDownloadLimit = Configuration.DailyLimitScope.AllBooks;
		Configuration.Instance.DailyDownloadLimitQuantity = 1;
		Configuration.Instance.DailyDownloadLimitUnit = Configuration.DailyLimitUnit.Books;
		DownloadHistoryStore.Record("B0EARLIER001", isAudiblePlus: false, bytes: 1);
		var (run, queue, ran) = Arrange(_ => ProcessBookResult.Success);
		var stdout = new StringWriter();
		Console.SetOut(stdout);

		await run.Go(queue);

		ran.Should().HaveCount(0);
		StringAssert.Contains(stdout.ToString(), "2");
		StringAssert.Contains(stdout.ToString(), "Done. All books have been processed");
	}
}
