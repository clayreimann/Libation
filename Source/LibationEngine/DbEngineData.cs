using ApplicationServices;
using AudibleUtilities;
using DataLayer;
using LibationEngine.Contracts;

namespace LibationEngine;

public sealed class DbEngineData : IEngineData
{
	public IReadOnlyList<AccountDto> GetAccounts()
	{
		using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
		return persister.AccountsSettings.Accounts.Select(ToDto).ToList();
	}

	public LibraryPageDto GetLibrary(int offset, int limit)
	{
		var books = DbContexts.GetLibrary_Flat_NoTracking();
		var items = books
			.OrderByDescending(lb => lb.DateAdded)
			.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
			.Skip(offset)
			.Take(limit)
			.Select(ToDto)
			.ToList();
		return new(books.Count, offset, limit, items);
	}

	public BookDto? GetBook(string asin)
		=> DbContexts.GetLibraryBook_Flat_NoTracking(asin) is { } lb ? ToDto(lb) : null;

	public static AccountDto ToDto(Account account)
		=> new(account.AccountId, account.AccountName, account.Locale?.Name, account.LibraryScan);

	public static BookDto ToDto(LibraryBook lb)
	{
		var book = lb.Book;
		return new(
			book.AudibleProductId,
			book.Title,
			string.IsNullOrEmpty(book.Subtitle) ? null : book.Subtitle,
			book.Authors.Select(a => a.Name).ToList(),
			book.Narrators.Select(n => n.Name).ToList(),
			book.SeriesLink.Select(s => new SeriesDto(s.Series.Name ?? "", s.Order, s.Index)).ToList(),
			book.LengthInMinutes,
			book.UserDefinedItem.BookStatus.ToString(),
			book.UserDefinedItem.PdfStatus?.ToString(),
			lb.DateAdded,
			lb.Account,
			book.Locale,
			book.ContentType.ToString(),
			book.ContentType == ContentType.Episode);
	}
}
