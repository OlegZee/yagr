using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace QaKit.Yagr.Controllers
{
	public class StatusResponse
	{
		public class SessionsInfo
		{
			public SessionsInfo() : this(0, new List<object>())
			{}

			public SessionsInfo(int count, List<object> sessions)
			{
				this.Count = count;
				this.Sessions = sessions;
			}
			
			[JsonPropertyName("count")]
			public int Count { get; set; }
			
			[JsonPropertyName("sessions")]
			public List<object> Sessions { get; set; }

			public SessionsInfo Merge(SessionsInfo other)
			{
				if (other == null) return this;
				
				return new SessionsInfo(Count + other.Count, Sessions.Concat(other.Sessions).ToList());
			}
		}

		public class BrowsersSessionInfo : Dictionary<string, Dictionary<string, Dictionary<string, SessionsInfo>>>
		{
			public void Merge(BrowsersSessionInfo other)
			{
				foreach (var (browser, otherVersions) in other)
				{
					if (!TryGetValue(browser, out var versions))
					{
						Add(browser, versions = new Dictionary<string, Dictionary<string, SessionsInfo>>());
					}
				
					foreach (var (version, otherPlatforms) in otherVersions)
					{
						if (!versions.TryGetValue(version, out var platforms))
						{
							versions.Add(version, platforms = new Dictionary<string, SessionsInfo>());
						}

						foreach (var (platform, otherSessions) in otherPlatforms)
						{
							if (platforms.TryGetValue(platform, out var sessions))
							{
								platforms[platform] = sessions.Merge(otherSessions);
							}
							else
							{
								platforms.Add(platform, otherSessions);
							}
						}
					}
				}
			}
		}
		
		public int total { get; set; }
		public int used { get; set; }
		public int queued { get; set; }
		public int pending { get; set; }
		public BrowsersSessionInfo browsers { get; set; } = new BrowsersSessionInfo();

		public void Merge(StatusResponse hs)
		{
			browsers.Merge(hs.browsers);

			total += hs.total;
			used += hs.used;
			queued += hs.queued;
			pending += hs.pending;
		}
	}
}
