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

		public IEnumerable<Uri> RunningHosts => _runningHosts.Keys;
		public RoundRobinBalancer(ILogger<RoundRobinBalancer> logger, IHttpClientFactory clientFactory)
		{
			_logger = logger;
			_clientFactory = clientFactory;
		}

		public async Task<bool> AddHost(HostConfig config)
		{
			var hostUri = new Uri(config.HostUri);
			await DeleteHost(hostUri);
			_logger.LogInformation($"(Re)starting host '{hostUri}'");

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