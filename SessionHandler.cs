using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProxyKit;

namespace proxy
{
	public class HostConfig
	{
		public string HostUri { get; set; }
		public int Limit { get; set; }
		public uint Weight { get; set; }
	}
	
	public class RouterConfig
	{
		public List<HostConfig> Hosts { get; set; }
	}
	
	public class SessionHandler : IProxyHandler
	{
		private readonly ILogger<SessionHandler> _logger;

		private readonly RoundRobin _hosts;
		private readonly IDictionary<string, UpstreamHost> _sessions = new ConcurrentDictionary<string, UpstreamHost>();
		
		private readonly RouterConfig _config = new RouterConfig();

		public SessionHandler(ILogger<SessionHandler> logger, IConfiguration config)
		{
			_logger = logger;

			config.GetSection("router").Bind(_config);
			_logger.LogInformation("{0} routers configured", _config.Hosts.Count);
			
			_hosts = new RoundRobin((
				from h in _config.Hosts
				select new UpstreamHost(h.HostUri, Math.Max(1, h.Weight))
				).ToArray()
			);
		}

		private static readonly SemaphoreSlim _sessionLimitLock = new SemaphoreSlim(2);
		
		private async Task<UpstreamHost> GetAvailableHost()
		{
			_logger.LogInformation("Acquiring session, current count: {0}", _sessionLimitLock.CurrentCount);
			// TODO take the host with least number of sessions
			// TODO ensure we do not exceed the limits/per host
			await _sessionLimitLock.WaitAsync();
			
			_logger.LogInformation("Acquired session, current count: {0}", _sessionLimitLock.CurrentCount);
			return _hosts.Next();
		}

		public async Task<HttpResponseMessage> HandleProxyRequest(HttpContext context)
		{
			if (context.Request.Path == "")
			{
				var host = await GetAvailableHost();
				var initResponse = await context
					.ForwardTo(host)
					.AddXForwardedHeaders()
					.Send();
				var responseBody = await initResponse.Content.ReadAsStringAsync();
				var seleniumResponse = JsonDocument.Parse(responseBody);
				var sessionId = seleniumResponse.RootElement.GetProperty("value").GetProperty("sessionId").GetString();
					
				_logger.LogInformation("New session: {0}", sessionId);
				_sessions.Add(sessionId, host);
				return initResponse;
			}
			else
			{
				var sessionId = ParseSessionId(context.Request.Path);
				if (!_sessions.TryGetValue(sessionId, out var host))
				{
					throw new Exception("invalid session id");
				}

				var response = await context
					.ForwardTo(host)
					.AddXForwardedHeaders()
					.Send();
				if (context.Request.Method == "DELETE")
				{
					_sessions.Remove(sessionId);
					_sessionLimitLock.Release();
					_logger.LogInformation("Deleted session: {0}, lock count: {1}", sessionId, _sessionLimitLock.CurrentCount);
				}
				return response;
			}
		}

		private static string ParseSessionId(in string requestPath)
		{
			var segments = requestPath.Split('/');
			return segments[1];
		}
	}
}