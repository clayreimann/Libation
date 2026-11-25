using System.Text.RegularExpressions;
using AAXClean;
using Mpeg4Lib.ID3;
using Serilog;

namespace LibationEngine;

/// <summary>StartSec runs across the whole book; FileStartSec is within file FileIndex.</summary>
public sealed record BookChapter(int Index, string Title, double StartSec, double? DurationSec, int FileIndex, double FileStartSec);

public static partial class AudioChapters
{
	private sealed record Part(string Title, double Start, double? Duration);

	/// <summary>Per file: embedded chapters, else the .cue (single-file books only), else the whole file as one chapter.</summary>
	public static IReadOnlyList<BookChapter> Build(IReadOnlyList<AudioFileInfo> files, string? cuePath)
	{
		var chapters = new List<BookChapter>();
		var offset = 0d;
		foreach (var file in files)
		{
			var spans = Embedded(file) ?? (files.Count == 1 ? FromCueFile(cuePath, file.DurationSec) : null)
				?? [new Part(Path.GetFileNameWithoutExtension(file.Name), 0, file.DurationSec)];
			foreach (var span in spans)
				chapters.Add(new(chapters.Count, span.Title, offset + span.Start, span.Duration, file.Index, span.Start));
			offset += file.DurationSec ?? 0;
		}
		return chapters;
	}

	private static List<Part>? Embedded(AudioFileInfo file)
	{
		try
		{
			var spans = Path.GetExtension(file.Path).ToLowerInvariant() switch
			{
				".m4b" or ".m4a" or ".mp4" => FromMp4(file.Path),
				".mp3" => FromMp3(file.Path, file.DurationSec),
				_ => null
			};
			return spans is { Count: > 0 } ? spans : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException or NullReferenceException or InvalidOperationException)
		{
			Log.Logger.Warning(ex, "Could not read the chapters of {File}", file.Path);
			return null;
		}
	}

	private static List<Part>? FromMp4(string path)
	{
		using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var file = new Mp4File(stream);
		return file.GetChaptersFromMetadata()?.Chapters
			.Select(c => new Part(c.Title, c.StartOffset.TotalSeconds, c.Duration.TotalSeconds))
			.ToList();
	}

	private static List<Part>? FromMp3(string path, double? fileDuration)
	{
		using var stream = File.OpenRead(path);
		if (Id3Tag.Create(stream) is not { } tag)
			return null;

		var frames = tag.Children.OfType<CHAPFrame>().OrderBy(c => c.StartTime).ToList();
		if (frames.Count > 0)
			return frames.Select((c, i) => new Part(
				c.Children.OfType<TEXTFrame>().FirstOrDefault(t => t.Header.Identifier == "TIT2")?.Text?.Trim('\0', ' ') is { Length: > 0 } title ? title : $"Chapter {i + 1}",
				c.StartTime.TotalSeconds,
				(c.EndTime - c.StartTime).TotalSeconds)).ToList();

		var cue = tag.Children.OfType<TXXXFrame>().FirstOrDefault(f => f.FieldName?.Trim('\0', ' ') == "CUESHEET")?.FieldValue;
		return cue is null ? null : ParseCue(cue, fileDuration);
	}

	private static List<Part>? FromCueFile(string? cuePath, double? fileDuration)
	{
		if (cuePath is null || !File.Exists(cuePath))
			return null;
		try
		{
			return ParseCue(File.ReadAllText(cuePath), fileDuration);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Log.Logger.Warning(ex, "Could not read {Cue}", cuePath);
			return null;
		}
	}

	/// <summary>Reads the TRACK, TITLE and INDEX 01 lines Libation writes; null when no track has a start.</summary>
	private static List<Part>? ParseCue(string text, double? fileDuration)
	{
		var starts = new List<(string Title, double Start)>();
		string? title = null;
		var track = false;
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.Trim(' ', '\t', '\r', '\0');
			if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
			{
				track = true;
				title = null;
			}
			else if (track && line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
			{
				var open = line.IndexOf('"');
				var close = line.LastIndexOf('"');
				title = open >= 0 && close > open ? line[(open + 1)..close] : line[6..].Trim();
			}
			else if (track && CueIndex().Match(line) is { Success: true } m)
			{
				var start = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value) / 75d;
				starts.Add((title is { Length: > 0 } ? title : $"Chapter {starts.Count + 1}", start));
				track = false;
			}
		}
		if (starts.Count == 0)
			return null;

		starts.Sort((a, b) => a.Start.CompareTo(b.Start));
		return starts.Select((s, i) => new Part(
			s.Title,
			s.Start,
			i + 1 < starts.Count ? starts[i + 1].Start - s.Start : fileDuration is { } total ? Math.Max(0, total - s.Start) : null)).ToList();
	}

	[GeneratedRegex(@"^INDEX\s+01\s+(\d+):(\d{2}):(\d{2})$", RegexOptions.IgnoreCase)]
	private static partial Regex CueIndex();
}
