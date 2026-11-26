using ApplicationServices;
using DataLayer;
using LibationFileManager;

namespace LibationEngine;

public static class ProgressPushJob
{
	/// <summary>A push-only job for a liberated book; null when the ASIN is not in the library.</summary>
	public static LiberationJob? Create(LiberationQueue queue, string asin, bool force)
		=> DbContexts.GetLibraryBook_Flat_NoTracking(asin, caseSensative: false) is { } book
			? new LiberationJob(book, Configuration.Instance, queue.BadBookSession).AddPushProgressToAudiobookshelf(force)
			: null;
}
