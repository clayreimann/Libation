using ApplicationServices;
using DataLayer;
using LibationFileManager;

namespace LibationEngine;

public static class AudiobookshelfUploadJob
{
	/// <summary>An upload-only job for an already-liberated book; null when the ASIN is not in the library.</summary>
	public static LiberationJob? Create(LiberationQueue queue, string asin)
		=> DbContexts.GetLibraryBook_Flat_NoTracking(asin, caseSensative: false) is { } book
			? new LiberationJob(book, Configuration.Instance, queue.BadBookSession).AddUploadToAudiobookshelf()
			: null;

	public static bool IsLiberated(LiberationJob job) => job.LibraryBook.Book.UserDefinedItem.BookStatus == LiberatedStatus.Liberated;
}
