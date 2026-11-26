using DataLayer;
using LibationFileManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace ApplicationServices;

/// <summary>Server address, token and library that progress is pushed to.</summary>
public sealed record AbsConnection(string ServerUrl, string ApiToken, string? LibraryId)
{
	/// <summary>Null unless the server URL and API token are both set; the library may still be missing.</summary>
	public static AbsConnection? From(Configuration config)
		=> string.IsNullOrWhiteSpace(config.AudiobookshelfServerUrl) || string.IsNullOrWhiteSpace(config.AudiobookshelfApiToken)
			? null
			: new(config.AudiobookshelfServerUrl, config.AudiobookshelfApiToken, config.AudiobookshelfLibraryId);
}

/// <summary>One book's outcome from <see cref="AudiobookshelfProgressPush.PushAsync"/>.</summary>
public sealed record BookPushResult(string Asin, string? Title, Outcome Outcome, string Message, string? AbsItemId, double? PositionSeconds)
{
	public string Label => $"[{Asin}] {Title}";
	public bool Pushed => Outcome is Outcome.Updated or Outcome.WouldUpdate;
	public bool Failed => Outcome is Outcome.Failed;
	public bool Skipped => !Pushed && !Failed;
	public bool CanForce => Outcome is Outcome.ServerIsNewer;
}

/// <summary>Reads Audible positions for books; the seam tests replace.</summary>
public delegate Task<List<ListeningProgress>> AudiblePositionReader(
	IReadOnlyCollection<LibraryBook> books,
	Action<string, Exception> onAccountError,
	CancellationToken cancellationToken);

/// <summary>Pushes Audible listening progress for chosen library books to Audiobookshelf.</summary>
public static class AudiobookshelfProgressPush
{
	public static AudiblePositionReader DefaultReader(bool allowInteractiveLogin) => (books, onAccountError, ct)
		=> AudibleListeningProgressService.GetProgressAsync(books, allowInteractiveLogin, onAccountError, ct);

	/// <summary>One result per book in <paramref name="books"/>, in order; never throws for a single title.</summary>
	public static async Task<List<BookPushResult>> PushAsync(
		IReadOnlyCollection<LibraryBook> books,
		AbsConnection connection,
		bool force,
		bool dryRun = false,
		AudiblePositionReader? readPositions = null,
		Action<BookPushResult>? onResult = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(books);
		ArgumentNullException.ThrowIfNull(connection);
		books = books.Distinct().ToList();

		var outcomes = new Dictionary<LibraryBook, BookPushResult>();
		void Record(LibraryBook lb, BookPushResult result)
		{
			outcomes[lb] = result;
			onResult?.Invoke(result);
		}
		BookPushResult Make(LibraryBook lb, Outcome outcome, string message)
			=> new(lb.Book.AudibleProductId, lb.Book.TitleWithSubtitle, outcome, message, null, null);

		if (string.IsNullOrWhiteSpace(connection.LibraryId))
		{
			foreach (var lb in books)
				Record(lb, Make(lb, Outcome.Failed, "No Audiobookshelf library is selected (choose one in Settings -> Audiobookshelf)"));
			return Collect(books, outcomes);
		}

		var accountErrors = new Dictionary<string, Exception>();
		List<ListeningProgress> positions;
		try
		{
			positions = await (readPositions ?? DefaultReader(allowInteractiveLogin: true))(books, (label, ex) => accountErrors[label] = ex, cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			foreach (var lb in books)
				Record(lb, Make(lb, Outcome.Failed, $"Could not read progress from Audible: {ex.Message}"));
			return Collect(books, outcomes);
		}

		var positionByAsin = positions
			.GroupBy(p => p.Asin, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

		var toPush = new List<(LibraryBook Book, ListeningProgress Progress)>();
		foreach (var lb in books)
		{
			var label = AudibleListeningProgressService.AccountLabel(lb.Account, lb.Book.Locale);
			if (positionByAsin.TryGetValue(lb.Book.AudibleProductId, out var progress))
				toPush.Add((lb, progress));
			else if (accountErrors.TryGetValue(label, out var error))
				Record(lb, Make(lb, Outcome.Failed, $"Could not read progress from Audible: {error.Message}"));
			else
				Record(lb, Make(lb, Outcome.NoAudiblePosition, "Audible has no listening position for this book"));
		}

		if (toPush.Count > 0)
		{
			try
			{
				var byProgress = toPush.ToDictionary(t => t.Progress, t => t.Book);
				await AudiobookshelfProgressSync.PushAsync(
					connection.ServerUrl,
					connection.ApiToken,
					connection.LibraryId,
					toPush.Select(t => t.Progress),
					force,
					dryRun,
					r => Record(byProgress[r.Progress], new(
						r.Progress.Asin,
						byProgress[r.Progress].Book.TitleWithSubtitle,
						r.Outcome,
						r.Message.StartsWith($"[{r.Progress.Asin}] {r.Progress.Title}: ", StringComparison.Ordinal)
							? r.Message[$"[{r.Progress.Asin}] {r.Progress.Title}: ".Length..]
							: r.Message,
						r.AbsItemId,
						r.Progress.PositionMs / 1000d)),
					cancellationToken);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Serilog.Log.Logger.Error(ex, "Audiobookshelf progress push failed");
				foreach (var (lb, _) in toPush.Where(t => !outcomes.ContainsKey(t.Book)))
					Record(lb, Make(lb, Outcome.Failed, $"Could not reach Audiobookshelf: {ex.Message}"));
			}
		}

		return Collect(books, outcomes);
	}

	private static List<BookPushResult> Collect(IEnumerable<LibraryBook> books, Dictionary<LibraryBook, BookPushResult> outcomes)
		=> books.Select(lb => outcomes[lb]).ToList();
}
