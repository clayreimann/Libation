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

public sealed record SeriesDto(string Name, string? Order, float Index, string Asin);

public sealed record RatingDto(float Overall, float Performance, float Story);

public record BookDto(
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
	bool IsPodcastEpisode,
	string Tags,
	bool InTrash,
	IReadOnlyList<string> Categories,
	RatingDto ProductRating,
	RatingDto MyRating,
	bool HasPdf,
	bool IsAudiblePlus,
	DateTime? IncludedUntil,
	DateTime? LastDownloaded,
	string? LastDownloadedVersion,
	string? LastDownloadedFormat,
	bool IsSpatial,
	string? PictureId,
	string? ParentAsin,
	bool AbsentFromLastScan);

/// <summary>A <see cref="BookDto"/> plus the fields too heavy for list payloads.</summary>
public sealed record BookDetailDto : BookDto
{
	public BookDetailDto(BookDto book) : base(book) { }

	public string? Description { get; init; }
	public string? Publisher { get; init; }
	public string? Language { get; init; }
	public string? Copyright { get; init; }
	public bool IsAbridged { get; init; }
	public DateTime? DatePublished { get; init; }
	public IReadOnlyList<IReadOnlyList<string>> CategoryLadders { get; init; } = [];
	public bool AudioFileExists { get; init; }
	public bool PartialDownload { get; init; }
}

/// <summary>Desktop status-bar counts; Partial has a half-downloaded file, Unavailable is absent from the last scan.</summary>
public sealed record BackupCountsDto(
	int Total,
	int Liberated,
	int Partial,
	int NotLiberated,
	int Error,
	int Unavailable,
	int PdfLiberated,
	int PdfNotLiberated,
	int PdfUnavailable);

public sealed record LibraryPageDto(int Total, int Offset, int Limit, IReadOnlyList<BookDto> Items);

/// <summary>Total counts every match; Asins is capped at 50000.</summary>
public sealed record LibraryAsinsDto(int Total, IReadOnlyList<string> Asins);

public sealed record ErrorDto(string Error);

public sealed record QueueJobDto(string Id, string Asin, string Title, string State);

public sealed record PendingDecisionDto(long Id, string Reason, string? JobId, IReadOnlyList<string> Allowed);

public sealed record VolumeDto(string Root, long? FreeBytes, long RequiredBytes);

public sealed record QueueWaitDto(
	long Id,
	string Reason,
	DateTimeOffset Since,
	DateTimeOffset? NextCheck,
	string? JobId,
	IReadOnlyList<string> Allowed,
	IReadOnlyList<VolumeDto>? Volumes);

public sealed record QueueDto(
	bool Running,
	IReadOnlyList<QueueJobDto> Jobs,
	IReadOnlyList<PendingDecisionDto> Pending,
	QueueWaitDto? Waiting);

/// <summary>Body of a decision POST; <see cref="Action"/>, <see cref="ApplyToAll"/> and <see cref="Remember"/> apply to badBook only.</summary>
public sealed record DecisionRequest(string? Decision, string? Action = null, bool ApplyToAll = false, bool Remember = false);

/// <summary>Body of <c>PUT /api/library/{asin}/tags</c>; blank clears the tags.</summary>
public sealed record TagsRequest(string? Tags);

/// <summary>Body of <c>PUT /api/library/{asin}/status</c>; Status is liberated or notLiberated.</summary>
public sealed record StatusRequest(string? Status);

/// <summary>Body of bulk library actions; at most 5000 ASINs.</summary>
public sealed record BulkRequest(IReadOnlyList<string>? Asins);

/// <summary>Body of <c>PUT /api/library/tags</c>; Tags replaces, Add and Remove edit, and Tags excludes the other two.</summary>
public sealed record BulkTagsRequest(IReadOnlyList<string>? Asins, string? Tags = null, string? Add = null, string? Remove = null);

/// <summary>Body of <c>PUT /api/library/status</c> and <c>/pdf-status</c>; Status is liberated or notLiberated.</summary>
public sealed record BulkStatusRequest(IReadOnlyList<string>? Asins, string? Status);

/// <summary>Result is ok, notFound or conflict.</summary>
public sealed record BulkItemDto(string Asin, string Result);

/// <summary>Updated counts ok items; Missing lists the notFound ASINs; Results has every ASIN in request order.</summary>
public sealed record BulkResultDto(int Updated, IReadOnlyList<string> Missing, IReadOnlyList<BulkItemDto> Results);

public sealed record NamingSettingsDto(string FolderTemplate, string FileTemplate, string ChapterFileTemplate, string ChapterTitleTemplate);

public sealed record DownloadSettingsDto(
	string Quality,
	bool OverwriteExisting,
	bool UseWidevine,
	bool RequestXheAac,
	string SpatialAudioCodec,
	bool DownloadCoverArt,
	bool DownloadClipsBookmarks,
	string ClipsBookmarksFileFormat,
	bool CreateCueSheet,
	bool SaveMetadataToFile,
	bool RetainAaxFile,
	bool SplitFilesByChapter,
	bool MergeOpeningAndEndCredits,
	bool StripUnabridged,
	bool StripAudibleBrandAudio,
	bool DecryptToLossy,
	bool MoveMoovToBeginning,
	bool CombineNestedChapterTitles,
	bool AllowLibationFixup,
	bool UseCoverAsFolderIcon,
	int MinimumFileDuration,
	string CreationTime,
	string LastWriteTime);

public sealed record QueueSettingsDto(
	int MaxConcurrentDownloads,
	long DownloadSpeedLimit,
	string DailyDownloadLimit,
	int DailyDownloadLimitQuantity,
	string DailyDownloadLimitUnit,
	string BadBook);

public sealed record LibrarySettingsDto(
	bool ImportEpisodes,
	bool ImportPlusTitles,
	bool DownloadEpisodes,
	bool AutoDownloadEpisodes,
	bool SavePodcastsToParentFolder);

public sealed record AudiobookshelfSettingsDto(bool Enabled, bool IncludePdfs, bool ServerConfigured);

/// <summary>Curated, non-secret settings; no tokens, key files or account data.</summary>
public sealed record SettingsDto(
	string? Books,
	string InProgress,
	NamingSettingsDto Naming,
	DownloadSettingsDto Download,
	QueueSettingsDto Queue,
	LibrarySettingsDto Library,
	AudiobookshelfSettingsDto Audiobookshelf);
