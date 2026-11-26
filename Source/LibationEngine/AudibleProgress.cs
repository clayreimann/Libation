using ApplicationServices;
using DataLayer;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace LibationEngine;

/// <summary>Either side is null when its service has no position; throws when a service cannot be reached.</summary>
public sealed record ProgressSnapshot(ListeningProgress? Audible, AbsProgress? Audiobookshelf, string? AbsItemId);

/// <summary>The Audible and Audiobookshelf calls behind the progress endpoints; the seam tests replace.</summary>
public interface IProgressSync
{
	Task<ProgressSnapshot> ReadAsync(LibraryBook book, AbsConnection? connection, CancellationToken ct);

	Task<IReadOnlyList<BookPushResult>> PushAsync(IReadOnlyCollection<LibraryBook> books, AbsConnection connection, bool force, bool dryRun, CancellationToken ct);
}

public sealed class ProgressSync : IProgressSync
{
	public async Task<ProgressSnapshot> ReadAsync(LibraryBook book, AbsConnection? connection, CancellationToken ct)
	{
		var errors = new List<Exception>();
		var positions = await AudibleListeningProgressService.GetProgressAsync([book], allowInteractiveLogin: false, (_, ex) => errors.Add(ex), ct);
		if (positions.Count == 0 && errors.Count > 0)
			throw errors[0];
		var audible = positions.FirstOrDefault();

		var probe = audible ?? new ListeningProgress(book.Book.AudibleProductId, book.Book.TitleWithSubtitle, book.Book.AuthorNames, book.Account, book.Book.Locale, 0, null, null, false);
		AbsItem? item = null;
		AbsProgress? existing = null;
		if (connection is { LibraryId: { Length: > 0 } libraryId } && !string.IsNullOrWhiteSpace(libraryId))
		{
			var items = await GetLibraryItemsAsync(connection.ServerUrl, connection.ApiToken, libraryId, ct);
			item = FindItem(probe, items)?.Item;
			if (item is not null)
				(await GetMyProgressAsync(connection.ServerUrl, connection.ApiToken, ct)).TryGetValue(item.Id, out existing);
		}
		return new(audible, existing, item?.Id);
	}

	public async Task<IReadOnlyList<BookPushResult>> PushAsync(IReadOnlyCollection<LibraryBook> books, AbsConnection connection, bool force, bool dryRun, CancellationToken ct)
		=> await AudiobookshelfProgressPush.PushAsync(books, connection, force, dryRun, AudiobookshelfProgressPush.DefaultReader(allowInteractiveLogin: false), cancellationToken: ct);
}
