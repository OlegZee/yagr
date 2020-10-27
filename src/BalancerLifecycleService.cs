using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QaKit.Yagr
{
	public class BalancerLifecycleService : IHostedService
	{
		private readonly IOptionsMonitor<RouterConfig> _configOptions;
		private readonly ILoadBalancer _balancer;
		private readonly IHttpClientFactory _clientFactory;
		private readonly ILogger<BalancerLifecycleService> _logger;

		public BalancerLifecycleService(IOptionsMonitor<RouterConfig> configOptions,
			ILoadBalancer balancer,
			IHttpClientFactory clientFactory,
			ILogger<BalancerLifecycleService> logger)
		{
			_configOptions = configOptions;
			_balancer = balancer;
			_clientFactory = clientFactory;
			_logger = logger;

			configOptions.OnChange(_ => {
				_logger.LogInformation($"Config has changed");
				ReloadConfiguration();
			});
		}

		private void ReloadConfiguration()
		{
			var updateCts = new CancellationTokenSource();
			SyncConfig(updateCts.Token).ConfigureAwait(false);
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
	
		public async Task StartAsync(CancellationToken cancellationToken)
		{
			_logger.LogInformation("Starting services");
			await SyncConfig(cancellationToken);
		}

		private async Task SyncConfig(CancellationToken cancellationToken)
		{
			var runningHosts = _balancer.GetConfig().ToDictionary(c => new Uri(c.HostUri), c => c);
			var newConfig = _configOptions.CurrentValue;
			var newHosts = newConfig.Hosts.ToDictionary(c => new Uri(c.HostUri), c => c);

			var hostsStatus = await CheckAliveStatus(from h in newConfig.Hosts select h.HostUri);
			Predicate<Uri> isAlive = uri => hostsStatus.TryGetValue(uri, out var x) && x;

			var toBeRemoved = 
				(from host in runningHosts.Keys
				where !newHosts.ContainsKey(host) || !isAlive(host)
				select host).ToArray();
			var toBeStarted =
				(from host in newHosts.Keys
				where (!runningHosts.ContainsKey(host) || !areConfigsEqual(newHosts[host], runningHosts[host])) && isAlive(host)
				select host).ToArray();

			// remove if none of changes
			_logger.LogInformation("{0} routers in config, {1} are alive", newConfig.Hosts.Count, hostsStatus.Count(p => p.Value == true));

			await Task.WhenAll(
				from host in toBeRemoved
				select _balancer.DeleteHost(host)
				);
			await Task.WhenAll(
				from host in toBeStarted
				select _balancer.AddHost(newHosts[host])
				);

			// TODO update status monitor queue
			// if (hostsStatus.Any(status => status.Value == false))
			// {
			// 	var deadHosts = from h in newConfig.Hosts where !isAlive(h.HostUri) select h.HostUri;
			// 	_logger.LogWarning("Hosts not available: {0}", string.Join(", ", deadHosts.ToArray()));
			// }
		}

		private static bool areConfigsEqual(HostConfig config1, HostConfig config2)
		{
			var json1 = JsonSerializer.Serialize(config1);
			var json2 = JsonSerializer.Serialize(config2);
			return json1 == json2;
		}

		private async Task<IDictionary<Uri, bool>> CheckAliveStatus(IEnumerable<string> hosts)
		{
			var checkAliveCli = _clientFactory.CreateClient("checkalive");
			var result = await Task.WhenAll(
				from h in hosts let uri = new Uri(h)
				select IsHostAlive(uri, checkAliveCli).ContinueWith(task => new { uri = uri, alive = task.Result})
			);
			var hostsStatus = result.ToDictionary(v => v.uri, v => v.alive);
			return hostsStatus;
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			_logger.LogInformation("Stopping services");
			var configs = _balancer.RunningHosts;
			await Task.WhenAll(
				configs.Select(host => _balancer.DeleteHost(host))
				);
		}
	}
}
