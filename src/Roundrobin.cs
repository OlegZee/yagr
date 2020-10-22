using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QaKit.Yagr
{

	public class Consumer
	{
		public void ProcessRequest()
		{
		}
	}

	public class Request
	{
		private readonly Caps _caps;

		public Request(Caps caps)
		{
			_caps = caps;
		}
	}

	public class Worker: IDisposable
	{
		private readonly Action _setComplete;
		public Caps Caps { get; private set; }

		public Worker(Action setComplete, Caps caps)
		{
			_setComplete = setComplete;
			Caps = caps;
		}

		public void Dispose() => _setComplete();
	}

	public class RoundRobinBalancer
	{
		private class Ticket
		{
			private readonly TaskCompletionSource<Worker> _workerTcs = new TaskCompletionSource<Worker>();

			public Ticket(Request req)
			{}

			public Task<Worker> AwaitWorker() => _workerTcs.Task;

			public Task Process(Uri machine, Caps caps)
			{
				var processingComplete = new TaskCompletionSource<bool>();
				_workerTcs.SetResult(new Worker(() => processingComplete.SetResult(true), caps));
				return processingComplete.Task;
			}
		}

		private Channel<Ticket> _queue = Channel.CreateUnbounded<Ticket>();

		public Task Run(Uri[] machines, CancellationToken cancel)
		{
			return RunBalancer(_queue.Reader, machines, cancel);
		}

		public async Task<Worker> GetNext(Request req)
		{
			var ticket = new Ticket(req);
			await _queue.Writer.WriteAsync(ticket);

			var worker = await ticket.AwaitWorker();
			return worker;
		}

		private static async Task RunBalancer(ChannelReader<Ticket> inputQueue, Uri[] machines, CancellationToken cancel)
		{
			var input = inputQueue.ReadAllAsync(cancel);

			async Task Worker(Uri machine, Caps caps)
			{
				await foreach (var req in input.WithCancellation(cancel))
				{
					await req.Process(machine, caps);
				}
				// queue is empty, quit the worker
			}

			var processingPool =
				from machine in machines
				from p in Enumerable.Range(0, 4)
				select Worker(machine, Caps.FromBVPL("", "", "", ""));

			await Task.WhenAll(processingPool);
		}

	}
}