using ApplicationServices;
using DataLayer;
using Dinah.Core.ErrorHandling;
using System;
using System.Threading.Tasks;

namespace FileLiberator;

/// <summary>Pushes one book's Audible listening progress to Audiobookshelf; never fails the book.</summary>
public class PushProgressToAudiobookshelf : Processable
{
	public override string Name => "Push progress to Audiobookshelf";

	public bool Force { get; init; }

	/// <summary>Raised once per processed book with what happened.</summary>
	public event EventHandler<BookPushResult>? ResultDetermined;

	internal Func<LibraryBook, bool, Task<BookPushResult>>? Pusher { get; set; }

	public override bool Validate(LibraryBook libraryBook)
		=> AbsConnection.From(Configuration) is not null
			&& libraryBook.Book.UserDefinedItem.BookStatus == LiberatedStatus.Liberated;

	public override async Task<StatusHandler> ProcessAsync(LibraryBook libraryBook)
	{
		OnBegin(libraryBook);
		try
		{
			var result = await (Pusher ?? PushViaService)(libraryBook, Force);
			OnStatusUpdate(result.Pushed ? "Pushed Audible listening progress to Audiobookshelf" : $"Audible progress not pushed: {result.Message}");
			ResultDetermined?.Invoke(this, result);
		}
		catch (Exception ex)
		{
			Serilog.Log.Logger.Error(ex, "Error pushing Audible progress for {Book}; continuing as soft-failure", libraryBook.LogFriendly());
			OnStatusUpdate($"Audible progress push error: {ex.Message}");
			ResultDetermined?.Invoke(this, new(libraryBook.Book.AudibleProductId, libraryBook.Book.TitleWithSubtitle, AudiobookshelfProgressSync.Outcome.Failed, ex.Message, null, null));
		}
		finally
		{
			OnCompleted(libraryBook);
		}
		return new StatusHandler();
	}

	private async Task<BookPushResult> PushViaService(LibraryBook libraryBook, bool force)
	{
		var connection = AbsConnection.From(Configuration)
			?? throw new InvalidOperationException("Audiobookshelf is not configured");
		var results = await AudiobookshelfProgressPush.PushAsync([libraryBook], connection, force, readPositions: AudiobookshelfProgressPush.DefaultReader(allowInteractiveLogin: false));
		return results[0];
	}
}
