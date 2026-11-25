using DataLayer;
using Dinah.Core;
using LibationEngine;
using LibationFileManager;
using LibationUiBase.Forms;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibationUiBase.ProcessQueue;

/// <summary>Shows the dialog for each queue pause and answers it.</summary>
internal sealed class GuiQueuePrompter
{
	private const string StopQuestion = "\n\nStop the queue? Choose No to keep going.";

	private readonly LiberationQueue _queue;
	private readonly SemaphoreSlim _oneDialogAtATime = new(1, 1);
	private bool _dailyLimitShownThisRun;

	public GuiQueuePrompter(LiberationQueue queue)
	{
		_queue = queue;
		_queue.RunStarted += (_, _) => _dailyLimitShownThisRun = false;
		_queue.QueuePaused += (_, pause) => _ = AnswerAsync(pause);
		_queue.QueueWaiting += (_, wait) => _ = NotifyWaitAsync(wait);
	}

	private async Task AnswerAsync(PendingDecision pause)
	{
		await _oneDialogAtATime.WaitAsync();
		try
		{
			// An earlier answer may have settled this one (apply to all, stop).
			if (_queue.Pending.All(p => p.Id != pause.Id))
				return;

			_queue.Resume(pause.Id, await PromptAsync(pause));
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Failed to show the queue dialog for {Reason}", pause.Reason);
			_queue.Resume(pause.Id, pause.Reason switch
			{
				PauseReason.BadBook => Decision.BadBook(Configuration.BadBookAction.Retry),
				PauseReason.DiskFull => Decision.Stop,
				_ => Decision.Continue
			});
		}
		finally
		{
			_oneDialogAtATime.Release();
		}
	}

	private async Task<Decision> PromptAsync(PendingDecision pause)
	{
		var job = pause.Job is { } id ? _queue.FindJob(id) : null;
		var libraryBook = job?.LibraryBook;

		switch (pause.Reason)
		{
			case PauseReason.BadBook when libraryBook is not null:
				return await AskBadBookAsync(libraryBook);
			case PauseReason.DiskFull:
				return await AskYesNoAsync(
					DiskFullUserMessage.BuildQueuePausedBody(),
					DiskFullUserMessage.DialogCaption,
					MessageBoxIcon.Warning) ? Decision.Resume : Decision.Stop;
			case PauseReason.LicenseGuidance when libraryBook is not null:
				var title = libraryBook.Book.TitleWithSubtitle;
				var (locale, hasAdditionalMarketplaces) = AppScaffolding.LicenseRecoveryGuidance.GetAccountContext(libraryBook);
				var updateStatus = AppScaffolding.LibationScaffolding.LastVersionCheckOutcome;
				var body = job!.Result switch
				{
					ProcessBookResult.LicenseDeniedPossibleOutage => ContentLicenseDeniedUserMessage.BuildDialogBodyForPossibleOutage(title, locale, updateStatus, hasAdditionalMarketplaces),
					ProcessBookResult.LicenseDeniedThrottled => ContentLicenseDeniedUserMessage.BuildDialogBodyForThrottling(title, locale, updateStatus, hasAdditionalMarketplaces),
					ProcessBookResult.LicenseDeniedThrottledRecovered => ContentLicenseDeniedUserMessage.BuildDialogBodyForThrottlingRecovered(title),
					_ => ContentLicenseDeniedUserMessage.BuildDialogBodyForPlusCatalog(title, locale, updateStatus, hasAdditionalMarketplaces)
				};
				return await ContinueOrStopAsync(body, ContentLicenseDeniedUserMessage.DialogCaption);
			case PauseReason.WidevineGuidance when libraryBook is not null:
				return await ContinueOrStopAsync(
					WidevineRecommendationUserMessage.BuildDialogBody(libraryBook.Book.TitleWithSubtitle),
					WidevineRecommendationUserMessage.DialogCaption);
			default:
				return Decision.Continue;
		}
	}

	private static async Task<Decision> AskBadBookAsync(LibraryBook libraryBook)
	{
		string details;
		try
		{
			static string trunc(string str)
				=> string.IsNullOrWhiteSpace(str) ? "[empty]"
				: (str.Length > 50) ? $"{str.Truncate(47)}..."
				: str;

			details = $"""
				  Title: {libraryBook.Book.TitleWithSubtitle}
				  ID: {libraryBook.Book.AudibleProductId}
				  Author: {trunc(libraryBook.Book.AuthorNames)}
				  Narr: {trunc(libraryBook.Book.NarratorNames)}
				""";
		}
		catch
		{
			details = "[Error retrieving details]";
		}

		var skipDialogText = $"""
			An error occurred while trying to process this book.
			{details}
			
			- ABORT: Stop processing books.
			
			- RETRY: Skip this book for now, but retry if it is requeued. Continue processing the queued books.
			
			- IGNORE: Permanently ignore this book. Continue processing the queued books. (Will not try this book again later.)

			Check "Apply to all remaining books" to use your choice for the rest of this queue.
			Check "Remember in Settings" to save your choice in Download/Decrypt settings.
			""";

		var result = await BadBookActionDialogBase.Show(skipDialogText, "Skip this book?");
		return Decision.BadBook(ToBadBookAction(result.Action), result.ApplyToAll, result.RememberInSettings);
	}

	private static Configuration.BadBookAction ToBadBookAction(DialogResult action)
		=> action switch
		{
			DialogResult.Abort => Configuration.BadBookAction.Abort,
			DialogResult.Retry => Configuration.BadBookAction.Retry,
			DialogResult.Ignore => Configuration.BadBookAction.Ignore,
			_ => Configuration.BadBookAction.Retry
		};

	private static async Task<Decision> ContinueOrStopAsync(string body, string caption)
	{
		var stop = await AskYesNoAsync(body + StopQuestion, caption, MessageBoxIcon.Asterisk);
		return stop ? Decision.Stop : Decision.Continue;
	}

	/// <summary>True for Yes.</summary>
	private static async Task<bool> AskYesNoAsync(string body, string caption, MessageBoxIcon icon)
		=> await MessageBoxBase.Show(body, caption, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;

	private async Task NotifyWaitAsync(QueueWait wait)
	{
		if (wait.Reason != WaitReason.DailyLimit || wait.Allowance is not { } allowance || _dailyLimitShownThisRun)
			return;

		_dailyLimitShownThisRun = true;
		var title = wait.Job is { } id ? _queue.FindJob(id)?.LibraryBook.Book.TitleWithSubtitle : null;

		try
		{
			await MessageBoxBase.Show(
				DailyDownloadLimitUserMessage.BuildQueuePausedBody(allowance, title ?? string.Empty),
				DailyDownloadLimitUserMessage.DialogCaption,
				MessageBoxButtons.OK,
				MessageBoxIcon.Information);
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Failed to show the daily download limit message");
		}
	}
}
