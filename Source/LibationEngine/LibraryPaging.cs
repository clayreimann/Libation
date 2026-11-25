namespace LibationEngine;

public static class LibraryPaging
{
	public const int DefaultLimit = 100;
	public const int MaxLimit = 1000;
	public const int MaxAsins = 50_000;

	public static (int Offset, int Limit) Clamp(int? offset, int? limit)
		=> (Math.Max(offset ?? 0, 0), Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));
}
