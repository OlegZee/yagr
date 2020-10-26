using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProxyKit;
using Serilog;

namespace QaKit.Yagr
{
	public class BalancerLifecycleService : IHostedService
	{
		private readonly ILoadBalancer _balancer;
		private readonly ILogger<BalancerLifecycleService> _logger;
		private readonly RouterConfig _config;
		private readonly CancellationTokenSource _balancerCts;

		public BalancerLifecycleService(IOptions<RouterConfig> configOptions, ILoadBalancer balancer, ILogger<BalancerLifecycleService> logger)
		{
			_balancer = balancer;
			_logger = logger;

			_config = configOptions.Value;
			_logger.LogInformation("{0} routers configured", _config.Hosts.Count);

			_balancerCts = new CancellationTokenSource();
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			_logger.LogInformation("Starting services");
			_balancer.Start(_config.Hosts.ToArray());
			return Task.CompletedTask;
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			_logger.LogInformation("Stopping services");
			await _balancer.Shutdown();
		}
	}

	public class Startup
	{
		public Startup(IConfiguration configuration)
		{
			Configuration = configuration; 
		}

		public IConfiguration Configuration { get; }

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			services.Configure<RouterConfig>(Configuration.GetSection("router"));

			services.AddProxy();
			services.AddSingleton<SessionHandler>();
			// services.AddSingleton<IHostsRegistry,HostsRegistry>();
			services.AddSingleton<ILoadBalancer, RoundRobinBalancer>();
			services.AddHttpClient();

			services.AddHostedService<BalancerLifecycleService>();

			services.AddControllers();
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}

			app.UseSerilogRequestLogging();

			app.Map("/session", sessionHandler => sessionHandler.RunProxy<SessionHandler>());

			app.UseRouting();
			app.UseEndpoints(endpoints =>
			{
				endpoints.MapControllers();
			});
		}
	}
}
