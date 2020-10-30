using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProxyKit;
using Serilog;

namespace QaKit.Yagr
{
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
