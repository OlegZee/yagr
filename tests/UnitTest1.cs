using System;
using System.Text.Json;
using NUnit.Framework;
using QaKit.Yagr.Controllers;

namespace tests
{
	public class Tests
	{
		private readonly string[] Configs = new string[]
		{
			@"
				{""total"":3,""used"":1,""queued"":0,""pending"":0,""browsers"":{""chrome"":{"""":{""unknown"":{""count"":1,""sessions"":[{""id"":""675ec582ae88097f69f9d617787c427a"",""vnc"":false,""screen"":""1920x1080x24"",""caps"":{""browserName"":""chrome"",""screenResolution"":""1920x1080x24"",""videoScreenSize"":""1920x1080""}}]}},""latest"":{}},""firefox"":{""latest"":{}}}}
				",
			@"
				{""total"":4,""used"":1,""queued"":0,""pending"":0,""browsers"":{""chrome"":{"""":{""unknown"":{""count"":1,""sessions"":[{""id"":""83734884a21e90819c31318863d4ab33"",""vnc"":false,""screen"":""1920x1080x24"",""caps"":{""browserName"":""chrome"",""screenResolution"":""1920x1080x24"",""videoScreenSize"":""1920x1080""}}]}},""latest"":{}},""firefox"":{""latest"":{}}}}
				",
			@"
				{""total"":4,""used"":0,""queued"":0,""pending"":0,""browsers"":{""chrome"":{""latest"":{}},""firefox"":{""latest"":{}}}}
				"
		};
		
		[SetUp]
		public void Setup()
		{
		}

		[Test]
		public void MergeStatusResponses()
		{
			var configs = Array.ConvertAll(Configs,
				config => JsonSerializer.Deserialize<StatusController.StatusResponse>(config));
			
			Assert.IsTrue(configs.Length > 0);
		}
	}
}