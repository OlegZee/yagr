using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace QaKit.Yagr
{
	public class Program
	{
		public static IConfiguration Configuration
		{
			get
			{
				var builder = new ConfigurationBuilder()
					.SetBasePath(Directory.GetCurrentDirectory())
					.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
					.AddJsonFile(
						$"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
						optional: true)
					.AddEnvironmentVariables();
				return builder.Build();
			}
		}
		
		public static int Main(string[] args)
		{
			Log.Logger = new LoggerConfiguration()
				.ReadFrom.Configuration(Configuration)
				.Enrich.FromLogContext()
				.Enrich.WithProperty("SourceContext", "")
				.WriteTo.Debug()
				.CreateLogger();

			try
			{
				Log.Information("Starting web host");
				var host = CreateHostBuilder(args).Build();
				Console.CancelKeyPress += (sender, args) => {
					args.Cancel = false;
					Log.Logger.Warning("Termination initiated");
					// TODO
					// var balancer = (ILoadBalancer)host.Services.GetService(typeof(ILoadBalancer));
					// balancer.Shutdown().Wait();
				};
				host.Run();
				return 0;
			}
			catch (Exception ex)
			{
				Log.Fatal(ex, "Host terminated unexpectedly");
				return 1;
			}
			finally
			{
				Log.CloseAndFlush();
			}
			
		}

		public static IHostBuilder CreateHostBuilder(string[] args) =>
			Host.CreateDefaultBuilder(args)
				.ConfigureWebHostDefaults(builder =>
					builder.UseConfiguration(Configuration)
					.UseStartup<Startup>()
				)
				.UseSerilog();
	}
}
