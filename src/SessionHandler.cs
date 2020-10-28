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
			public SessionData(Uri endpoint, Worker worker)
			{
				Endpoint = endpoint;
				Host = new UpstreamHost(endpoint.AbsoluteUri);
				Worker = worker;
				// TODO customizable timespan
				Timeout = TimeSpan.FromSeconds(30);
				Touch();
			}

			public Uri Endpoint { get; }
			public UpstreamHost Host { get; }
			public Worker Worker { get; }
			public DateTimeOffset ExpiresAfter { get; private set; }
			public TimeSpan Timeout {get; private set; }

			public void Touch()
			{
				// TODO sync access System.DateTimeOffset.UtcNow
				ExpiresAfter = DateTimeOffset.UtcNow + Timeout;
			}
		}

		private readonly ILogger<SessionHandler> _logger;
		private readonly ILoadBalancer _balancer;
		private readonly IHttpClientFactory _factory;

		private readonly IDictionary<string, SessionData> _sessions = new ConcurrentDictionary<string, SessionData>();
		
		public SessionHandler(ILogger<SessionHandler> logger, ILoadBalancer balancer, IHttpClientFactory factory)
		{
			_logger = logger;
			_balancer = balancer;
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

		private async Task RemoteDeleteSession(string sessionId, Uri sessionEndpoint, CancellationToken cancel)
		{
			using var client = _factory.CreateClient("delete");
			var deleteSession = new Uri(sessionEndpoint, $"/session/{sessionId}");

			_logger.LogInformation("Sending DELETE request! {0}", deleteSession);

			try
			{
				var response = await client.DeleteAsync(deleteSession, cancel);
				_logger.LogDebug("Completed DELETE request with {0} code.", response.StatusCode);
			}
			catch (Exception e)
			{
				_logger.LogError("sending DELETE to a '{0}' failed: {1}", deleteSession, e.Message);
			}
		}

		public async Task<HttpResponseMessage> HandleProxyRequest(HttpContext context)
		{
			_logger.LogDebug($"New proxy '{context.Request.Method}' request '{context.Request.Path}'");

			if (context.Request.Path == "" && context.Request.Method == "POST")
			{
				_logger.LogDebug($"Initiating new session");
				return await ProcessSessionRequest(context);
			}

			// Otherwise just forward request
			var sessionId = ParseSessionId(context.Request.Path);
			if (!_sessions.TryGetValue(sessionId, out var sessionData))
			{
				_logger.LogDebug($"Failed to find session '{sessionId}', unknown request");
				// throw new Exception("invalid session id");
				return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
			}

			_logger.LogDebug($"Forwarding request to {sessionData.Endpoint}");
			var response = await context
				.ForwardTo(sessionData.Endpoint)
				.AddXForwardedHeaders()
				.Send();
			sessionData.Touch();

			// TODO the session might have been deleted by that time, do nothing?
			if (context.RequestAborted.IsCancellationRequested)
			{
				_logger.LogError($"Session cancelled! {sessionId}");
				_sessions.Remove(sessionId);
				sessionData.Worker.Dispose();
				// TODO consider _registry.ReleaseHost(sessionData.Host)

				var cts = new CancellationTokenSource(4000);
				await RemoteDeleteSession(sessionId, sessionData.Endpoint, cts.Token);
			}
			if (context.Request.Method == "DELETE")
			{
				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("DELETE '{0}' query for {1} session failed", sessionData.Endpoint, sessionId);
				}
					
				_sessions.Remove(sessionId);
				sessionData.Worker.Dispose();
				_logger.LogInformation($"Closed session: {sessionId} on host '{sessionData.Endpoint}'");
				// TODO consider _registry.ReleaseHost(sessionData.Host) instead of Dispose
			}
			return response;
		}

		public async Task CleanupExpiredSessions()
		{
			var cts = new CancellationTokenSource(4000);

			var now = DateTimeOffset.UtcNow;
			var expired = (from pair in _sessions
				where now > pair.Value.ExpiresAfter
				select new { sessionId = pair.Key, endPoint = pair.Value.Endpoint, worker = pair.Value.Worker, expires = pair.Value.ExpiresAfter }
				).ToList();
			if (!expired.Any()) return;

			expired.ForEach(s => {
				_logger.LogWarning($"Removing expired session {s.sessionId}, expired {s.expires}");
				_sessions.Remove(s.sessionId);
				s.worker.Dispose();
			});

			await Task.WhenAll(from e in expired select RemoteDeleteSession(e.sessionId, e.endPoint, cts.Token));
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

				var worker = await _balancer.GetNext(new Request(caps));
				var sessionEndpoint = new Uri(new Uri(worker.Host.AbsoluteUri.TrimEnd('/') + "/"), "session");

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

					_logger.LogInformation($"New session: {sessionId} on host '{worker.Host}'");
					_sessions.Add(sessionId, new SessionData(sessionEndpoint, worker));
				}
				else if (--retriesLeft <= 0)
				{
					_logger.LogError("Failed to process /session request after 3 retries");
				}
				else
				{
					_logger.LogWarning("Failed to get response from {0}, retrying in 10s", worker.Host);
					worker.Dispose();
					// TODO consider _registry.ReleaseHost(worker);
					// TODO check worker availability
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