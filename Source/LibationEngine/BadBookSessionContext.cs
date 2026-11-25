using LibationFileManager;

namespace LibationEngine;

/// <summary>The "apply to all" answer for the rest of one queue run.</summary>
public class BadBookSessionContext
{
	public Configuration.BadBookAction? Override { get; set; }

	public void Reset() => Override = null;
}
