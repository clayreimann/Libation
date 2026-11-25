using System.Collections.Concurrent;
using LibationEngine.Contracts;

namespace LibationEngine;

/// <summary>Publishes a queue's per-book and pause events to an <see cref="EventBroadcaster"/>; progress at most once per interval per book.</summary>
public sealed class QueueEventPublisher
{
	public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

	/// <summary>Info lines per book per second; warnings and errors are never held back.</summary>
	public const int InfoLinesPerSecond = 20;

	private readonly EventBroadcaster _events;
	private readonly TimeProvider _time;
	private readonly ConcurrentDictionary<JobId, long> _lastProgress = new();
	private readonly Dictionary<JobId, (long Start, int Count)> _logWindows = [];

	public QueueEventPublisher(LiberationQueue queue, EventBroadcaster events, TimeProvider? time = null)
	{
		_events = events;
		_time = time ?? TimeProvider.System;

		queue.JobStarted += (_, job) => Started(job);
		queue.JobEnded += (_, job) => Ended(job);
		queue.DispatchPauseChanged += (_, _) => events.Publish(queue.DispatchPaused ? new QueueDispatchPaused() : new QueueDispatchResumed());
		queue.QueuePaused += (_, pause) => events.Publish(new QueuePaused(QueueMapper.ToDto(pause)));
		queue.QueueResumed += (_, resolved) => events.Publish(new QueueResumed(resolved.Id.Value, QueueMapper.Name(resolved.Decision.Kind)));
		queue.QueueWaiting += (_, wait) => events.Publish(new QueueWaiting(QueueMapper.ToDto(wait)));
		queue.QueueWaitEnded += (_, wait) => events.Publish(new QueueWaitEnded(wait.Id.Value, QueueMapper.Name(wait.Reason)));
	}

	private void Started(LiberationJob job)
	{
		var (id, asin) = (job.Id, job.LibraryBook.Book.AudibleProductId);
		_lastProgress.TryRemove(id, out _);
		job.ProgressChanged -= OnProgress;
		job.ProgressChanged += OnProgress;
		job.LineLogged -= OnLine;
		job.LineLogged += OnLine;
		lock (_logWindows)
			_logWindows.Remove(id);
		_events.Publish(new BookStarted(id.Value, asin, job.LibraryBook.Book.TitleWithSubtitle));
	}

	private void OnProgress(object? sender, int percent)
	{
		if (sender is not LiberationJob job)
			return;

		var now = _time.GetTimestamp();
		if (_lastProgress.TryGetValue(job.Id, out var last) && _time.GetElapsedTime(last, now) < ProgressInterval)
			return;

		_lastProgress[job.Id] = now;
		_events.Publish(new BookProgress(job.Id.Value, job.LibraryBook.Book.AudibleProductId, percent));
	}

	private void OnLine(object? sender, JobLogLine line)
	{
		if (sender is not LiberationJob job || !AllowLine(job.Id, line))
			return;

		_events.Publish(new BookLog(job.Id.Value, QueueMapper.Name(line.Level), line.Message) { At = line.At });
	}

	private bool AllowLine(JobId id, JobLogLine line)
	{
		if (line.Level != JobLogLevel.Info)
			return true;

		var now = _time.GetTimestamp();
		lock (_logWindows)
		{
			var (start, count) = _logWindows.TryGetValue(id, out var window) ? window : (now, 0);
			if (_time.GetElapsedTime(start, now) >= TimeSpan.FromSeconds(1))
				(start, count) = (now, 0);
			_logWindows[id] = (start, count + 1);
			return count < InfoLinesPerSecond;
		}
	}

	private void Ended(LiberationJob job)
	{
		job.ProgressChanged -= OnProgress;
		job.LineLogged -= OnLine;
		lock (_logWindows)
			_logWindows.Remove(job.Id);
		_lastProgress.TryRemove(job.Id, out _);
		_events.Publish(new BookFinished(
			job.Id.Value,
			job.LibraryBook.Book.AudibleProductId,
			job.LibraryBook.Book.TitleWithSubtitle,
			QueueMapper.Name(job.Result),
			QueueMapper.Name(job.Status)));
	}
}
