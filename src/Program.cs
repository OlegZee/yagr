using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using QaKit.Yagr.Controllers;
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
//				.WriteTo.Console(
//					restrictedToMinimumLevel: LogEventLevel.Information,
//					theme: AnsiConsoleTheme.Code
//					// {Properties:j} added:
////					outputTemplate: "\\\\{SourceContext:l}\\\\ [{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} " +
////					                "{Properties:j}{NewLine}{Exception}"
//				)
				.CreateLogger();

			try
			{
				Log.Information("Starting web host");
				CreateHostBuilder(args).Build().Run();
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
