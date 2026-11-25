namespace LibationEngine.Contracts;

public sealed record StatusDto(
	string State,
	DateTimeOffset? LastRun,
	DateTimeOffset? LastSuccess,
	int? ScanExitCode,
	int? LiberateExitCode,
	DateTimeOffset? NextRun,
	int IntervalSeconds,
	string? Version);

public sealed record AccountDto(string AccountId, string? AccountName, string? Locale, bool LibraryScan);

public sealed record SeriesDto(string Name, string? Order, float Index);

public sealed record BookDto(
	string Asin,
	string Title,
	string? Subtitle,
	IReadOnlyList<string> Authors,
	IReadOnlyList<string> Narrators,
	IReadOnlyList<SeriesDto> Series,
	int LengthInMinutes,
	string BookStatus,
	string? PdfStatus,
	DateTime DateAdded,
	string Account,
	string Locale,
	string ContentType,
	bool IsPodcastEpisode);

public sealed record LibraryPageDto(int Total, int Offset, int Limit, IReadOnlyList<BookDto> Items);

public sealed record ErrorDto(string Error);
