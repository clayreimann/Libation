using DataLayer;
using LibationFileManager;

namespace LibationEngine;

public enum QueueNoticeKind
{
	DiskFull,
	LicenseGuidance,
	WidevineGuidance,
	DailyLimitPaused
}

/// <summary>Something the queue wants the user told, once per run.</summary>
/// <param name="Allowance">Set only for <see cref="QueueNoticeKind.DailyLimitPaused"/>.</param>
public sealed record QueueNotice(
	QueueNoticeKind Kind,
	ProcessBookResult Result,
	LibraryBook LibraryBook,
	DailyDownloadLimit.Allowance? Allowance = null);
