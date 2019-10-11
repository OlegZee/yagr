using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace QaKit.Yagr.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class StatusController : Controller
	{
		private readonly ILogger<StatusController> _logger;

		public class StatusResponse
		{
			public class SessionsInfo
			{
				public int count { get; set; }
				public List<object> sessions { get; set; }

				public SessionsInfo Merge(SessionsInfo other)
				{
					if (other == null) return this;
					var result = new SessionsInfo { count = count + other.count, sessions = new List<object>()};
					result.sessions.AddRange(sessions);
					result.sessions.AddRange(other.sessions);
					return result;
				}
			}

			public class BrowsersSessionInfo : Dictionary<string, Dictionary<string, Dictionary<string, SessionsInfo>>>
			{
				public void Merge(BrowsersSessionInfo other)
				{
					foreach (var (browser, otherVersions) in other)
					{
						if (!TryGetValue(browser, out var versions))
						{
							Add(browser, versions = new Dictionary<string, Dictionary<string, SessionsInfo>>());
						}
					
						foreach (var (version, otherPlatforms) in otherVersions)
						{
							if (!versions.TryGetValue(version, out var platforms))
							{
								versions.Add(version, platforms = new Dictionary<string, SessionsInfo>());
							}

							foreach (var (platform, otherSessions) in otherPlatforms)
							{
								if (platforms.TryGetValue(platform, out var sessions))
								{
									platforms[platform] = sessions.Merge(otherSessions);
								}
								else
								{
									platforms.Add(platform, otherSessions);
								}
							}
						}
					}
				}
			}
			
			public int total { get; set; }
			public int used { get; set; }
			public int queued { get; set; }
			public int pending { get; set; }
			public BrowsersSessionInfo browsers { get; set; } = new BrowsersSessionInfo();

			public void Merge(StatusResponse hs)
			{
				browsers.Merge(hs.browsers);

				total += hs.total;
				used += hs.used;
				queued += hs.queued;
				pending += hs.pending;
			}
		}

		[HttpGet]
		public async Task<StatusResponse> Get(
			[FromServices] IHostsRegistry registry,
			[FromServices] IHttpClientFactory factory,
			[FromServices] ILogger<StatusController> logger)
		{
			var hostsConfig = registry.GetConfig();
			var stats = new StatusResponse();
			
			logger.LogInformation("STATUS request with {0} hosts configured", hostsConfig.Hosts.Count);
			var client = factory.CreateClient();
			foreach (var host in hostsConfig.Hosts)
			{
				var statusUri = new Uri(new Uri(host.HostUri), "/status");
				// TODO ignore if not available
				var hostStatusResponse = await client.GetAsync(statusUri, HttpCompletionOption.ResponseContentRead);
				if (hostStatusResponse.IsSuccessStatusCode)
				{
					var responseContent = await hostStatusResponse.Content.ReadAsStringAsync();
					var hs = JsonSerializer.Deserialize<StatusResponse>(responseContent);
					stats.Merge(hs);
				}
			}

			return stats;
		}
	}
}