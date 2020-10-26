using System.Linq;

namespace QaKit.Yagr
{
	public static class CapsExt
	{
		public static Caps? FulfilCaps(HostConfig hostConfig, Caps caps)
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
	}
}