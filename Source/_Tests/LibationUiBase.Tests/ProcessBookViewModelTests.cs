using AudibleApi;
using AudibleApi.Common;
using DataLayer;
using Dinah.Core.ErrorHandling;
using FileLiberator;
using LibationFileManager;
using LibationUiBase.Forms;
using LibationUiBase.ProcessQueue;
using Newtonsoft.Json.Linq;

namespace LibationUiBase.Tests;

[TestClass]
[DoNotParallelize]
public class ProcessBookViewModelTests : QueueTestBase
{
	private const string SableJson = """
		{"error_code":"000307","message":"Unable to retrieve asset details from Sable(AssetInfos), for marketplaceId:AF2M0KC94RCEA, asin:B08XDZCH78, acr:null, skuLite:BK_ACX0_239890, version:LATEST, aaaClientId:urn:cdo:AudibleApiExternalRouterService:Prod:Default"}
		""";

	private class FakeStep(string name) : Processable
	{
		public override string Name => name;
		public bool Valid { get; init; } = true;
		public Func<StatusHandler> Outcome { get; init; } = () => new StatusHandler();
		public int ProcessCalls { get; private set; }

		public override bool Validate(LibraryBook libraryBook) => Valid;

		public override Task<StatusHandler> ProcessAsync(LibraryBook libraryBook)
		{
			ProcessCalls++;
			return Task.FromResult(Outcome());
		}
	}

	private sealed class LicensedStep(string name, DownloadOptions.LicenseInfo? toObtain = null) : FakeStep(name), ILicensedDownload
	{
		public DownloadOptions.LicenseInfo? LicenseInfo { get; set; }
		public DownloadOptions.LicenseInfo? ObtainedLicense { get; private set; }
		public DownloadOptions.LicenseInfo? Received { get; private set; }

		public override Task<StatusHandler> ProcessAsync(LibraryBook libraryBook)
		{
			Received = LicenseInfo;
			ObtainedLicense = toObtain;
			return base.ProcessAsync(libraryBook);
		}
	}

	private sealed class ThrowingStep(string name, Exception toThrow) : FakeStep(name)
	{
		public override Task<StatusHandler> ProcessAsync(LibraryBook libraryBook) => throw toThrow;
	}

	private static Func<Configuration, Processable> Succeeds(string name, List<string>? created = null)
		=> config => { created?.Add(name); return new FakeStep(name) { Configuration = config }; };

	private static Func<Configuration, Processable> Fails(string error, string name = "fails", List<string>? created = null)
		=> config => { created?.Add(name); return new FakeStep(name) { Configuration = config, Outcome = () => new StatusHandler { error } }; };

	private static Func<Configuration, Processable> Throws(Exception ex)
		=> config => new ThrowingStep("throws", ex) { Configuration = config };

	private static ProcessBookViewModel Chain(ProcessBookViewModel book, params Func<Configuration, Processable>[] steps)
	{
		foreach (var step in steps)
			book.AddProcessable(step);
		return book;
	}

	private static ContentLicenseDeniedException Denied(params (string ValidationType, string Reason)[] reasons)
	{
		var license = new ContentLicense
		{
			Asin = "B002V5B8OY",
			StatusCode = "Denied",
			LicenseDenialReasons = [.. reasons.Select(r => new LicenseDenialReason
			{
				ValidationType = r.ValidationType,
				RejectionReason = r.Reason,
				Message = r.Reason
			})]
		};
		return new ContentLicenseDeniedException(new Uri("https://audible.com/licenserequest"), license);
	}


