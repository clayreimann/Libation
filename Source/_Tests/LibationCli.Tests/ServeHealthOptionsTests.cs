using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace LibationCli.Tests;

[TestClass]
[DoNotParallelize]
public class ServeHealthOptionsTests
{
	private static T Parse<T>(params string[] args) where T : class
	{
		using var error = new StringWriter();
		var options = Program.ParseInvocation(args, error).Result?.Value as T;
		Assert.IsNotNull(options, error.ToString());
		return options;
	}

	[TestMethod]
	public void Serve_defaults_leave_everything_to_the_environment()
	{
		var o = Parse<ServeOptions>("serve");
		Assert.IsNull(o.Interval);
		Assert.IsNull(o.Listen);
		Assert.IsFalse(o.ReconcileOnStart);
	}

	[TestMethod]
	public void Serve_parses_all_options()
	{
		var o = Parse<ServeOptions>("serve", "--interval", "30m", "--reconcile-on-start", "--listen", "http://0.0.0.0:9", "--metrics-file", "/m/x.prom", "--accounts-import", "/s/a.json");
		Assert.AreEqual("30m", o.Interval);
		Assert.IsTrue(o.ReconcileOnStart);
		Assert.AreEqual("http://0.0.0.0:9", o.Listen);
		Assert.AreEqual("/m/x.prom", o.MetricsFile);
		Assert.AreEqual("/s/a.json", o.AccountsImport);
		Assert.IsNull(o.ReadonlyConfigDir);
	}

	[TestMethod]
	public void Serve_parses_once()
		=> Assert.IsTrue(Parse<ServeOptions>("serve", "--once").Once);

	[TestMethod]
	public void Serve_parses_readonly_config()
		=> Assert.AreEqual("/ro", Parse<ServeOptions>("serve", "--readonly-config", "/ro").ReadonlyConfigDir);

	[TestMethod]
	public void Health_defaults_to_loopback_8080()
		=> Assert.AreEqual("http://127.0.0.1:8080/health", Parse<HealthOptions>("health").Url);

	[TestMethod]
	public async Task Health_exits_1_when_nothing_is_listening()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();

		var rc = await new HealthOptions { Url = $"http://127.0.0.1:{port}/health" }.ExecuteAsync();
		Assert.AreEqual(1, rc);
	}

	[TestMethod]
	[DataRow("200 OK", 0)]
	[DataRow("503 Service Unavailable", 1)]
	public async Task Health_exit_code_follows_the_response_status(string status, int expected)
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		var server = Task.Run(async () =>
		{
			using var client = await listener.AcceptTcpClientAsync();
			var stream = client.GetStream();
			await stream.ReadAsync(new byte[4096]);
			await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}"));
		});

		var rc = await new HealthOptions { Url = $"http://127.0.0.1:{port}/health" }.ExecuteAsync();
		await server;
		listener.Stop();

		Assert.AreEqual(expected, rc);
	}
}
