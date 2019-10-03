using System;
using System.Collections.Generic;
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
		Task<UpstreamHost> GetAvailableHost();
		void ReleaseSession(string sessionId);
	}
	
	public class HostConfig
	{
		public string HostUri { get; set; }
		public int Limit { get; set; }
		public uint Weight { get; set; }
	}
	
	public class RouterConfig
	{
		public List<HostConfig> Hosts { get; set; }
		public int Limit { get; set; }
	}


	public class HostsRegistry : IHostsRegistry
	{
		private readonly ILogger _logger;
		private RouterConfig _config = new RouterConfig();
		
		private readonly RoundRobin _hosts;
		private readonly SemaphoreSlim _sessionLimitLock;

		public HostsRegistry(IConfiguration config, ILogger<HostsRegistry> logger)
		{
			_logger = logger;

			config.GetSection("router").Bind(_config);
			_logger.LogInformation("{0} routers configured", _config.Hosts.Count);

			_sessionLimitLock = new SemaphoreSlim(_config.Limit == 0u ? 2^10: _config.Limit);
			_hosts = new RoundRobin((
					from h in _config.Hosts
					select new UpstreamHost(h.HostUri, Math.Max(1, h.Weight))
				).ToArray()
			);
		}
		
		public async Task<UpstreamHost> GetAvailableHost()
		{
			_logger.LogInformation("Acquiring session, current count: {0}", _sessionLimitLock.CurrentCount);
			// TODO take the host with least number of sessions
			// TODO ensure we do not exceed the limits/per host
			await _sessionLimitLock.WaitAsync();
			
			_logger.LogInformation("Acquired session, current count: {0}", _sessionLimitLock.CurrentCount);
			return _hosts.Next();
		}

		public void ReleaseSession(string sessionId)
		{
			_sessionLimitLock.Release();
			_logger.LogInformation("Deleted session: {0}, global limit is now: {1}", sessionId, _sessionLimitLock.CurrentCount);
		}
	}
}