	[TestMethod]
	public async Task steps_run_in_order_and_a_book_whose_steps_all_succeed_completes()
	{
		var created = new List<string>();
		var book = Chain(Book("A"), Succeeds("one", created), Succeeds("two", created), Succeeds("three", created));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.Success, result);
		Assert.AreEqual(ProcessBookResult.Success, book.Result);
		Assert.AreEqual(ProcessBookStatus.Completed, book.Status);
		Assert.AreEqual("Finished", book.StatusText);
		CollectionAssert.AreEqual(new[] { "one", "two", "three" }, created);
	}

	[TestMethod]
	public async Task the_first_step_that_does_not_succeed_stops_the_chain_and_later_steps_are_never_created()
	{
		var created = new List<string>();
		var book = Chain(Book("A"), Succeeds("one", created), Fails("Cancelled", "two", created), Succeeds("three", created));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.Cancelled, result);
		CollectionAssert.AreEqual(new[] { "one", "two" }, created);
	}

	[TestMethod]
	public async Task a_step_that_fails_without_a_recognised_reason_ends_the_chain_and_goes_to_the_bad_book_action()
	{
		Configuration.Instance.BadBook = Configuration.BadBookAction.Retry;
		var created = new List<string>();
		var book = Chain(Book("A"), Fails("boom", "one", created), Succeeds("two", created));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.FailedRetry, result);
		CollectionAssert.AreEqual(new[] { "one" }, created);
	}

	[TestMethod]
	public async Task a_step_that_does_not_apply_is_skipped_as_a_success_without_being_processed()
	{
		FakeStep? skipped = null;
		var book = Chain(Book("A"),
			config => skipped = new FakeStep("optional") { Configuration = config, Valid = false },
			Succeeds("after"));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.Success, result);
		Assert.AreEqual(0, skipped!.ProcessCalls);
	}

	[TestMethod]
	public async Task a_license_one_step_obtained_is_handed_to_the_next_licensed_step_and_kept_for_the_one_after()
	{
		var license = DownloadOptions.LicenseInfo.Create(new ContentLicense());
		LicensedStep? first = null, second = null, third = null;
		var book = Chain(Book("A"),
			config => first = new LicensedStep("first", license) { Configuration = config },
			config => second = new LicensedStep("second") { Configuration = config },
			config => third = new LicensedStep("third") { Configuration = config });

		await book.ProcessOneAsync();

		Assert.IsNull(first!.Received, "The first step was given a license nobody had obtained yet.");
		Assert.AreSame(license, second!.Received);
		Assert.AreSame(license, third!.Received, "A step that obtained nothing dropped the license for the steps after it.");
	}

	[TestMethod]
	[DataRow("Cancelled", ProcessBookResult.Cancelled, ProcessBookStatus.Cancelled)]
	[DataRow("Validation failed", ProcessBookResult.ValidationFail, ProcessBookStatus.Failed)]
	[DataRow("There is not enough space on the disk.", ProcessBookResult.DiskFull, ProcessBookStatus.Failed)]
	public async Task a_failure_reported_by_a_step_is_classified_by_its_message(string error, ProcessBookResult expected, ProcessBookStatus status)
	{
		var book = Chain(Book("A"), Fails(error));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(expected, result);
		Assert.AreEqual(expected, book.Result);
		Assert.AreEqual(status, book.Status);
	}

	[TestMethod]
	public async Task a_disk_full_exception_is_a_disk_full_result_and_skips_the_bad_book_handling()
	{
		Configuration.Instance.BadBook = Configuration.BadBookAction.Ask;
		var book = Chain(Book("A"), Throws(new IOException("There is not enough space on the disk.")));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.DiskFull, result);
		Assert.AreEqual(ProcessBookStatus.Failed, book.Status);
	}

	[TestMethod]
	public async Task a_disk_full_exception_wrapped_in_another_is_still_a_disk_full_result()
	{
		var book = Chain(Book("A"), Throws(new InvalidOperationException("write failed", new IOException("No space left on device"))));

		Assert.AreEqual(ProcessBookResult.DiskFull, await book.ProcessOneAsync());
	}

	[TestMethod]
	public async Task a_throttled_license_denial_with_no_accounts_file_is_reported_as_throttled()
	{
		var book = Chain(Book("A"), Throws(Denied(("AYCL", RejectionReason.CustomerThrottled))));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.LicenseDeniedThrottled, result);
		Assert.AreEqual(ProcessBookStatus.Failed, book.Status);
		Assert.AreEqual("License denied (throttled)", book.StatusText);
	}

	[TestMethod]
	public async Task a_license_denial_with_no_reason_is_a_possible_outage()
	{
		var book = Chain(Book("A"), Throws(Denied()));

		Assert.AreEqual(ProcessBookResult.LicenseDeniedPossibleOutage, await book.ProcessOneAsync());
		Assert.AreEqual("Possible Service Interruption", book.StatusText);
	}

	[TestMethod]
	public async Task a_license_denial_with_a_generic_reason_is_a_possible_outage()
	{
		var book = Chain(Book("A"), Throws(Denied(("AYCL", RejectionReason.GenericError))));

		Assert.AreEqual(ProcessBookResult.LicenseDeniedPossibleOutage, await book.ProcessOneAsync());
	}

	[TestMethod]
	public async Task a_specific_license_denial_on_a_plus_book_is_a_license_denial_with_the_plus_status()
	{
		var book = Chain(Book("A", plus: true), Throws(Denied(("AYCL", RejectionReason.ContentEligibility))));

		Assert.AreEqual(ProcessBookResult.LicenseDenied, await book.ProcessOneAsync());
		Assert.AreEqual("License denied (Plus; often temporary)", book.StatusText);
	}

	[TestMethod]
	public async Task a_specific_license_denial_on_an_owned_book_is_a_license_denial()
	{
		var book = Chain(Book("A", plus: false), Throws(Denied(("AYCL", RejectionReason.ContentEligibility))));

		Assert.AreEqual(ProcessBookResult.LicenseDenied, await book.ProcessOneAsync());
		Assert.AreEqual("License Denied", book.StatusText);
	}

	[TestMethod]
	public async Task the_sable_acr_null_error_recommends_widevine_when_widevine_is_off()
	{
		Configuration.Instance.UseWidevine = false;
		var ex = new ApiErrorException(
			"https://api.audible.com/1.0/content/B08XDZCH78/licenserequest",
			JObject.Parse(SableJson),
			WidevineRecommendation.SableAssetErrorCode);
		var book = Chain(Book("A"), Throws(ex));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.WidevineRecommended, result);
		Assert.AreEqual(ProcessBookStatus.Failed, book.Status);
		Assert.AreEqual(WidevineRecommendationUserMessage.QueueStatusText, book.StatusText);
	}

	[TestMethod]
	public async Task an_unrecognised_exception_is_logged_and_falls_through_to_the_bad_book_action()
	{
		Configuration.Instance.BadBook = Configuration.BadBookAction.Retry;
		var logs = new List<string>();
		var book = Chain(Book("A"), Throws(new InvalidOperationException("kaboom")));
		book.LogWritten += (_, text) => logs.Add(text);

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.FailedRetry, result);
		CollectionAssert.Contains(logs, "ERROR: kaboom");
	}

	[TestMethod]
	[DataRow(Configuration.BadBookAction.Abort, ProcessBookResult.FailedAbort)]
	[DataRow(Configuration.BadBookAction.Retry, ProcessBookResult.FailedRetry)]
	[DataRow(Configuration.BadBookAction.Ignore, ProcessBookResult.FailedSkip)]
	public async Task the_bad_book_setting_decides_the_result_without_asking(Configuration.BadBookAction setting, ProcessBookResult expected)
	{
		Configuration.Instance.BadBook = setting;
		var book = Chain(PersistedBook("Bad Book"), Fails("boom"));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(expected, result);
		Assert.AreEqual(expected, book.Result);
		Assert.AreEqual(ProcessBookStatus.Failed, book.Status);
		Assert.AreEqual(LiberatedStatus.NotLiberated, StoredStatus(book), "The job wrote a status the queue owns.");
	}

	[TestMethod]
	public async Task the_ask_setting_finishes_the_book_awaiting_a_decision_without_a_dialog()
	{
		Configuration.Instance.BadBook = Configuration.BadBookAction.Ask;
		var book = Chain(Book("A"), Fails("boom"));

		var result = await book.ProcessOneAsync();

		Assert.AreEqual(ProcessBookResult.AwaitingDecision, result);
		Assert.AreEqual(ProcessBookStatus.Failed, book.Status);
		Assert.AreEqual("Error, waiting for your choice", book.StatusText);
		Assert.AreEqual(0, Dialogs.Captions.Length);
	}

	[TestMethod]
	[DataRow(Configuration.BadBookAction.Retry, ProcessBookResult.FailedRetry)]
	[DataRow(Configuration.BadBookAction.Ignore, ProcessBookResult.FailedSkip)]
	[DataRow(Configuration.BadBookAction.Abort, ProcessBookResult.FailedAbort)]
	public async Task an_apply_to_all_override_decides_the_result_for_every_later_book(Configuration.BadBookAction answer, ProcessBookResult expected)
	{
		Configuration.Instance.BadBook = Configuration.BadBookAction.Ask;
		var session = new BadBookSessionContext { Override = answer };
		ProcessBookViewModel a = Chain(Book("A", session: session), Fails("boom")), b = Chain(Book("B", session: session), Fails("boom"));

		Assert.AreEqual(expected, await a.ProcessOneAsync());
		Assert.AreEqual(expected, await b.ProcessOneAsync());
	}

	[TestMethod]
	public async Task resetting_the_session_forgets_the_override()
	{
		var session = new BadBookSessionContext { Override = Configuration.BadBookAction.Abort };

		session.Reset();

		Assert.IsNull(session.Override);
		Configuration.Instance.BadBook = Configuration.BadBookAction.Ask;
		Assert.AreEqual(ProcessBookResult.AwaitingDecision, await Chain(Book("A", session: session), Fails("boom")).ProcessOneAsync());
	}

	[TestMethod]
	public async Task cancelling_a_book_that_has_finished_every_step_does_not_throw()
	{
		var book = Chain(Book("A"), Succeeds("one"));
		await book.ProcessOneAsync();

		await book.CancelAsync();

		Assert.IsTrue(book.CancellationRequested);
	}

	[TestMethod]
	public async Task cancelling_a_book_awaiting_a_decision_does_not_throw()
	{
		Configuration.Instance.BadBook = Configuration.BadBookAction.Ask;
		var book = Chain(Book("A"), Fails("boom"));
		Assert.AreEqual(ProcessBookResult.AwaitingDecision, await book.ProcessOneAsync());

		await book.CancelAsync();

		Assert.IsTrue(book.CancellationRequested);
	}
}
