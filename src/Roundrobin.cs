using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QaKit.Yagr
{
	public class Request
	{}

	public class Result
	{}

	public class RoundRobinBalancer
	{
		public static async Task StartBalancer(Uri[] machines, CancellationToken cancel)
		{
			var queue = Channel.CreateUnbounded<Request>();
			var results = Channel.CreateUnbounded<Result>();

			var inputQueue = queue.Reader.ReadAllAsync(cancel);

			async Task<Result> ProcessTask(Request request, CancellationToken cancel)
			{
				throw new NotImplementedException();
			}

			async Task Worker(Uri machine)
			{
				await foreach (var t in inputQueue.WithCancellation(cancel))
				{
					var result = await ProcessTask(t, cancel);
					await results.Writer.WriteAsync(result, cancel);
				}
			}

			foreach (var machine in machines)
			{
				foreach (var p in Enumerable.Range(0, 4))
				{
					Task.Factory.StartNew(async () => await Worker(machine), cancel);
				}
			}
			
			

			return;
		}

	}
}