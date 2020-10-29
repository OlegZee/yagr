using System;
using System.Collections.Generic;

namespace QaKit.Yagr
{
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
		
		public List<BrowsersInfo> Browsers { get; } = new List<BrowsersInfo>();
	}

	public class RouterConfig
	{
		public List<HostConfig> Hosts { get; } = new List<HostConfig>();

		public TimeSpan Timeout { get; set; } = new TimeSpan(0, 0, 60);
		public TimeSpan MaxTimeout { get; set; } = new TimeSpan(1, 0, 0);

		public int SessionRetryCount { get; set; } = 3;
		public TimeSpan SessionRetryTimeout { get; set; } = new TimeSpan(0, 0, 30);

	}

}