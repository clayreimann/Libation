using ApplicationServices;
using DataLayer;
using LibationFileManager;
using LibationUiBase.Forms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace LibationUiBase.GridView;

/// <summary>Grid action: push Audible progress for selected books to Audiobookshelf, then summarise.</summary>
public static class AudiobookshelfProgressPushFlow
{
	public const string Caption = "Push Audible progress to Audiobookshelf";

	public delegate Task<List<BookPushResult>> PushBooks(IReadOnlyCollection<LibraryBook> books, bool force);

	private static PushBooks ConfiguredPush { get; } = (books, force) =>
	{
		var connection = AbsConnection.From(Configuration.Instance)
			?? throw new InvalidOperationException("Audiobookshelf server URL and API token are not set (Settings -> Audiobookshelf).");
		return AudiobookshelfProgressPush.PushAsync(books, connection, force);
	};

	public static async Task RunAsync(IEnumerable<LibraryBook> selected, PushBooks? push = null)
	{
		push ??= ConfiguredPush;
		var books = selected.Where(lb => lb.Book.UserDefinedItem.BookStatus is LiberatedStatus.Liberated).ToList();
		if (books.Count == 0)
			return;

		var results = await RunSafelyAsync(push, books, force: false);
		var newer = results.Where(r => r.CanForce).ToList();
		if (newer.Count == 0)
		{
			await MessageBoxBase.Show(BuildSummary(results), Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		var prompt = $"{BuildSummary(results)}\n\nOverwrite Audiobookshelf's progress with Audible's for the {newer.Count} {(newer.Count == 1 ? "book" : "books")} where Audiobookshelf is newer?";
		if (await MessageBoxBase.Show(prompt, Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
			return;

		var forceBooks = books.Where(lb => newer.Any(r => string.Equals(r.Asin, lb.Book.AudibleProductId, StringComparison.OrdinalIgnoreCase))).ToList();
		var forced = await RunSafelyAsync(push, forceBooks, force: true);
		await MessageBoxBase.Show(BuildSummary(forced), Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
	}

	private static async Task<List<BookPushResult>> RunSafelyAsync(PushBooks push, List<LibraryBook> books, bool force)
	{
		try
		{
			return await push(books, force);
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Pushing Audible progress to Audiobookshelf failed");
			return books.Select(lb => new BookPushResult(lb.Book.AudibleProductId, lb.Book.TitleWithSubtitle, Outcome.Failed, ex.Message, null, null)).ToList();
		}
	}

	public static string BuildSummary(IReadOnlyCollection<BookPushResult> results)
	{
		var sb = new StringBuilder();
		var pushed = results.Count(r => r.Pushed);
		sb.Append(pushed == 1 ? "Pushed progress for 1 book." : $"Pushed progress for {pushed} books.");

		foreach (var group in results.Where(r => r.Skipped).GroupBy(r => r.Outcome).OrderBy(g => g.Key))
			sb.Append($"\n{Describe(group.Key)}: {group.Count()}");

		var failed = results.Where(r => r.Failed).ToList();
		if (failed.Count > 0)
		{
			sb.Append($"\nFailed: {failed.Count}");
			foreach (var r in failed.Take(5))
				sb.Append($"\n- {r.Title}: {r.Message}");
			if (failed.Count > 5)
				sb.Append($"\n- and {failed.Count - 5} more (see the log)");
		}
		return sb.ToString();
	}

	private static string Describe(Outcome outcome) => outcome switch
	{
		Outcome.ServerIsNewer => "Skipped, Audiobookshelf is newer",
		Outcome.FinishedOnServer => "Skipped, finished on Audiobookshelf",
		Outcome.AlreadyInSync => "Already in sync",
		Outcome.NotOnServer => "Not found in Audiobookshelf",
		Outcome.NoAudiblePosition => "No Audible listening position",
		_ => outcome.ToString(),
	};
}
