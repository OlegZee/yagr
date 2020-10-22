using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProxyKit;

namespace QaKit.Yagr
{
	public interface IHostsRegistry
	{
		Task<UpstreamHost> GetAvailableHost(Caps caps);
		void ReleaseHost(UpstreamHost host);
		RouterConfig GetConfig();
	}

	public class HostsRegistry : IHostsRegistry
	{
		private class HostInfo
		{
			public HostInfo(Uri uri, int limit)
			{
				Semaphore = new SemaphoreSlim(limit);
			}
			public SemaphoreSlim Semaphore { get; private set; }
		}

		private readonly ILogger _logger;
		private readonly RouterConfig _config = new RouterConfig();
		
		private readonly RoundRobin _hosts;
		private readonly SemaphoreSlim _sessionLimitLock;
		private readonly IReadOnlyDictionary<Uri, HostInfo> _activeHosts;

		public HostsRegistry(IOptions<RouterConfig> configOptions, ILogger<HostsRegistry> logger, IHttpClientFactory clientFactory)
		{
			_logger = logger;

			_config = configOptions.Value;
			_logger.LogInformation("{0} routers configured", _config.Hosts.Count);

			_sessionLimitLock = new SemaphoreSlim(_config.SessionsLimit == 0u ? 2^10: _config.SessionsLimit);

			// TODO async wake up
			var checkAliveCli = clientFactory.CreateClient("checkalive");
			var tasks = from h in _config.Hosts let uri = new Uri(h.HostUri) select IsHostAlive(uri, checkAliveCli).ContinueWith(task => task.Result ? uri : null);
			var aliveHosts = (from uri in Task.WhenAll(tasks).Result where uri != null select uri).ToList();

			if (aliveHosts.Count() != _config.Hosts.Count)
			{
				var deadHosts = from h in _config.Hosts let uri = new Uri(h.HostUri) where !aliveHosts.Exists(ah => ah == uri) select h.HostUri;

				_logger.LogWarning("Hosts appears not available: {0}", 
					string.Join(", ", (from h in deadHosts select h.ToString()).ToArray()));
			}

			var hosts = (
				from h in _config.Hosts
				let uri = new Uri(h.HostUri)
				where aliveHosts.Exists(ah => ah == uri)
				let weight = Math.Max(0, h.Weight)
				let host = new UpstreamHost(uri.AbsoluteUri, weight)
				let limit = Math.Max(1, h.Limit)
				select new { host, weight, limit } ).ToList();

			_hosts = new RoundRobin((from h in hosts select h.host).ToArray());

			_activeHosts = new ReadOnlyDictionary<Uri, HostInfo>(
				hosts.ToDictionary(arg => arg.host.Uri, arg => new HostInfo(arg.host.Uri, arg.limit))
				);
		}

		private static async Task<bool> IsHostAlive(Uri host, HttpClient client)
		{
			var cts = new CancellationTokenSource(2000);
			try
			{
				var statusUri = new Uri(host, "/status");
				var response = await client.GetAsync(statusUri, cts.Token);
				return response.IsSuccessStatusCode;
			}
			catch
			{
				return false;
			}
		}

		sealed class FindHostResult
		{
			public FindHostResult(Uri host, Caps caps)
			{
				Host = host;
				Caps = caps;
			} 
			public Uri Host { get; }
			public Caps Caps { get; }
		}

		private static IEnumerable<FindHostResult> FindHosts(IEnumerable<HostConfig> configs, Caps caps, Uri? host)
		{
			foreach (var hostConfig in configs.Where(config => host == null || config.HostUri == host.ToString()))
			{
				var resultCaps = FulfilCaps(hostConfig, caps);
				if (resultCaps != null)
				{
					yield return new FindHostResult(new Uri(hostConfig.HostUri), resultCaps);
				}
			}
		}

		private static Caps FulfilCaps(HostConfig hostConfig, Caps caps)
		{
			// TODO this does not seem correct as grid might be requesting say chromedriver while host does only support Mozilla
			// I took this code from GGR and got no idea why it's done that way
			if (!hostConfig.Browsers.Any())
			{				
				return Caps.FromBVPL("", "", "", "");
			}

			var version = caps.Version;
			var platform = caps.Platform;
			
			foreach (var browser in hostConfig.Browsers.Where(info => info.Name == caps.Browser))
			{
				if (version == "") version = browser.DefaultVersion;
				if (platform == "" || platform == "ANY") platform = browser.DefaultPlatform;
				
				if (browser.Versions == null || browser.Versions.Any(
					v =>
						(version == "" || v.Number.StartsWith(version))
						&& (platform == "" || v.Platform.StartsWith(platform))))
				{
					return Caps.FromBVPL(browser.Name, version, platform, "");
				}
			}
			return null;
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

				var foundHosts = FindHosts(_config.Hosts, caps, host.Uri);
				if (foundHosts.Any())
				{
					var hostInfo = _activeHosts[host.Uri];
					if (hostInfo.Semaphore.CurrentCount > 0)
					{
						await hostInfo.Semaphore.WaitAsync();
						_logger.LogInformation("Acquired `{uri}`, global limit: {1}, host limit is {2}",
							host.Uri, _sessionLimitLock.CurrentCount, hostInfo.Semaphore.CurrentCount);
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
			var hostInfo = _activeHosts[host.Uri];
			hostInfo.Semaphore.Release();
			
			_sessionLimitLock.Release();
			_logger.LogInformation("Released host {0}, host limit: {1}, global limit: {2}",
				host.Uri, hostInfo.Semaphore.CurrentCount, _sessionLimitLock.CurrentCount);

			// TODO check host is not available anymore
		}

		public RouterConfig GetConfig()
		{
			return _config;
		}
	}
}