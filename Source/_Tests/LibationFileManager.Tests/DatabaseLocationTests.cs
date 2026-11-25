using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace LibationFileManager.Tests;

[TestClass]
public class DatabaseLocationTests
{
	[TestMethod]
	public void Sqlite_defaults_to_the_libation_files_dir()
		=> Assert.AreEqual(Path.Combine("/files", "LibationContext.db"), SqliteStorage.Resolve("/files", null));

	[TestMethod]
	public void A_blank_override_is_ignored()
		=> Assert.AreEqual(Path.Combine("/files", "LibationContext.db"), SqliteStorage.Resolve("/files", "  "));

	[TestMethod]
	public void The_environment_override_moves_the_database()
		=> Assert.AreEqual(Path.GetFullPath("/db/lib.sqlite"), SqliteStorage.Resolve("/files", "/db/lib.sqlite"));

	[TestMethod]
	public void Connection_string_env_wins_over_the_file()
	{
		var file = Path.GetTempFileName();
		try
		{
			File.WriteAllText(file, "from-file");
			Assert.AreEqual("from-env", Configuration.ResolveConnectionString("from-env", file));
		}
		finally { File.Delete(file); }
	}

	[TestMethod]
	public void Connection_string_file_is_read_and_trimmed()
	{
		var file = Path.GetTempFileName();
		try
		{
			File.WriteAllText(file, "  Host=db;Password=p\n");
			Assert.AreEqual("Host=db;Password=p", Configuration.ResolveConnectionString(null, file));
			Assert.AreEqual("Host=db;Password=p", Configuration.ResolveConnectionString("", file));
		}
		finally { File.Delete(file); }
	}

	[TestMethod]
	public void Connection_string_is_null_without_env_or_file()
	{
		Assert.IsNull(Configuration.ResolveConnectionString(null, null));
		Assert.IsNull(Configuration.ResolveConnectionString(null, ""));
	}
}
