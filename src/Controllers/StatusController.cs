using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace QaKit.Yagr.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public partial class StatusController : Controller
	{

		[HttpGet]
		public async Task<StatusResponse> Get(
			[FromServices] ILoadBalancer registry,
			[FromServices] IHttpClientFactory factory,
			[FromServices] ILogger<StatusController> logger)
		{
			var configHosts = registry.GetConfig();
			
			logger.LogInformation("STATUS request with {0} hosts configured", configHosts.Length);

			using var client = factory.CreateClient("status");

			async Task<string?> GetStatus(Uri statusUri, CancellationToken token)
			{
				try
				{
					var response = await client.GetAsync(statusUri, HttpCompletionOption.ResponseContentRead, token);
					if (response.IsSuccessStatusCode)
					{
						var responseContent = await response.Content.ReadAsStringAsync();
						return responseContent;
					}

					logger.LogError("Error getting /STATUS from {0}: got {1}", statusUri, response.StatusCode);
					return null;
				}
				catch (Exception e)
				{
					logger.LogError("Error getting /STATUS from {0}: {1}", statusUri, e.Message);
					return null;
				}
			}
			
			
			var cts = new CancellationTokenSource(4000);
			var responses = await Task.WhenAll(
				from host in configHosts
				let statusUri = new Uri(new Uri(host.HostUri), "/status")
				select GetStatus(statusUri, cts.Token)
				);

			var stats = new StatusResponse();
			foreach (var statusPayload in responses.Where(r => !string.IsNullOrEmpty(r)))
			{
				var hs = JsonSerializer.Deserialize<StatusResponse>(statusPayload);
				stats.Merge(hs);
			}

			return stats;
		}
	}
}