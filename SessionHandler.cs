using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProxyKit;

namespace proxy
{

	public class SessionHandler : IProxyHandler
	{
		private readonly ILogger<SessionHandler> _logger;
		private readonly IHostsRegistry _registry;

		private readonly IDictionary<string, UpstreamHost> _sessions = new ConcurrentDictionary<string, UpstreamHost>();
		
		public SessionHandler(ILogger<SessionHandler> logger, IHostsRegistry registry)
		{
			_logger = logger;
			_registry = registry;
		}

		public async Task<HttpResponseMessage> HandleProxyRequest(HttpContext context)
		{
			if (context.Request.Path == "")
			{
				var host = await _registry.GetAvailableHost();
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
					_registry.ReleaseSession(sessionId);
				
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