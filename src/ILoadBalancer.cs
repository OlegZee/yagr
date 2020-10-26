using System;
using System.Threading;
using System.Threading.Tasks;

namespace QaKit.Yagr
{
	public class Request
	{
		public Caps Caps {get; private set;}

		public Request(Caps caps)
		{
			Caps = caps;
		}
	}

	public class Worker: IDisposable
	{
		private readonly Action _setComplete;
		public Uri Host { get; private set; }
		public Caps Caps { get; private set; }

		private Worker(Action setComplete, Uri host, Caps caps) { _setComplete = setComplete; Host = host; Caps = caps; }
		public void Dispose() => _setComplete?.Invoke();

		public static Worker Create(Action setComplete, Uri host, Caps caps) => new Worker(setComplete, host, caps);
	}

	public interface ILoadBalancer
	{
		Task<Worker> GetNext(Request req);
		Task Start(HostConfig[] hostConfigs);
		Task Shutdown();

		HostConfig[] GetConfig();
	}
}