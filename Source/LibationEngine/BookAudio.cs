using AAXClean;
using Serilog;

namespace LibationEngine;

public enum AudioOutcome { Ok, NoSuchBook, NotLiberated, FilesMissing }

/// <summary>Paths are the book's audio files in play order, and CuePath its .cue next to the book, when Outcome is Ok.</summary>
public sealed record AudioLookup(AudioOutcome Outcome, IReadOnlyList<string>? Paths = null, string? CuePath = null);

/// <summary>DurationSec is null when the format has no reader.</summary>
public sealed record AudioFileInfo(int Index, string Path, string Name, string ContentType, long SizeBytes, double? DurationSec);

public static class AudioFiles
{
	private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		[".m4b"] = "audio/mp4",
		[".m4a"] = "audio/mp4",
		[".mp4"] = "audio/mp4",
		[".mp3"] = "audio/mpeg",
		[".aac"] = "audio/aac",
		[".flac"] = "audio/flac",
		[".ogg"] = "audio/ogg"
	};

	public static string ContentType(string path)
		=> ContentTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";

	/// <summary>The files sharing the primary file's folder and extension, in natural name order; a split book is the whole set.</summary>
	public static IReadOnlyList<string> PlayOrder(string primary, IEnumerable<string> candidates)
	{
		var folder = Path.GetDirectoryName(primary);
		var extension = Path.GetExtension(primary);
		var set = candidates.Append(primary)
			.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.Ordinal)
				&& string.Equals(Path.GetExtension(p), extension, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.Ordinal)
			.ToList();
		set.Sort((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
		return set;
	}

	public static IReadOnlyList<AudioFileInfo> Describe(IReadOnlyList<string> paths)
		=> paths.Select((p, i) => new AudioFileInfo(i, p, Path.GetFileName(p), ContentType(p), new FileInfo(p).Length, ReadDuration(p))).ToList();

	public static double? ReadDuration(string path)
	{
		try
		{
			return Path.GetExtension(path).ToLowerInvariant() switch
			{
				".m4b" or ".m4a" or ".mp4" => Mp4Duration(path),
				".mp3" => Mp3Duration.Read(path),
				_ => null
			};
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException or NullReferenceException or InvalidOperationException)
		{
			Log.Logger.Warning(ex, "Could not read the duration of {File}", path);
			return null;
		}
	}

	private static double? Mp4Duration(string path)
	{
		using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var file = new Mp4File(stream);
		return file.Duration.TotalSeconds;
	}

	private static int NaturalCompare(string a, string b)
	{
		int i = 0, j = 0;
		while (i < a.Length && j < b.Length)
		{
			if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
			{
				var ei = i;
				while (ei < a.Length && char.IsDigit(a[ei]))
					ei++;
				var ej = j;
				while (ej < b.Length && char.IsDigit(b[ej]))
					ej++;
				var left = a[i..ei].TrimStart('0');
				var right = b[j..ej].TrimStart('0');
				var byLength = left.Length.CompareTo(right.Length);
				if (byLength != 0)
					return byLength;
				var byDigits = string.CompareOrdinal(left, right);
				if (byDigits != 0)
					return byDigits;
				i = ei;
				j = ej;
			}
			else
			{
				var byChar = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
				if (byChar != 0)
					return byChar;
				i++;
				j++;
			}
		}
		var rest = (a.Length - i).CompareTo(b.Length - j);
		return rest != 0 ? rest : string.CompareOrdinal(a, b);
	}
}

/// <summary>Reads an mp3's length from its Xing, Info or VBRI frame count, else from its bitrate and size.</summary>
internal static class Mp3Duration
{
	private static readonly short[] V1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
	private static readonly short[] V2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
	private static readonly int[][] SampleRates = [[11025, 12000, 8000], [], [22050, 24000, 16000], [44100, 48000, 32000]];

	public static double? Read(string path)
	{
		using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		var length = stream.Length;
		var start = Id3v2Length(stream);
		var scan = new byte[Math.Min(65536, length - start)];
		stream.Position = start;
		stream.ReadExactly(scan);

		for (var at = 0; at + 4 <= scan.Length; at++)
		{
			if (scan[at] != 0xFF || (scan[at + 1] & 0xE0) != 0xE0)
				continue;
			var version = (scan[at + 1] >> 3) & 3;
			var layer = (scan[at + 1] >> 1) & 3;
			var bitrateIndex = scan[at + 2] >> 4;
			var rateIndex = (scan[at + 2] >> 2) & 3;
			if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
				continue;

			var sampleRate = SampleRates[version][rateIndex];
			var kbps = (version == 3 ? V1Bitrates : V2Bitrates)[bitrateIndex];
			var mono = (scan[at + 3] >> 6) == 3;
			var crc = (scan[at + 1] & 1) == 0 ? 2 : 0;
			var samplesPerFrame = version == 3 ? 1152 : 576;
			var sideInfo = version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);

			if ((XingFrames(scan, at + 4 + crc + sideInfo) ?? VbriFrames(scan, at + 4 + 32)) is { } frames)
				return (double)frames * samplesPerFrame / sampleRate;

			var audioEnd = length;
			if (length - start > 128)
			{
				stream.Position = length - 128;
				var tail = new byte[3];
				stream.ReadExactly(tail);
				if (tail[0] == 'T' && tail[1] == 'A' && tail[2] == 'G')
					audioEnd -= 128;
			}
			return (audioEnd - (start + at)) * 8d / (kbps * 1000d);
		}
		return null;
	}

	private static uint? XingFrames(byte[] data, int at)
		=> at + 12 <= data.Length && (Matches(data, at, "Xing") || Matches(data, at, "Info")) && (data[at + 7] & 1) != 0
			? ReadUInt32(data, at + 8)
			: null;

	private static uint? VbriFrames(byte[] data, int at)
		=> at + 18 <= data.Length && Matches(data, at, "VBRI") ? ReadUInt32(data, at + 14) : null;

	private static bool Matches(byte[] data, int at, string tag)
		=> at + tag.Length <= data.Length && tag.Select((c, i) => data[at + i] == c).All(b => b);

	private static uint ReadUInt32(byte[] data, int at)
		=> (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);

	private static long Id3v2Length(Stream stream)
	{
		var header = new byte[10];
		if (stream.Read(header, 0, 10) < 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3')
			return 0;
		var size = header[6] << 21 | header[7] << 14 | header[8] << 7 | header[9];
		return 10 + size + ((header[5] & 0x10) != 0 ? 10 : 0);
	}
}
