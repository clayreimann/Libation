namespace LibationEngine.Tests;

internal sealed class TempDir : IDisposable
{
	public string Path { get; } = Directory.CreateTempSubdirectory("libationengine-").FullName;

	public string File(string name) => System.IO.Path.Combine(Path, name);

	public void Dispose() => Directory.Delete(Path, recursive: true);
}
