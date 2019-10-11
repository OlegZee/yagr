using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProxyKit;

namespace QaKit.Yagr
{
	public interface IHostsRegistry
	{
		Task<UpstreamHost> GetAvailableHost(Caps caps);
		void ReleaseHost(UpstreamHost host);
		RouterConfig GetConfig();
	}

	public struct Caps
	{
		public string browser, platform, version, labels;
	}

	public class VersionInfo
	{
		public string Number { get; set; } = "";
		public string Platform { get; set; } = "";
	}

	public class BrowsersInfo
	{
		public string Name { get; set; } = "";
		public string DefaultVersion { get; set; } = "";
		public string DefaultPlatform { get; set; } = "";
		public List<VersionInfo> Versions { get; } = new List<VersionInfo>();
	}

	public class HostConfig
	{
		public string HostUri { get; set; } = "";
		public int Limit { get; set; }
		public uint Weight { get; set; }
		
		public List<BrowsersInfo> Browsers { get; } = new List<BrowsersInfo>();
	}
	
	public class RouterConfig
	{
		public List<HostConfig> Hosts { get; } = new List<HostConfig>();
		public int SessionsLimit { get; set; }
	}


	public class HostsRegistry : IHostsRegistry
	{
		private readonly ILogger _logger;
		private readonly RouterConfig _config = new RouterConfig();
		
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

		sealed class FindHostResult
		{
			public FindHostResult(Uri host, string version, string platform)
			{
				Host = host;
				Version = version;
				Platform = platform;
			} 
			public Uri Host { get; }
			public string Version { get; }
			public string Platform { get; }
		}

		private IEnumerable<FindHostResult> FindHosts(Caps caps, Uri? host)
		{
			foreach (var hostConfig in _config.Hosts.Where(config => host == null || config.HostUri == host.ToString()))
			{
				if (!hostConfig.Browsers.Exists(_ => true))
				{
					yield return new FindHostResult(new Uri(hostConfig.HostUri), "", "");
				}

				var version = caps.version;
				var platform = caps.platform;
				
				foreach (var browser in hostConfig.Browsers.Where(info => info.Name == caps.browser))
				{
					if (version == "") version = browser.DefaultVersion;
					if (platform == "" || platform == "ANY") platform = browser.DefaultPlatform;
					
					if (browser.Versions == null || browser.Versions.Any(
						    v =>
							    (version == "" || v.Number.StartsWith(version))
							    && (platform == "" || v.Platform.StartsWith(platform))))
					{
						yield return new FindHostResult(new Uri(hostConfig.HostUri), version, platform);
					}
				}
			}
		}
		
		public async Task<UpstreamHost> GetAvailableHost(Caps caps)
		{
			_logger.LogInformation("Acquiring session, current count: {0}", _sessionLimitLock.CurrentCount);
			await _sessionLimitLock.WaitAsync();
			
			var triedHostCount = 0;
			Uri? lastHostTried = null;
			
			do
			{
				var host = _hosts.Next();

				var foundHosts = FindHosts(caps, host.Uri);
				if (foundHosts.Any())
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

		public RouterConfig GetConfig()
		{
			return _config;
		}
	}
}