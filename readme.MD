# Yet Another Grid Router for Selenium

The product was inspired by a GGC and Yandex GR. Both turned to be not quite friendly for .NET-ters so here you go.

## Running router

The project is currently under heavy development.

* setup selenoid instances
* update src/appsettings.json with your solenoid hubs
* run the router using

```bash
cd src
dotnet run
```

## Configuration

_Yarg_ allows to configure setting via appsettings.json file, by command line and env variable.
E.g. Router/timeout setting in the file can be passes as `--router:timeout=0:0:10` as well.

Example of configuration file:
```json
{
  "Router": {
	"timeout": "0:0:45",
	"maxtimeout": "1:30",
	"sessionretrycount": 1,
	"sessionretrytimeout": "0:0:10",

	"Hosts": [
      { "HostUri": "http://andreye.dw.local:4444/wd/hub",
        "Limit": 4,
        "Browsers": [
          { "Name": "chrome",
            "DefaultVersion": "77",
            "Versions": [
              { "Number":  "77", "Platform": "" }
            ]
          }
        ]
      },
      { "HostUri": "http://localhost:4444/wd/hub/",
        "Limit": 3
      },
      { "HostUri": "http://localhost:4445/wd/hub/",
        "Limit": 0
      }
    ]
  }
}
```

Router settings:

* *timeout* (default value is 60s) - session timeout. If there's no activity for such period the session is terminated and resources are released.
* *maxtimeout* (1h) - maximal allowed timeout if defined in caps
* *limit* - maximal allowed number of sessions. Set to 0 to temporary remove the host from router.
* *SessionRetryCount* (3) - number of attempts to start a new session
* *SessionRetryTimeout* (0:0:30) - interval between session start retries

*Yarg* handles changes to hosts list and reloads the configufation dynamically.