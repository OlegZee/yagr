using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProxyKit;

namespace proxy
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

		private readonly IDictionary<string, SessionData> _sessions = new ConcurrentDictionary<string, SessionData>();
		
		public SessionHandler(ILogger<SessionHandler> logger, IHostsRegistry registry)
		{
			_logger = logger;
			_registry = registry;
		}

		private async Task<Caps> ReadCaps(HttpContext context)
		{
			var body = await ReadRequestBody(context);
			_logger.LogDebug("Request: {0}", body);
			
			var capsJson = JsonDocument.Parse(body).RootElement;

			var (caps, w3c) = (JsonDocument.Parse("{}").RootElement, false);

			if (capsJson.TryGetProperty("desiredCapabilities", out var desiredCapabilities))
			{
				(caps, w3c) = (desiredCapabilities, false);
			}
			else if(capsJson.TryGetProperty("capabilities", out var w3cCapabilities)
				&& w3cCapabilities.TryGetProperty("alwaysMatch", out var alwaysMatch))
			{
				(caps, w3c) = (alwaysMatch, true);
			}

			string capabilityJsonWireW3C(string jswKey, string w3cKey)
			{
				var k = w3c ? w3cKey : jswKey;
				if (!caps.TryGetProperty(k, out var propElement)) return "";

				switch (propElement.ValueKind)
				{
					case JsonValueKind.String:
						return propElement.GetString();
					case JsonValueKind.Object:
						return string.Join(" ",
							from prop in propElement.EnumerateObject()
							select $"{prop.Name}={prop.Value.GetString()}");
					default:
						_logger.LogError("Failed to interpret {0} capability", k);
						return "";
				}
			}

			string capability(string capKey) => capabilityJsonWireW3C(capKey, capKey);

			return new Caps()
			{
				browser = capability("browserName") switch
				{
					"" => capability("deviceName"),
					string s  => s
				},
				version = capabilityJsonWireW3C("version", "browserVersion"),
				platform = capabilityJsonWireW3C("platform", "platformName"),
				labels = capability("labels")
			};
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
			}
			if (context.Request.Method == "DELETE")
			{
				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("DELETE '{0}' query for {1} session failed", sessionData.Endpoint, sessionId);
					// TODO is the host stale?
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
				var caps = await ReadCaps(context);
				_logger.LogInformation($"Requested caps: {caps.browser}-{caps.version}-{caps.platform}-{caps.labels}");

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