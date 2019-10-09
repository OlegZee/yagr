using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProxyKit;

namespace proxy
{
	public interface IHostsRegistry
	{
		Task<UpstreamHost> GetAvailableHost(Caps caps);
		void ReleaseHost(UpstreamHost host);
	}

	public struct Caps
	{
		public string browser, platform, version, labels;
	}

	public class VersionInfo
	{
		public string Number { get; set; }
		public string Platform { get; set; }
	}

	public class BrowsersInfo
	{
		public string Name { get; set; }
		public string DefaultVersion { get; set; }
		public VersionInfo[] Versions { get; set; }
	}

	public class HostConfig
	{
		public string HostUri { get; set; }
		public int Limit { get; set; }
		public uint Weight { get; set; }
		
		public BrowsersInfo[] Browsers { get; set; }
	}
	
	public class RouterConfig
	{
		public List<HostConfig> Hosts { get; set; }
		public int SessionsLimit { get; set; }
	}


	public class HostsRegistry : IHostsRegistry
	{
		private readonly ILogger _logger;
		private RouterConfig _config = new RouterConfig();
		
		private readonly RoundRobin _hosts;
		private readonly SemaphoreSlim _sessionLimitLock;
		private readonly IReadOnlyDictionary<Uri, SemaphoreSlim> _hostLimits;

		public HostsRegistry(IConfiguration config, ILogger<HostsRegistry> logger)
		{
			_logger = logger;

			config.GetSection("router").Bind(_config);
			_logger.LogInformation("{0} routers configured", _config.Hosts.Count);

			_sessionLimitLock = new SemaphoreSlim(_config.SessionsLimit == 0u ? 2^10: _config.SessionsLimit);

			var hosts = (
				from h in _config.Hosts
				let weight = Math.Max(1, h.Weight)
				let host = new UpstreamHost(h.HostUri, weight)
				let limit = Math.Max(1, h.Limit)
				select new { host, weight, limit } ).ToList();
			
			_hosts = new RoundRobin(hosts.Select(h => h.host).ToArray());

			_hostLimits = new ReadOnlyDictionary<Uri, SemaphoreSlim>(
				hosts.ToDictionary(arg => arg.host.Uri, arg => new SemaphoreSlim(arg.limit))
				);
		}

		private bool SatisfiesCaps(Caps caps, Uri host)
		{
			var hostConfig = _config.Hosts.Find(config => config.HostUri == host.ToString());
			if (hostConfig == null)
			{
				_logger.LogError("Failed to locate host info {0}", host);
				return true;
			}

			if (hostConfig.Browsers == null)
			{
				return true;
			}

			foreach (var browser in hostConfig.Browsers.Where(info => info.Name == caps.browser))
			{
				if (browser.Versions == null || browser.Versions.Any(
					version => (caps.version == "" || version.Number.StartsWith(caps.version))
						&& (caps.platform == "" || version.Platform.StartsWith(caps.platform))))
				{
					return true;
				}
			}

			return false;
		}
		
		public async Task<UpstreamHost> GetAvailableHost(Caps caps)
		{
			_logger.LogInformation("Acquiring session, current count: {0}", _sessionLimitLock.CurrentCount);
			await _sessionLimitLock.WaitAsync();
			
			var triedHostCount = 0;
			Uri lastHostTried = null;
			
			do
			{
				var host = _hosts.Next();

				if (SatisfiesCaps(caps, host.Uri))
				{
					var semaphore = _hostLimits[host.Uri];
					if (semaphore.CurrentCount > 0)
					{
						await semaphore.WaitAsync();
						_logger.LogInformation("Acquired `{uri}`, global limit: {1}, host limit is {2}",
							host.Uri, _sessionLimitLock.CurrentCount, semaphore.CurrentCount);
						return host;
					}
				}

				if (host.Uri != lastHostTried)
				{
					triedHostCount++;
					lastHostTried = host.Uri;
				}
				if (triedHostCount % _hosts.Count() == 0)
				{
					_logger.LogWarning("Failed to acquire host after {0} attempts. Sleeping for 10s", triedHostCount);
					await Task.Delay(10000);
				}
			} while (true);
		}

		public void ReleaseHost(UpstreamHost host)
		{
			var semaphore = _hostLimits[host.Uri];
			semaphore.Release();
			
			_sessionLimitLock.Release();
			_logger.LogInformation("Released host {0}, host limit: {1}, global limit: {2}",
				host.Uri, semaphore.CurrentCount, _sessionLimitLock.CurrentCount);
		}
	}
}