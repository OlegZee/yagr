using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProxyKit;

namespace QaKit.Yagr
{
	public class SessionHandler : IProxyHandler
	{
		private class SessionData
		{
			public SessionData(Uri endpoint, UpstreamHost host)
			{
				Endpoint = endpoint;
				Host = host;
			}

			public Uri Endpoint { get; }
			public UpstreamHost Host { get; }
		}
		
		private readonly ILogger<SessionHandler> _logger;
		private readonly IHostsRegistry _registry;
		private readonly IHttpClientFactory _factory;

		private readonly IDictionary<string, SessionData> _sessions = new ConcurrentDictionary<string, SessionData>();
		
		public SessionHandler(ILogger<SessionHandler> logger, IHostsRegistry registry, IHttpClientFactory factory)
		{
			_logger = logger;
			_registry = registry;
			_factory = factory;
		}

		private static async Task<string> ReadRequestBody(HttpContext context)
		{
			context.Request.EnableBuffering();
			using var reader = new StreamReader(
				context.Request.Body,
				encoding: Encoding.UTF8,
				detectEncodingFromByteOrderMarks: false,
				bufferSize: 10000,
				leaveOpen: true);
			var body = await reader.ReadToEndAsync();
			// Reset the request body stream position so the next middleware can read it
			context.Request.Body.Position = 0;
			return body;
		}

		public async Task<HttpResponseMessage> HandleProxyRequest(HttpContext context)
		{
			if (context.Request.Path == "")
			{
				return await ProcessSessionRequest(context);
			}

			// Otherwise just forward request
			var sessionId = ParseSessionId(context.Request.Path);
			if (!_sessions.TryGetValue(sessionId, out var sessionData))
			{
				throw new Exception("invalid session id");
			}

			var response = await context
				.ForwardTo(sessionData.Endpoint)
				.AddXForwardedHeaders()
				.Send();

			if (context.RequestAborted.IsCancellationRequested)
			{
				_logger.LogError("Session cancelled! {0}", sessionId);
				_sessions.Remove(sessionId);
				_registry.ReleaseHost(sessionData.Host);

				_logger.LogInformation("Sending DELETE request! {0}", sessionId);
				
				using var client = _factory.CreateClient("delete");
				var deleteSession = new Uri(sessionData.Endpoint, $"/session/{sessionId}");

				var cts = new CancellationTokenSource(4000);
				try
				{
					await client.DeleteAsync(deleteSession, cts.Token);
				}
				catch (Exception e)
				{
					_logger.LogError("sending DELETE to a '{0}' failed: {1}", deleteSession, e.Message);
				}
			}
			if (context.Request.Method == "DELETE")
			{
				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("DELETE '{0}' query for {1} session failed", sessionData.Endpoint, sessionId);
				}
					
				_sessions.Remove(sessionId);
				_registry.ReleaseHost(sessionData.Host);
			}
			return response;
		}

		private async Task<HttpResponseMessage> ProcessSessionRequest(HttpContext context)
		{
			var retriesLeft = 3;
			do
			{
				var body = await ReadRequestBody(context);
				_logger.LogDebug("Request: {0}", body);
			
				if (string.IsNullOrWhiteSpace(body))
				{
					_logger.LogDebug("Session request with empty body");
				}

				var caps = Caps.Parse(body, _logger);
				_logger.LogInformation($"Requested caps: {JsonSerializer.Serialize(caps)}");

				var host = await _registry.GetAvailableHost(caps);
				var sessionEndpoint = new Uri(new Uri(host.Uri.AbsoluteUri.TrimEnd('/') + "/"), "session");

				var initResponse = await context
					.ForwardTo(sessionEndpoint)
					.AddXForwardedHeaders()
					.Send();
				if (initResponse.IsSuccessStatusCode)
				{
					var responseBody = await initResponse.Content.ReadAsStringAsync();
					var seleniumResponse = JsonDocument.Parse(responseBody);
					var sessionId = seleniumResponse.RootElement.GetProperty("value").GetProperty("sessionId")
						.GetString();

					_logger.LogInformation("New session: {0} on host `{1}`", sessionId, host.Uri);
					_sessions.Add(sessionId, new SessionData(sessionEndpoint, host));
				}
				else if (--retriesLeft <= 0)
				{
					_logger.LogError("Failed to process /session request after 3 retries");
				}
				else
				{
					_logger.LogWarning("Failed to get response from {0}, retrying in 10s", host.Uri);
					_registry.ReleaseHost(host);
					await Task.Delay(10000);
					continue;
				}

				return initResponse;
			} while (true);
		}

		private static string ParseSessionId(in string requestPath)
		{
			var segments = requestPath.Split('/');
			return segments[1];
		}
	}
}