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
	bool IsPodcastEpisode,
	string Tags,
	bool InTrash);

public sealed record LibraryPageDto(int Total, int Offset, int Limit, IReadOnlyList<BookDto> Items);

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
