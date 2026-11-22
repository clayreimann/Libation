using ApplicationServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static ApplicationServices.AudiobookshelfProgressSync;

namespace ApplicationServices.Tests;

/// <summary>Drives <see cref="AudiobookshelfProgressSync.PushAsync"/> against an in-process stub of the Audiobookshelf API.</summary>
[TestClass]
public class AudiobookshelfProgressPushTests
{
	private const string ItemsJson = """
		{ "results": [
			{ "id": "li_new", "mediaType": "book", "media": { "duration": 1000, "metadata": { "title": "New", "asin": "B0NEW" } } },
			{ "id": "li_newer", "mediaType": "book", "media": { "duration": 1000, "metadata": { "title": "Newer On Server", "asin": "B0NEWER" } } }
		] }
		""";

	private const string MeJson = """
		{ "mediaProgress": [ { "libraryItemId": "li_newer", "currentTime": 10, "isFinished": false, "lastUpdate": 4102444800000 } ] }
		""";

	private static ListeningProgress Progress(string asin, string title)
		=> new(asin, title, null, "acct", "us", 250_000, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, false);

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_patches_matched_items_and_reports_each_outcome()
	{
		using var stub = new StubServer();
		var progress = new[] { Progress("B0NEW", "New"), Progress("B0NEWER", "Newer On Server"), Progress("B0MISSING", "Missing") };

		var results = await PushAsync(stub.BaseUrl, "token-123", "lib_1", progress, force: false, dryRun: false);

		CollectionAssert.AreEqual(
			new[] { Outcome.Updated, Outcome.ServerIsNewer, Outcome.NotOnServer },
			results.Select(r => r.Outcome).ToArray());

		var patch = stub.Requests.Single(r => r.Method == "PATCH");
		Assert.AreEqual("/api/me/progress/li_new", patch.Path);
		Assert.AreEqual("Bearer token-123", patch.Authorization);
		var body = JObject.Parse(patch.Body);
		Assert.AreEqual(250d, body.Value<double>("currentTime"));
		Assert.AreEqual(0.25, body.Value<double>("progress"));

		Assert.IsTrue(stub.Requests.Any(r => r.Method == "GET" && r.Path == "/api/libraries/lib_1/items" && r.Query.Contains("limit=0")));
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_dry_run_writes_nothing()
	{
		using var stub = new StubServer();

		var results = await PushAsync(stub.BaseUrl, "t", "lib_1", [Progress("B0NEW", "New")], force: false, dryRun: true);

		Assert.AreEqual(Outcome.WouldUpdate, results.Single().Outcome);
		Assert.IsFalse(stub.Requests.Any(r => r.Method != "GET"));
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task PushAsync_reports_server_error_as_failed_without_throwing()
	{
		using var stub = new StubServer(patchStatus: 500);

		var results = await PushAsync(stub.BaseUrl, "t", "lib_1", [Progress("B0NEW", "New")], force: false, dryRun: false);

		Assert.AreEqual(Outcome.Failed, results.Single().Outcome);
		StringAssert.Contains(results.Single().Message, "500");
	}

	private sealed record Request(string Method, string Path, string Query, string? Authorization, string Body);

	private sealed class StubServer : IDisposable
	{
		private readonly HttpListener listener = new();
		private readonly int patchStatus;
		public ConcurrentQueue<Request> Requests { get; } = new();
		public string BaseUrl { get; }

		public StubServer(int patchStatus = 200)
		{
			this.patchStatus = patchStatus;
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();

			BaseUrl = $"http://127.0.0.1:{port}";
			listener.Prefixes.Add(BaseUrl + "/");
			listener.Start();
			_ = Task.Run(LoopAsync);
		}

		private async Task LoopAsync()
		{
			while (listener.IsListening)
			{
				HttpListenerContext ctx;
				try { ctx = await listener.GetContextAsync(); }
				catch { return; }

				using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
				var body = await reader.ReadToEndAsync();
				var path = ctx.Request.Url!.AbsolutePath;
				Requests.Enqueue(new(ctx.Request.HttpMethod, path, ctx.Request.Url.Query, ctx.Request.Headers["Authorization"], body));

				var (status, json) = (ctx.Request.HttpMethod, path) switch
				{
					("GET", "/api/libraries/lib_1/items") => (200, ItemsJson),
					("GET", "/api/me") => (200, MeJson),
					("PATCH", _) => (patchStatus, patchStatus == 200 ? "{}" : "boom"),
					_ => (404, "not found"),
				};

				var bytes = Encoding.UTF8.GetBytes(json);
				ctx.Response.StatusCode = status;
				ctx.Response.ContentType = "application/json";
				await ctx.Response.OutputStream.WriteAsync(bytes);
				ctx.Response.Close();
			}
		}

		public void Dispose()
		{
			listener.Stop();
			listener.Close();
		}
	}
}
