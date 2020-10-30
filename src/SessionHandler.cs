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
using Microsoft.Extensions.Options;
using ProxyKit;

namespace QaKit.Yagr
{
	public class SessionHandler : IProxyHandler
	{
		private const int DeleteSessionTimeoutMs = 4000;

		private class SessionData
		{
			public SessionData(Uri upstream, Uri hostUri, Worker worker, TimeSpan sessionTimeout)
			{
				Upstream = upstream;
				HostUri = hostUri;
				UpstreamHost = new UpstreamHost(upstream.AbsoluteUri);
				Worker = worker;
				Timeout = sessionTimeout;
			}

			public Uri Upstream { get; }
			public Uri HostUri { get; }
			public UpstreamHost UpstreamHost { get; }
			public Worker Worker { get; }
			public TimeSpan Timeout {get; }

		}

		private readonly IOptions<RouterConfig> _routerConfig;
		private readonly ILogger<SessionHandler> _logger;
		private readonly ILoadBalancer _balancer;
		private readonly IHttpClientFactory _factory;
		private readonly IDictionary<string, SessionData> _sessions = new ConcurrentDictionary<string, SessionData>();
		private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionsExpires = new ConcurrentDictionary<string, DateTimeOffset>();
		private readonly ConcurrentDictionary<Uri, DateTimeOffset> _lastHostAccess = new ConcurrentDictionary<Uri, DateTimeOffset>();
		private readonly object _expiryCleanupLock = new object();
		
		public SessionHandler(ILogger<SessionHandler> logger, ILoadBalancer balancer, IHttpClientFactory factory, IOptions<RouterConfig> routerConfig)
		{
			_logger = logger;
			_balancer = balancer;
			_factory = factory;
			_routerConfig = routerConfig;
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
			var deleteSession = new Uri(sessionEndpoint, $"session/{sessionId}");

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
				return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
			}

			_logger.LogDebug($"Forwarding request to {sessionData.Upstream}");
			var response = await context
				.ForwardTo(sessionData.Upstream)
				.AddXForwardedHeaders()
				.Send();

			// TODO the session might have been deleted by that time, do nothing?
			if (context.RequestAborted.IsCancellationRequested)
			{
				ReleaseSession(sessionId);
				_logger.LogError($"Session cancelled! {sessionId}");

				using var cts = new CancellationTokenSource(DeleteSessionTimeoutMs);
				await RemoteDeleteSession(sessionId, sessionData.Upstream, cts.Token);
			}
			else if (context.Request.Method == "DELETE")
			{
				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("DELETE '{0}' query for {1} session failed", sessionData.Upstream, sessionId);
				}
					
				ReleaseSession(sessionId);
				_logger.LogInformation($"Closed session: {sessionId} on host '{sessionData.Upstream}'");
			}
			else
			{
				Touch(sessionId, sessionData.HostUri);
			}
			return response;
		}

		private void ReleaseSession(string sessionId)
		{
			if(_sessions.Remove(sessionId, out var sessionData))
			{
				sessionData.Worker.Dispose();
				_sessionsExpires.Remove(sessionId, out var _);
				// TODO consider _registry.ReleaseHost(sessionData.Host)
			}
		}

		private void Touch(string sessionId, Uri host)
		{
			if (_sessions.TryGetValue(sessionId, out var sessionData))
			{
				var now = DateTimeOffset.UtcNow;
				var expireTime = now + sessionData.Timeout;

				lock(_expiryCleanupLock) {
					_sessionsExpires.AddOrUpdate(sessionId, expireTime, (_, _1) => expireTime);
				}
				_lastHostAccess.AddOrUpdate(host, now, (_, _1) => now);
			}
		}

		public async Task CleanupExpiredSessions()
		{
			var expired = new List<Tuple<string,Uri,DateTimeOffset>>();
			lock(_expiryCleanupLock)
			{
				var now = DateTimeOffset.UtcNow;
				var expiredSessions = (from pair in _sessionsExpires
					where now > pair.Value
					select pair.Key
					).ToHashSet();
				if (!expiredSessions.Any()) return;

				expired = (from pair in _sessions
					let sessionId = pair.Key
					where expiredSessions.Contains(sessionId)
					select Tuple.Create(sessionId, pair.Value.Upstream, _sessionsExpires[sessionId])
					).ToList();
				expiredSessions.ToList().ForEach(sessionId =>
					_sessionsExpires.TryRemove(sessionId, out var _)
				);
				if (!expired.Any()) return;
			}

			expired.ForEach(i => {
				var (sessionId, _, expires) = i;
				ReleaseSession(sessionId);
				_logger.LogWarning($"Removing expired session {sessionId}, expired {expires}");
			});

			using var cts = new CancellationTokenSource(DeleteSessionTimeoutMs);
			await Task.WhenAll(from i in expired select RemoteDeleteSession(i.Item1, i.Item2, cts.Token));
		}
		public Task TerminateAllSessions(CancellationToken cancel)
		{
			var sessions = (from pair in _sessions
				let sessionId = pair.Key
				select new { sessionId, endpoint = pair.Value.Upstream }
				).ToList();
			if (!sessions.Any()) return Task.CompletedTask;

			sessions.ForEach(i => ReleaseSession(i.sessionId));
			return Task.WhenAll(from i in sessions select RemoteDeleteSession(i.sessionId, i.endpoint, cancel));
		}

		// Gets the list of hosts that router successfully communicated with after specified moment of time
		public IEnumerable<Uri> GetAliveHosts(DateTimeOffset when)
		{
			return (
				from pair in _lastHostAccess
				where pair.Value > when
				select pair.Key).ToList();
		}

		private async Task<HttpResponseMessage> ProcessSessionRequest(HttpContext context)
		{
			var retriesLeft = _routerConfig.Value.SessionRetryCount;
			var retryTimeout = _routerConfig.Value.SessionRetryTimeout;

			var body = await ReadRequestBody(context);
			_logger.LogDebug("Request: {0}", body);
		
			if (string.IsNullOrWhiteSpace(body))
			{
				_logger.LogDebug("Session request with empty body");
			}

			var caps = Caps.Parse(body, _logger);
			_logger.LogInformation($"New session request from {context.Connection.RemoteIpAddress}");
			_logger.LogDebug($"Requested caps: {JsonSerializer.Serialize(caps)}");
			var sessionTimeout = GetSessionTimeout(caps);

			do
			{
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
					_sessions.Add(sessionId, new SessionData(sessionEndpoint, worker.Host, worker, sessionTimeout));
					Touch(sessionId, worker.Host);
					return initResponse;
				}
				else if (--retriesLeft <= 0)
				{
					_logger.LogError("Failed to process /session request after 3 retries");
					return initResponse;
				}
				else
				{
					_logger.LogWarning("Failed to get response from {0}, retrying in 10s", worker.Host);
					worker.Dispose();
					// TODO consider _registry.ReleaseHost(worker);
					await Task.Delay(retryTimeout);
				}
			} while (true);
		}

		private TimeSpan GetSessionTimeout(Caps caps)
		{
			// TODO read caps
			var timeout = _routerConfig.Value.Timeout;
			return timeout;
		}

		private static string ParseSessionId(in string requestPath)
		{
			var segments = requestPath.Split('/');
			return segments[1];
		}
	}
}