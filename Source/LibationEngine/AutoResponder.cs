using LibationFileManager;
using System.IO;

namespace LibationEngine;

/// <summary>Answers every pause without a person: guidance continues, a bad book is left for a later retry, a full disk stops.</summary>
public sealed class AutoResponder
{
	private readonly LiberationQueue _queue;
	private readonly LiberationPolicy _policy;
	private readonly TextWriter _warnings;

	public AutoResponder(LiberationQueue queue, LiberationPolicy policy, TextWriter warnings)
	{
		_queue = queue;
		_policy = policy;
		_warnings = warnings;
		_queue.QueuePaused += (_, pause) => Answer(pause);
		foreach (var pause in _queue.Pending)
			Answer(pause);
	}

	private void Answer(PendingDecision pause)
	{
		var title = pause.Job is { } id ? _queue.FindJob(id)?.LibraryBook.Book.TitleWithSubtitle : null;
		var subject = title is null ? "" : $" ({title})";

		Decision decision;
		switch (pause.Reason)
		{
			case PauseReason.BadBook:
				_warnings.WriteLine($"Book failed{subject}; leaving it to retry on the next run.");
				decision = Decision.BadBook(Configuration.BadBookAction.Retry, applyToAll: true);
				break;
			case PauseReason.DiskFull:
				_warnings.WriteLine($"Disk full{subject}; stopping the queue.");
				decision = _policy.OnDiskFull == DiskFullPolicy.WaitForSpace ? Decision.Resume : Decision.Stop;
				break;
			default:
				_warnings.WriteLine($"{pause.Reason}{subject}; continuing.");
				decision = Decision.Continue;
				break;
		}

		_queue.Resume(pause.Id, decision);
	}
}
