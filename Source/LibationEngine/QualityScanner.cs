using DataLayer;
using LibationEngine.Contracts;
using Serilog;

namespace LibationEngine;

/// <summary>Background scan comparing liberated books' files with the best quality Audible now offers, as desktop's Find better quality.</summary>
public sealed class QualityScanner(IAudibleQuality audible)
{
	private readonly object gate = new();
	private QualityScanDto current = new("idle", false, 0, 0, 0, [], null, null, null);
	private CancellationTokenSource? cts;
	private bool running;

	public QualityScanDto Current
	{
		get { lock (gate) return current; }
	}

	public bool Running
	{
		get { lock (gate) return running; }
	}

	/// <summary>False when a scan is already running.</summary>
	public bool TryStart(IReadOnlyList<LibraryBook> books, bool widevine, Action<EngineEvent> publish)
	{
		CancellationToken token;
		lock (gate)
		{
			if (running)
				return false;
			running = true;
			cts = new CancellationTokenSource();
			token = cts.Token;
			current = new("running", widevine, books.Count, 0, 0, [], null, DateTimeOffset.UtcNow, null);
		}
		publish(new QualityScanProgress(current with { Results = null }));
		_ = Task.Run(() => RunAsync(books, widevine, publish, token));
		return true;
	}

	/// <summary>The scan as cancelling; null when none is running.</summary>
	public QualityScanDto? Cancel(Action<EngineEvent> publish)
	{
		QualityScanDto next;
		CancellationTokenSource? source;
		lock (gate)
		{
			if (!running)
				return null;
			source = cts;
			next = current = current with { State = "cancelling" };
		}
		publish(new QualityScanProgress(next with { Results = null }));
		//Outside the lock: cancelling can run the scan's continuation inline, and its Finish must not be overwritten.
		try { source?.Cancel(); }
		catch (ObjectDisposedException) { }
		return next;
	}

	/// <summary>Drops applied books from the results.</summary>
	public void Remove(IReadOnlySet<string> asins)
	{
		lock (gate)
			current = current with { Results = current.Results?.Where(r => !asins.Contains(r.Asin)).ToList() };
	}

	private void Update(Func<QualityScanDto, QualityScanDto> change, Action<EngineEvent> publish)
	{
		QualityScanDto next;
		lock (gate)
			next = current = change(current);
		publish(new QualityScanProgress(next with { Results = null }));
	}

	private async Task RunAsync(IReadOnlyList<LibraryBook> books, bool widevine, Action<EngineEvent> publish, CancellationToken ct)
	{
		try
		{
			foreach (var book in books)
			{
				ct.ThrowIfCancellationRequested();
				var hit = await ScanOneAsync(book, widevine, ct);
				Update(c => c with
				{
					Scanned = c.Scanned + 1,
					Failed = c.Failed + (hit.Failed ? 1 : 0),
					Results = hit.Result is { } r ? [.. c.Results ?? [], r] : c.Results
				}, publish);
			}
			Finish("completed", null, publish);
		}
		catch (OperationCanceledException)
		{
			Finish("cancelled", null, publish);
		}
		catch (Exception ex)
		{
			Log.Logger.Error(ex, "Quality scan failed");
			Finish("failed", ex.Message, publish);
		}
	}

	private async Task<(bool Failed, QualityResultDto? Result)> ScanOneAsync(LibraryBook book, bool widevine, CancellationToken ct)
	{
		var asin = book.Book.AudibleProductId;
		try
		{
			var (file, format) = QualityCheck.FindHighestExistingFormat(book);
			var last = book.Book.UserDefinedItem.LastDownloadedFormat;
			var now = file is not null ? new AudioQuality(format.CodecString, format.BitRate)
				: last is not null ? new AudioQuality(last.CodecString, last.BitRate)
				: null;
			if (now is null)
				return (true, null);

			var available = await audible.GetAvailableAsync(book, widevine, ct);
			return (false, QualityCheck.IsSignificant(now.Bitrate, available.Bitrate)
				? new QualityResultDto(asin, book.Book.Title, new(now.Codec, now.Bitrate), new(available.Codec, available.Bitrate))
				: null);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Log.Logger.Error(ex, "Error checking for better quality for {@Asin}", new { asin });
			return (true, null);
		}
	}

	private void Finish(string state, string? error, Action<EngineEvent> publish)
	{
		QualityScanDto last;
		lock (gate)
		{
			last = current = current with { State = state, Error = error, FinishedAt = DateTimeOffset.UtcNow };
			running = false;
			cts?.Dispose();
			cts = null;
		}
		publish(new QualityScanProgress(last));
	}
}
