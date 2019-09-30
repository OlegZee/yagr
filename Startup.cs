using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProxyKit;
using Serilog;

namespace proxy
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
			services.AddProxy();
			// services.AddProxy(options =>
			// {
			// 	options.PrepareRequest = (originalRequest, message) =>
			// 	{
			// 		message.Headers.Add("X-Forwarded-Host", originalRequest.Host.Host);
			// 		return Task.FromResult(0);
			// 	};
			// });

			// services.AddControllers();
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}

			app.UseSerilogRequestLogging();

			app
				.RunProxy(context => context
					.ForwardTo("http://localhost:12345/")
					.AddXForwardedHeaders()
					.Send());

		}
	}
}
