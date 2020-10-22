using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace QaKit.Yagr
{
	public class Caps
	{
		public string Browser { get; private set; } = "";
		public string Platform { get; private set; } = "";
		public string Version { get; private set; } = "";
		public string Labels { get; private set; } = "";

		public static Caps Parse(string data, ILogger logger)
		{
			var capsJson = JsonDocument.Parse("{}").RootElement;
			if (!string.IsNullOrWhiteSpace(data))
			{
				try
				{
					capsJson = JsonDocument.Parse(data).RootElement;
				}
				catch (JsonException e)
				{
					logger.LogWarning("Failed to parse caps '{0}': {1}", data, e.Message);
				}
			}
			var (caps, w3c) = (JsonDocument.Parse("{}").RootElement, false);

			if (capsJson.TryGetProperty("desiredCapabilities", out var desiredCapabilities))
			{
				(caps, w3c) = (desiredCapabilities, false);
			}
			else if(capsJson.TryGetProperty("capabilities", out var w3cCapabilities)
				&& w3cCapabilities.TryGetProperty("alwaysMatch", out var alwaysMatch))
			{
				(caps, w3c) = (alwaysMatch, true);
			}

			string capabilityJsonWireW3C(string jswKey, string w3cKey)
			{
				var k = w3c ? w3cKey : jswKey;
				if (!caps.TryGetProperty(k, out var propElement)) return "";

				switch (propElement.ValueKind)
				{
					case JsonValueKind.String:
						return propElement.GetString();
					case JsonValueKind.Object:
						return string.Join(" ",
							from prop in propElement.EnumerateObject()
							select $"{prop.Name}={prop.Value.GetString()}");
					default:
						logger.LogError("Failed to interpret {0} capability", k);
						return "";
				}
			}

			string GetCapability(string capKey) => capabilityJsonWireW3C(capKey, capKey);

			return new Caps {
				Browser = GetCapability("browserName") switch
				{
					"" => GetCapability("deviceName"),
					string s  => s
				},
				Version = capabilityJsonWireW3C("version", "browserVersion"),
				Platform = capabilityJsonWireW3C("platform", "platformName"),
				Labels = GetCapability("labels")
			};
		}

	}
}