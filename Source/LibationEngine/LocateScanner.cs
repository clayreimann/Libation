using ApplicationServices;
using DataLayer;
using LibationEngine.Contracts;
using LibationFileManager;
using Serilog;

namespace LibationEngine;

/// <summary>Background folder scan matching embedded ASINs to library books, as desktop's Locate audiobooks.</summary>
public sealed class LocateScanner
{
	private readonly object gate = new();
	private LocateScanDto current = new("idle", null, 0, 0, 0, null, null, null);
	private bool running;

	public LocateScanDto Current
	{
		get { lock (gate) return current; }
	}

	/// <summary>False when a scan is already running.</summary>
	public bool TryStart(string folder, Action<EngineEvent> publish)
	{
		lock (gate)
		{
			if (running)
				return false;
			running = true;
			current = new("running", folder, 0, 0, 0, null, DateTimeOffset.UtcNow, null);
		}
		publish(new LocateScanProgress(current));
		_ = Task.Run(() => RunAsync(folder, publish));
		return true;
	}

	private void Update(Func<LocateScanDto, LocateScanDto> change, Action<EngineEvent> publish)
	{
		LocateScanDto next;
		lock (gate)
			next = current = change(current);
		publish(new LocateScanProgress(next));
	}

	private async Task RunAsync(string folder, Action<EngineEvent> publish)
	{
		try
		{
			await foreach (var entry in AudioFileStorage.FindAudiobooksAsync(folder, CancellationToken.None))
			{
				FilePathCache.Insert(entry);
				var matched = 0;
				var updated = 0;
				var lb = DbContexts.GetLibraryBook_Flat_NoTracking(entry.Id);
				if (lb is not null)
				{
					matched = 1;
					if (lb.Book.UserDefinedItem.BookStatus is not LiberatedStatus.Liberated)
					{
						await lb.UpdateBookStatusAsync(LiberatedStatus.Liberated);
						updated = 1;
					}
				}
				Update(c => c with { Found = c.Found + 1, Matched = c.Matched + matched, Updated = c.Updated + updated }, publish);
			}
			Finish("completed", null, publish);
		}
		catch (Exception ex)
		{
			Log.Logger.Error(ex, "Locate scan of {Folder} failed", folder);
			Finish("failed", ex.Message, publish);
		}
	}

	private void Finish(string state, string? error, Action<EngineEvent> publish)
	{
		LocateScanDto last;
		lock (gate)
		{
			last = current = current with { State = state, Error = error, FinishedAt = DateTimeOffset.UtcNow };
			running = false;
		}
		publish(new LocateScanProgress(last));
	}
}
