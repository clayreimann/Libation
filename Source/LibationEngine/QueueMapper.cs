using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LibationEngine.Contracts;
using LibationFileManager;

namespace LibationEngine;

/// <summary>Queue state and decisions to and from their wire shapes; camelCase names, no secrets.</summary>
public static class QueueMapper
{
	public static string Name<TEnum>(TEnum value) where TEnum : struct, Enum
		=> JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

	public static QueueJobDto ToDto(LiberationJob job, string state)
		=> new(job.Id.Value, job.LibraryBook.Book.AudibleProductId, job.LibraryBook.Book.TitleWithSubtitle, state);

	public static PendingDecisionDto ToDto(PendingDecision pause)
		=> new(pause.Id.Value, Name(pause.Reason), pause.Job?.Value, Allowed(pause.Allowed));

	public static QueueWaitDto ToDto(QueueWait wait)
		=> new(
			wait.Id.Value,
			Name(wait.Reason),
			wait.Since,
			wait.NextCheck,
			wait.Job?.Value,
			Allowed(wait.Allowed),
			wait.Volumes?.Select(v => new VolumeDto(v.Root, v.FreeBytes, v.RequiredBytes)).ToList());

	/// <summary>Running and waiting jobs, then queued ones in dispatch order.</summary>
	public static QueueDto Snapshot(LiberationQueue queue)
	{
		var active = queue.Jobs.GetActive();
		var finished = queue.Jobs.GetCompleted().ToHashSet();
		var jobs = active.Select(j => ToDto(j, "working"))
			.Concat(queue.Jobs.GetAllItems()
				.Where(j => !finished.Contains(j) && !active.Contains(j))
				.Select(j => ToDto(j, "queued")))
			.ToList();

		return new(
			queue.Running,
			jobs,
			queue.Pending.Select(ToDto).ToList(),
			queue.Waiting is { } wait ? ToDto(wait) : null,
			queue.DispatchPaused,
			ToDto(queue.Stats()));
	}

	public static QueueStatsDto ToDto(QueueStats stats) => new(stats.Queued, stats.Completed, stats.Errors, stats.RunningSince);

	public static FinishedJobDto ToDto(FinishedJob job)
		=> new(job.Id.Value, job.Asin, job.Title, Name(job.Result), Name(job.Status), job.FinishedAt);

	public static JobLogLineDto ToDto(JobLogLine line) => new(line.At, Name(line.Level), line.Message);

	public static JobLogDto ToDto(JobId id, JobLog log)
		=> new(id.Value, log.Dropped, log.Snapshot().Select(ToDto).ToList());

	public static bool TryParse(MoveRequest? request, out QueuePosition position)
	{
		position = default;
		switch (request?.To?.Trim().ToLowerInvariant())
		{
			case "top": position = QueuePosition.First; return true;
			case "up": position = QueuePosition.OneUp; return true;
			case "down": position = QueuePosition.OneDown; return true;
			case "bottom": position = QueuePosition.Last; return true;
			default: return false;
		}
	}

	/// <summary>Parses a decision body; <paramref name="error"/> says what was wrong when it returns false.</summary>
	public static bool TryParse(DecisionRequest? request, [NotNullWhen(true)] out Decision? decision, [NotNullWhen(false)] out string? error)
	{
		decision = null;
		error = null;

		switch (request?.Decision?.Trim().ToLowerInvariant())
		{
			case "continue":
				decision = Decision.Continue;
				return true;
			case "stop":
				decision = Decision.Stop;
				return true;
			case "resume":
				decision = Decision.Resume;
				return true;
			case "badbook":
				var action = request.Action?.Trim().ToLowerInvariant() switch
				{
					"retry" => Configuration.BadBookAction.Retry,
					"ignore" => Configuration.BadBookAction.Ignore,
					"abort" => Configuration.BadBookAction.Abort,
					_ => (Configuration.BadBookAction?)null
				};
				if (action is null)
				{
					error = "badBook needs an action of retry, ignore or abort.";
					return false;
				}
				decision = Decision.BadBook(action.Value, request.ApplyToAll, request.Remember);
				return true;
			default:
				error = "decision must be one of continue, stop, resume, badBook.";
				return false;
		}
	}

	private static IReadOnlyList<string> Allowed(IReadOnlySet<DecisionKind> kinds)
		=> kinds.Select(Name).Order().ToList();
}
