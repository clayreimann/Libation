using AudibleUtilities;
using ApplicationServices;
using DataLayer;
using LibationEngine.Contracts;
using Serilog;

namespace LibationEngine;

public interface IAudibleInventory
{
	/// <summary>The <paramref name="candidates"/> missing from the Audible libraries of <paramref name="accounts"/>; throws when the scan could not see the whole library.</summary>
	Task<IReadOnlyList<LibraryBook>> FindInactiveAsync(IReadOnlyList<LibraryBook> candidates, IReadOnlyList<Account> accounts, CancellationToken ct);
}

public sealed class AudibleInventory : IAudibleInventory
{
	public async Task<IReadOnlyList<LibraryBook>> FindInactiveAsync(IReadOnlyList<LibraryBook> candidates, IReadOnlyList<Account> accounts, CancellationToken ct)
		=> await LibraryCommands.FindInactiveBooks(candidates, allowInteractiveLogin: false, [.. accounts]).WaitAsync(ct);
}

/// <summary>Background read-only scan for library books no longer in the Audible account, as desktop's Scan library and remove books.</summary>
public sealed class InactiveScanner(IAudibleInventory audible)
{
	private readonly object gate = new();
	private InactiveScanDto current = new("idle", 0, null, null, null, null, null);
	private CancellationTokenSource? cts;
	private bool running;

	public InactiveScanDto Current
	{
		get { lock (gate) return current; }
	}

	/// <summary>False when a scan is already running.</summary>
	public bool TryStart(IReadOnlyList<LibraryBook> candidates, IReadOnlyList<Account> accounts, Action<EngineEvent> publish)
	{
		CancellationToken token;
		lock (gate)
		{
			if (running)
				return false;
			running = true;
			cts = new CancellationTokenSource();
			token = cts.Token;
			current = new("running", candidates.Count, null, null, null, DateTimeOffset.UtcNow, null);
		}
		publish(new InactiveScanProgress(current));
		_ = Task.Run(() => RunAsync(candidates, accounts, publish, token));
		return true;
	}

	/// <summary>The scan as cancelling; null when none is running.</summary>
	public InactiveScanDto? Cancel(Action<EngineEvent> publish)
	{
		InactiveScanDto next;
		CancellationTokenSource? source;
		lock (gate)
		{
			if (!running)
				return null;
			source = cts;
			next = current = current with { State = "cancelling" };
		}
		publish(new InactiveScanProgress(next));
		//Outside the lock: cancelling can run the scan's continuation inline, and its Finish must not be overwritten.
		try { source?.Cancel(); }
		catch (ObjectDisposedException) { }
		return next;
	}

	private async Task RunAsync(IReadOnlyList<LibraryBook> candidates, IReadOnlyList<Account> accounts, Action<EngineEvent> publish, CancellationToken ct)
	{
		try
		{
			var inactive = await audible.FindInactiveAsync(candidates, accounts, ct);
			var results = inactive
				.OrderBy(lb => lb.Book.Title, StringComparer.OrdinalIgnoreCase)
				.ThenBy(lb => lb.Book.AudibleProductId, StringComparer.Ordinal)
				.Select(lb => new InactiveBookDto(lb.Book.AudibleProductId, lb.Book.Title, lb.Book.Authors.Select(a => a.Name).ToList(), lb.Account))
				.ToList();
			Finish("completed", null, results, publish);
		}
		catch (OperationCanceledException)
		{
			Finish("cancelled", null, null, publish);
		}
		catch (Exception ex)
		{
			Log.Logger.Error(ex, "Inactive book scan failed");
			Finish("failed", ex.Message, null, publish);
		}
	}

	private void Finish(string state, string? error, IReadOnlyList<InactiveBookDto>? results, Action<EngineEvent> publish)
	{
		InactiveScanDto last;
		lock (gate)
		{
			last = current = current with
			{
				State = state,
				Error = error,
				Results = results,
				Scanned = state == "completed" ? current.Total : null,
				FinishedAt = DateTimeOffset.UtcNow
			};
			running = false;
			cts?.Dispose();
			cts = null;
		}
		publish(new InactiveScanProgress(last with { Results = null }));
	}
}
