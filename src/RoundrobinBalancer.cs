using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace QaKit.Yagr
{
	public class RoundRobinBalancer: ILoadBalancer
	{
		private static readonly TimeSpan WaitStoppingHostTimeout = TimeSpan.FromSeconds(30); // TODO to config

		private class Ticket
		{
			private readonly TaskCompletionSource<Worker> _workerTcs = new TaskCompletionSource<Worker>();

			public Ticket(Request req)
			{
				Caps = req.Caps;
			}

			public Caps Caps { get; private set; }

			public Task<Worker> AwaitWorker() => _workerTcs.Task;

			public Task Process(Uri host, Caps caps)
			{
				var processingComplete = new TaskCompletionSource<bool>();
				_workerTcs.SetResult(Worker.Create(() => processingComplete.SetResult(true), host, caps));
				return processingComplete.Task;
			}
		}

		private class HostInfo
		{
			public HostInfo(Uri hostUri, HostConfig config, CancellationTokenSource cts, Task[] runners)
			{
				HostUri = hostUri;
				Config = config;
				Cts = cts;
				Runners = runners;
			}

			public readonly Uri HostUri;
			public readonly HostConfig Config;
			public readonly CancellationTokenSource Cts;

			public readonly Task[] Runners;
		}

		private Channel<Ticket> _queue = Channel.CreateUnbounded<Ticket>();
		private readonly IList<Ticket> _denialQueue = new List<Ticket>();
		private readonly object _denialQueueLock = new object();
		private readonly ILogger<RoundRobinBalancer> _logger;
		private readonly IHttpClientFactory _clientFactory;

		private ConcurrentDictionary<Uri,HostInfo> _runningHosts = new ConcurrentDictionary<Uri,HostInfo>();
		private volatile bool _shutdownInProgress;

		public RoundRobinBalancer(ILogger<RoundRobinBalancer> logger, IHttpClientFactory clientFactory)
		{
			_logger = logger;
			_clientFactory = clientFactory;
		}

		public async Task<bool> RestartHost(HostConfig config)
		{
			if(_shutdownInProgress)
			{
				_logger.LogWarning($"Attempt to start '{config.HostUri}' while shutdown is in progress.");
				return false;
			}
			var hostUri = new Uri(config.HostUri);
			await DeleteHost(hostUri);

			// start or updates tasks for a specific host
			var cts = new CancellationTokenSource();
			var hostInfo = new HostInfo(hostUri, config, cts, StartHost(config, cts.Token));

			// TODO sync access
			_runningHosts.TryAdd(hostUri, hostInfo);
			return true;
		}
		public async Task<bool> DeleteHost(Uri host)
		{
			if(_runningHosts.TryRemove(host, out var hostInfo))
			{
				_logger.LogInformation($"Stopping host '{host}'");
				hostInfo.Cts.Cancel();

				var timeout = Task.Delay(WaitStoppingHostTimeout);
				var timeoutExpired = await Task.WhenAny(
					timeout,
					Task.WhenAll(hostInfo.Runners)) == timeout;

				if(timeoutExpired)
				{
					_logger.LogError($"Failed to stop '{host}' gracefully");
					// TODO force stopping agent
				}
				else
				{
					_logger.LogInformation($"Stopped host '{host}'");
				}
				return true;
			}
			return false;
		}

		public async Task Start(HostConfig[] hostConfigs)
		{
			_logger.LogInformation("Starting load balancer");
			_logger.LogInformation("{0} routers configured", hostConfigs.Length);

			var checkAliveCli = _clientFactory.CreateClient("checkalive");
			var tasks = from h in hostConfigs let uri = new Uri(h.HostUri) select IsHostAlive(uri, checkAliveCli).ContinueWith(task => task.Result ? uri : null);
			var aliveUris = (from uri in Task.WhenAll(tasks).Result where uri != null select uri).ToList();

			Predicate<string> isAlive = uri => aliveUris.Exists(ah => ah == new Uri(uri));

			if (aliveUris.Count() != hostConfigs.Length)
			{
				var deadHosts = from h in hostConfigs where !isAlive(h.HostUri) select h.HostUri;

				_logger.LogWarning("Hosts appears not available: {0}", 
					string.Join(", ", (from h in deadHosts select h.ToString()).ToArray()));
			}
			// TODO let lifecycle service to monitor hosts
			var aliveHosts = from h in hostConfigs where isAlive(h.HostUri) select h;
			await Task.WhenAll(aliveHosts.Select(host => RestartHost(host)));
		}

		/// Forcefully cancels the running sessions
		public async Task Shutdown()
		{
			_logger.LogInformation("Stopping load balancer");
			_shutdownInProgress = true;
			
			var runningHosts = _runningHosts.Keys;
			await Task.WhenAll(
				runningHosts.Select(host => DeleteHost(host))
				);

			// TODO graceful/forceful shutdown
			// _logger.LogError("Forceful shutdown is not implemented yet");
			_shutdownInProgress = false;
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

		public async Task<Worker> GetNext(Request req)
		{
			var ticket = new Ticket(req);
			await _queue.Writer.WriteAsync(ticket);

			var worker = await ticket.AwaitWorker();
			return worker;
		}

		public HostConfig[] GetConfig() => _runningHosts.Values.Select(h => h.Config).ToArray();

		private bool LookForDeniedTickets(Func<Ticket, Caps?> canProcess, [MaybeNullWhen(false)] out Ticket ticket, [MaybeNullWhen(false)] out Caps caps)
		{
			if(_denialQueue.Any()) {
				lock(_denialQueueLock)
				{
					for(var i = 0; i < _denialQueue.Count; i++)
					{
						var hostCaps = canProcess(_denialQueue[i]);
						if(hostCaps != null)
						{
							ticket = _denialQueue[i];
							caps = hostCaps;
							_denialQueue.RemoveAt(i);
							return true;
						}
					}
				}
			}
			ticket = null;
			caps = null;
			return false;
		}

		private Task[] StartHost(HostConfig config, CancellationToken cancel)
		{
			var inputQueue = _queue.Reader;
			async Task HostWorker()
			{
				var hostUri = new Uri(config.HostUri);
				Caps? canProcess(Ticket ticket)
				{
					return CapsExt.FulfilCaps(config, ticket.Caps);
				}
				while(!cancel.IsCancellationRequested)
				{
					if(LookForDeniedTickets(canProcess, out var deniedTicket, out var hostCaps))
					{
						await deniedTicket.Process(hostUri, hostCaps);
						continue;
					}
					var canRead = await inputQueue.WaitToReadAsync(cancel);
					if(canRead && inputQueue.TryRead(out var ticket))
					{
						var caps = canProcess(ticket);
						if (caps != null)
						{
							await ticket.Process(hostUri, caps);
						}
						else
						{
							lock(_denialQueueLock) _denialQueue.Add(ticket);
							await Task.Delay(1);	// penalty
						}
					}
				}
				// queue is empty, quit the worker
			}

			var processingPool = Array.ConvertAll(
				new int[Math.Max(1, config.Limit)],
				_ => HostWorker());
			return processingPool;
		}
	}
}