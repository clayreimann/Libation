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
		var finished = queue.Jobs.Completed.ToHashSet();
		var jobs = active.Select(j => ToDto(j, "working"))
			.Concat(queue.Jobs.GetAllItems()
				.Where(j => !finished.Contains(j) && !active.Contains(j))
				.Select(j => ToDto(j, "queued")))
			.ToList();

		return new(
			queue.Running,
			jobs,
			queue.Pending.Select(ToDto).ToList(),
			queue.Waiting is { } wait ? ToDto(wait) : null);
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
