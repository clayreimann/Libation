using System;
using System.Collections.Generic;

namespace LibationEngine;

public enum JobLogLevel
{
	Info,
	Warning,
	Error
}

public sealed record JobLogLine(DateTimeOffset At, JobLogLevel Level, string Message);

/// <summary>A job's most recent log lines, redacted on the way in and bounded at <see cref="MaxLines"/>.</summary>
public sealed class JobLog
{
	public const int MaxLines = 500;

	private readonly object _gate = new();
	private readonly Queue<JobLogLine> _lines = new();
	private int _dropped;

	/// <summary>Lines pushed out by the bound, oldest first.</summary>
	public int Dropped
	{
		get { lock (_gate) return _dropped; }
	}

	public IReadOnlyList<JobLogLine> Snapshot()
	{
		lock (_gate)
			return [.. _lines];
	}

	internal JobLogLine Add(JobLogLevel level, string message, DateTimeOffset at)
	{
		var line = new JobLogLine(at, level, LogRedactor.Redact(message));
		lock (_gate)
		{
			_lines.Enqueue(line);
			while (_lines.Count > MaxLines)
			{
				_lines.Dequeue();
				_dropped++;
			}
		}
		return line;
	}
}
