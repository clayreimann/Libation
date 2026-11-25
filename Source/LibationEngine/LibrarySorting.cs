using DataLayer;

namespace LibationEngine;

public enum LibrarySortKey { Title, Author, Narrator, Series, Length, PurchaseDate, Added, Status, Rating, LastDownload }

public static class LibrarySorting
{
	public static bool TryParse(string? sort, string? dir, out LibrarySortKey? key, out bool descending, out string error)
	{
		key = null;
		descending = false;
		error = "";
		if (!string.IsNullOrWhiteSpace(sort))
		{
			if (!Enum.TryParse<LibrarySortKey>(sort.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
			{
				error = $"sort must be one of {string.Join(", ", Enum.GetNames<LibrarySortKey>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}.";
				return false;
			}
			key = parsed;
		}

		switch (dir?.Trim().ToLowerInvariant())
		{
			case null or "" or "asc":
				return true;
			case "desc":
				descending = true;
				return true;
			default:
				error = "dir must be asc or desc.";
				return false;
		}
	}

	public static IOrderedEnumerable<LibraryBook> Order(IEnumerable<LibraryBook> books, LibrarySortKey key, bool descending)
	{
		IOrderedEnumerable<LibraryBook> By<T>(Func<LibraryBook, T> selector, IComparer<T>? comparer = null)
			=> descending ? books.OrderByDescending(selector, comparer) : books.OrderBy(selector, comparer);

		IOrderedEnumerable<LibraryBook> NullsLast(Func<LibraryBook, string?> selector)
		{
			var present = books.OrderBy(lb => selector(lb) is null);
			return descending
				? present.ThenByDescending(lb => selector(lb) ?? "", StringComparer.OrdinalIgnoreCase)
				: present.ThenBy(lb => selector(lb) ?? "", StringComparer.OrdinalIgnoreCase);
		}

		IOrderedEnumerable<LibraryBook> LastDownload()
		{
			var present = books.OrderBy(lb => lb.Book.UserDefinedItem.LastDownloaded is null);
			return descending
				? present.ThenByDescending(lb => lb.Book.UserDefinedItem.LastDownloaded)
				: present.ThenBy(lb => lb.Book.UserDefinedItem.LastDownloaded);
		}

		static string? First(IEnumerable<Contributor> people) => people.Select(p => p.Name).FirstOrDefault();
		static SeriesBook? FirstSeries(LibraryBook lb) => lb.Book.SeriesLink.FirstOrDefault();

		var sorted = key switch
		{
			LibrarySortKey.Title => By(lb => lb.Book.TitleWithSubtitle, StringComparer.OrdinalIgnoreCase),
			LibrarySortKey.Author => NullsLast(lb => First(lb.Book.Authors)),
			LibrarySortKey.Narrator => NullsLast(lb => First(lb.Book.Narrators)),
			LibrarySortKey.Series => NullsLast(lb => FirstSeries(lb)?.Series.Name),
			LibrarySortKey.Length => By(lb => lb.Book.LengthInMinutes),
			LibrarySortKey.PurchaseDate or LibrarySortKey.Added => By(lb => lb.DateAdded),
			LibrarySortKey.Status => By(lb => lb.Book.UserDefinedItem.BookStatus),
			LibrarySortKey.Rating => By(lb => lb.Book.Rating?.OverallRating ?? 0f),
			LibrarySortKey.LastDownload => LastDownload(),
			_ => throw new ArgumentOutOfRangeException(nameof(key))
		};

		if (key == LibrarySortKey.Series)
			sorted = descending ? sorted.ThenByDescending(lb => FirstSeries(lb)?.Index ?? 0f) : sorted.ThenBy(lb => FirstSeries(lb)?.Index ?? 0f);

		return sorted.ThenBy(lb => lb.Book.TitleWithSubtitle, StringComparer.OrdinalIgnoreCase).ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal);
	}
}
