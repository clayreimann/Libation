using DataLayer;
using Dinah.Core;
using LibationEngine;
using LibationFileManager;
using LibationUiBase.Forms;
using System.Threading.Tasks;

namespace LibationUiBase.ProcessQueue;

internal sealed class GuiLiberationInteraction : ILiberationInteraction
{
	public static GuiLiberationInteraction Instance { get; } = new();

	public async Task<BadBookAnswer> AskBadBookAsync(LiberationJob job)
	{
		var libraryBook = job.LibraryBook;
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
		return new BadBookAnswer(ToBadBookAction(result.Action), result.ApplyToAll, result.RememberInSettings);
	}

	private static Configuration.BadBookAction ToBadBookAction(DialogResult action)
		=> action switch
		{
			DialogResult.Abort => Configuration.BadBookAction.Abort,
			DialogResult.Retry => Configuration.BadBookAction.Retry,
			DialogResult.Ignore => Configuration.BadBookAction.Ignore,
			_ => Configuration.BadBookAction.Retry
		};

	public async Task NotifyAsync(QueueNotice notice)
	{
		var libraryBook = notice.LibraryBook;
		switch (notice.Kind)
		{
			case QueueNoticeKind.DiskFull:
				await MessageBoxBase.Show(
					DiskFullUserMessage.BuildQueueStoppedBody(),
					DiskFullUserMessage.DialogCaption,
					MessageBoxButtons.OK,
					MessageBoxIcon.Warning);
				break;
			case QueueNoticeKind.LicenseGuidance:
				var title = libraryBook.Book.TitleWithSubtitle;
				var (locale, hasAdditionalMarketplaces) = AppScaffolding.LicenseRecoveryGuidance.GetAccountContext(libraryBook);
				var updateStatus = AppScaffolding.LibationScaffolding.LastVersionCheckOutcome;
				var body = notice.Result switch
				{
					ProcessBookResult.LicenseDeniedPossibleOutage => ContentLicenseDeniedUserMessage.BuildDialogBodyForPossibleOutage(title, locale, updateStatus, hasAdditionalMarketplaces),
					ProcessBookResult.LicenseDeniedThrottled => ContentLicenseDeniedUserMessage.BuildDialogBodyForThrottling(title, locale, updateStatus, hasAdditionalMarketplaces),
					ProcessBookResult.LicenseDeniedThrottledRecovered => ContentLicenseDeniedUserMessage.BuildDialogBodyForThrottlingRecovered(title),
					_ => ContentLicenseDeniedUserMessage.BuildDialogBodyForPlusCatalog(title, locale, updateStatus, hasAdditionalMarketplaces)
				};
				await MessageBoxBase.Show(
					body,
					ContentLicenseDeniedUserMessage.DialogCaption,
					MessageBoxButtons.OK,
					MessageBoxIcon.Asterisk);
				break;
			case QueueNoticeKind.WidevineGuidance:
				await MessageBoxBase.Show(
					WidevineRecommendationUserMessage.BuildDialogBody(libraryBook.Book.TitleWithSubtitle),
					WidevineRecommendationUserMessage.DialogCaption,
					MessageBoxButtons.OK,
					MessageBoxIcon.Asterisk);
				break;
			case QueueNoticeKind.DailyLimitPaused when notice.Allowance is { } allowance:
				await MessageBoxBase.Show(
					DailyDownloadLimitUserMessage.BuildQueuePausedBody(allowance, libraryBook.Book.TitleWithSubtitle),
					DailyDownloadLimitUserMessage.DialogCaption,
					MessageBoxButtons.OK,
					MessageBoxIcon.Information);
				break;
		}
	}
}
