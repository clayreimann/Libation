using System.Net;
using System.Text.Json;
using Serilog;

namespace LibationEngine;

/// <summary>Serves <c>GET /health</c> from <see cref="CycleState"/>; every other path is 404.</summary>
public sealed class HealthServer : IDisposable
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly HttpListener listener = new();
	private readonly CycleState state;

	public HealthServer(string listenUrl, CycleState state)
	{
		this.state = state;
		listener.Prefixes.Add(ToPrefix(listenUrl));
	}

	/// <summary>Turns a listen URL into an HttpListener prefix; host <c>0.0.0.0</c> or <c>*</c> becomes <c>+</c>.</summary>
	public static string ToPrefix(string listenUrl)
	{
		var text = listenUrl.Replace("://*", "://wildcard").Replace("://+", "://wildcard");
		if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
			throw new FormatException($"Invalid listen URL '{listenUrl}'. Use e.g. http://127.0.0.1:8080");

		var host = uri.Host is "wildcard" or "0.0.0.0" ? "+" : uri.Host.Contains(':') ? $"[{uri.Host}]" : uri.Host;
		return $"{uri.Scheme}://{host}:{uri.Port}/";
	}

	public void Start()
	{
		listener.Start();
		_ = Task.Run(AcceptLoop);
	}

	private async Task AcceptLoop()
	{
		while (listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await listener.GetContextAsync();
			}
			catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
			{
				return;
			}

			try
			{
				Respond(context);
			}
			catch (Exception ex)
			{
				Log.Logger.Warning(ex, "Health request failed");
			}
		}
	}

	private void Respond(HttpListenerContext context)
	{
		using var response = context.Response;
		if (context.Request.Url?.AbsolutePath == "/health" && context.Request.HttpMethod is "GET" or "HEAD")
		{
			var body = JsonSerializer.SerializeToUtf8Bytes(state.Snapshot(), Json);
			response.ContentType = "application/json";
			response.ContentLength64 = body.Length;
			if (context.Request.HttpMethod == "GET")
				response.OutputStream.Write(body);
		}
		else
			response.StatusCode = 404;
	}

	public void Dispose() => listener.Close();
}
