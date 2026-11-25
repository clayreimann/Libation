using System;
using System.Collections.Generic;
using System.Linq;

namespace LibationEngine;

public sealed record FinishedJob(
	JobId Id,
	string Asin,
	string Title,
	ProcessBookResult Result,
	ProcessBookStatus Status,
	DateTimeOffset FinishedAt,
	JobLog Log);

/// <summary>The most recently finished jobs, newest last; the oldest fall off at <see cref="Capacity"/>.</summary>
public sealed class QueueHistory
{
	public const int DefaultCapacity = 200;

	private readonly object _gate = new();
	private readonly List<FinishedJob> _jobs = [];

	public int Capacity { get; }

	public QueueHistory(int capacity = DefaultCapacity) => Capacity = capacity;

	internal void Add(LiberationJob job, DateTimeOffset finishedAt)
	{
		var entry = new FinishedJob(
			job.Id,
			job.LibraryBook.Book.AudibleProductId,
			job.LibraryBook.Book.TitleWithSubtitle,
			job.Result,
			job.Status,
			finishedAt,
			job.Log);

		lock (_gate)
		{
			_jobs.RemoveAll(j => j.Id == entry.Id);
			_jobs.Add(entry);
			if (_jobs.Count > Capacity)
				_jobs.RemoveRange(0, _jobs.Count - Capacity);
		}
	}

	/// <summary>Newest first.</summary>
	public IReadOnlyList<FinishedJob> Snapshot()
	{
		lock (_gate)
			return _jobs.AsEnumerable().Reverse().ToList();
	}

	public FinishedJob? Find(JobId id)
	{
		lock (_gate)
			return _jobs.FirstOrDefault(j => j.Id == id);
	}

	public void Clear()
	{
		lock (_gate)
			_jobs.Clear();
	}
}
