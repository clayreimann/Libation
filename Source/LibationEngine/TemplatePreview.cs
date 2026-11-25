using DataLayer;
using FileLiberator;
using FileManager;
using LibationEngine.Contracts;
using LibationFileManager;
using LibationFileManager.Templates;

namespace LibationEngine;

/// <summary>Renders a naming template as the desktop template editor does.</summary>
public static class TemplatePreview
{
	public const string Folder = "folder", File = "file", ChapterFile = "chapterFile", ChapterTitle = "chapterTitle";

	public static readonly IReadOnlyList<string> Kinds = [Folder, File, ChapterFile, ChapterTitle];

	/// <summary>Uses the sample book when <paramref name="book"/> is null.</summary>
	public static TemplatePreviewDto Render(Configuration config, string kind, string text, LibraryBook? book)
		=> kind switch
		{
			Folder => RenderPath<Templates.FolderTemplate>(config, text, book),
			File => RenderPath<Templates.FileTemplate>(config, text, book),
			ChapterFile => RenderPath<Templates.ChapterFileTemplate>(config, text, book),
			ChapterTitle => RenderName(text, book),
			_ => throw new ArgumentException($"Unknown template kind '{kind}'.", nameof(kind))
		};

	private static TemplatePreviewDto RenderPath<T>(Configuration config, string text, LibraryBook? book) where T : Templates, ITemplate, new()
	{
		if (Check<T>(text) is { } invalid)
			return invalid;

		LibraryBookDto? fileDto = book?.ToDto(), folderDto = fileDto;
		if (book is not null && book.Book.IsEpisodeChild() && config.SavePodcastsToParentFolder
			&& book.Book.SeriesLink.SingleOrDefault() is { } series
			&& ApplicationServices.DbContexts.GetLibraryBook_Flat_NoTracking(series.Series.AudibleSeriesId, account: book.Account) is { } parent)
			folderDto = parent.ToDto();

		var baseDir = config.Books ?? Path.GetTempPath();
		var editor = TemplateEditor<T>.CreateFilenameEditor(baseDir, text, folderDto, fileDto);
		editor.SetTemplateText(text);
		var sep = Path.DirectorySeparatorChar;
		var extension = config.DecryptToLossy ? "mp3" : "m4b";
		var preview = $"{editor.BaseDirectory.PathWithoutPrefix}{sep}{editor.GetFolderName()}{sep}{editor.GetFileName()}.{extension}";
		return new(true, [], Warnings<T>(text), preview);
	}

	private static TemplatePreviewDto RenderName(string text, LibraryBook? book)
	{
		if (Check<Templates.ChapterTitleTemplate>(text) is { } invalid)
			return invalid;
		var editor = TemplateEditor<Templates.ChapterTitleTemplate>.CreateNameEditor(text, book?.ToDto());
		editor.SetTemplateText(text);
		return new(true, [], Warnings<Templates.ChapterTitleTemplate>(text), editor.GetName());
	}

	private static TemplatePreviewDto? Check<T>(string text) where T : Templates, ITemplate, new()
	{
		if (string.IsNullOrWhiteSpace(text))
			return new(false, ["The template must not be empty."], [], null);
		Templates.TryGetTemplate<T>(text, out var template);
		return template.IsValid ? null : new(false, template.Errors.ToList(), template.Warnings.ToList(), null);
	}

	private static IReadOnlyList<string> Warnings<T>(string text) where T : Templates, ITemplate, new()
	{
		Templates.TryGetTemplate<T>(text, out var template);
		return template.Warnings.ToList();
	}
}
