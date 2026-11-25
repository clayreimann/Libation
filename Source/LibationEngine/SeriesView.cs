using DataLayer;
using LibationEngine.Contracts;

namespace LibationEngine;

public static class SeriesView
{
	public static IReadOnlyList<SeriesGroupDto> Group(IEnumerable<LibraryBook> books)
		=> books
			.SelectMany(lb => lb.Book.SeriesLink.Select(link => (Book: lb, Link: link)))
			.GroupBy(x => x.Link.Series.AudibleSeriesId, StringComparer.OrdinalIgnoreCase)
			.Select(g =>
			{
				var ordered = g.OrderBy(x => Key(x.Link)).ThenBy(x => x.Book.Book.TitleWithSubtitle, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Book.Book.AudibleProductId, StringComparer.Ordinal).ToList();
				return new SeriesGroupDto(
					g.Key,
					g.Select(x => x.Link.Series.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "",
					ordered.Count,
					ordered.Count(x => x.Book.Book.UserDefinedItem.BookStatus is LiberatedStatus.Liberated),
					ordered.Select(x => new SeriesBookDto(x.Book.Book.AudibleProductId, x.Book.Book.TitleWithSubtitle, x.Link.Index > 0 ? x.Link.Order : null)).ToList());
			})
			.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(g => g.SeriesId, StringComparer.Ordinal)
			.ToList();

	/// <summary>Descending by series index, entries without one first, as the grid's podcast children.</summary>
	public static IEnumerable<LibraryBook> EpisodeOrder(IEnumerable<LibraryBook> episodes, string seriesId)
		=> episodes
			.OrderByDescending(lb => Key(lb.Book.SeriesLink.First(l => string.Equals(l.Series.AudibleSeriesId, seriesId, StringComparison.OrdinalIgnoreCase))))
			.ThenBy(lb => lb.Book.TitleWithSubtitle, StringComparer.OrdinalIgnoreCase)
			.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal);

	private static float Key(SeriesBook link) => link.Index > 0 ? link.Index : float.PositiveInfinity;
}
