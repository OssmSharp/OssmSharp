# Repository architecture

The web and command-line control paths, from top to bottom:

```mermaid
flowchart TB
    subgraph Web["Web panels"]
        Guest["Guest web<br/>OssmSharp.Web.GuestWeb"]
        Admin["Admin web<br/>OssmSharp.Web.AdminWeb"]
    end

    subgraph ApiLayer["API"]
        Api["OssmSharp.Web.Api"]
        Library["OssmSharp.Runtime.Oem"]
        Api --> Library
    end

    subgraph CliLayer["CLI"]
        Cli["OssmSharp.Cli"]
        CliLibrary["OssmSharp.Runtime.Oem"]
        Cli --> CliLibrary
    end

    Device["OSSM device"]

    Guest -->|SignalR| Api
    Admin -->|SignalR| Api
    Admin ~~~ Cli
    Library -->|Bluetooth| Device
    CliLibrary -->|Bluetooth| Device

    classDef web fill:#26ceaa,stroke:#078e70,color:#000000
    classDef application fill:#9ae8c1,stroke:#078e70,color:#000000
    classDef runtime fill:#7bade0,stroke:#078e70,color:#000000
    classDef device fill:#3d1a78,stroke:#078e70,color:#ffffff

    class Guest,Admin web
    class Api,Cli application
    class Library,CliLibrary runtime
    class Device device

    style Web fill:#ffffff,stroke:#078e70,color:#000000
    style ApiLayer fill:#ffffff,stroke:#078e70,stroke-width:2px,color:#000000
    style CliLayer fill:#ffffff,stroke:#078e70,stroke-width:2px,color:#000000
    linkStyle default stroke:#078e70,color:#000000
```

Both panels use [Web.Client](../src/OssmSharp.Web.Client/) to communicate with
the [API](../src/OssmSharp.Web.Api/). The API uses the
[device library](../src/OssmSharp.Runtime.Oem/) to send patterns and settings.
The [CLI](../src/OssmSharp.Cli/) uses the same library directly, without the API.
The two runtime boxes show its use in each application, not separate libraries.
The device firmware controls motor motion and decides whether motion is safe.
