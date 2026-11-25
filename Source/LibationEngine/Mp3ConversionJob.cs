using ApplicationServices;
using DataLayer;
using FileLiberator;
using LibationFileManager;

namespace LibationEngine;

public enum ConvertOutcome { Ok, NotFound, Conflict }

public sealed record ConvertJobResult(ConvertOutcome Outcome, LiberationJob? Job = null);

public static class Mp3ConversionJob
{
	/// <summary>A convert-only job; Conflict unless the book passes desktop's Convert to Mp3 filter and has an m4b without an mp3.</summary>
	public static ConvertJobResult Create(LiberationQueue queue, string asin, Func<LibraryBook, bool>? hasM4bToConvert = null)
	{
		if (DbContexts.GetLibraryBook_Flat_NoTracking(asin, caseSensative: false) is not { } book)
			return new(ConvertOutcome.NotFound);
		return IsConvertible(book, hasM4bToConvert ?? ConvertToMp3.ValidateMp3)
			? new(ConvertOutcome.Ok, new LiberationJob(book, Configuration.Instance, queue.BadBookSession).AddConvertToMp3())
			: new(ConvertOutcome.Conflict);
	}

	public static bool IsConvertible(LibraryBook book, Func<LibraryBook, bool> hasM4bToConvert)
		=> !book.AbsentFromLastScan
			&& book.Book.ContentType is ContentType.Product
			&& book.Book.UserDefinedItem.BookStatus is LiberatedStatus.Liberated
			&& hasM4bToConvert(book);
}
