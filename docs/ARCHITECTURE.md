# Repository architecture

The repository is split into two main groups - the **Runtime** and **Web**. Runtime facilitates
bluetooth communibation with the OSSM device and the web projects -as the name suggests - are for
the web-based controls. There is also a CLI to send commands directly to the device but this is
primarily useful for testing.

```mermaid
%% Keep attribute selectors unquoted: Mermaid rewrites apostrophes in directives.
%% Viewer theme classes take precedence over the operating system's color scheme.
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#9ae8c1", "primaryTextColor": "#000000", "primaryBorderColor": "#666666", "lineColor": "#666666", "textColor": "#000000", "edgeLabelBackground": "#f5f5f5"}, "themeCSS": ".root{--diagram-border:#666666;--diagram-foreground:#000000}@media(prefers-color-scheme:dark){.root{--diagram-border:#aaaaaa;--diagram-foreground:#ffffff}}.root:is(.vscode-light *,.vscode-high-contrast-light *){--diagram-border:#666666;--diagram-foreground:#000000}.root:is(.vscode-dark *,.vscode-high-contrast *){--diagram-border:#aaaaaa;--diagram-foreground:#ffffff}.node rect,.cluster rect{stroke:var(--diagram-border)!important}.cluster-label p{color:var(--diagram-foreground)!important}[data-id^=L_Guest_],[data-id^=L_Admin_]{--edge-color:#26ceaa}[data-id^=L_Api_],[data-id^=L_Cli_]{--edge-color:#9ae8c1}[data-id^=L_Library_],[data-id^=L_CliLibrary_]{--edge-color:#7bade0}[data-id] .labelBkg,[data-id] .edgeLabel,[data-id] .edgeLabel p{background-color:var(--edge-color)!important}[data-id] .edgeLabel p{color:#000000!important}"}}%%
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

    classDef web fill:#26ceaa,color:#000000
    classDef application fill:#9ae8c1,color:#000000
    classDef runtime fill:#7bade0,color:#000000
    classDef device fill:#3d1a78,color:#ffffff

    class Guest,Admin web
    class Api,Cli application
    class Library,CliLibrary runtime
    class Device device

    style Web fill:transparent,stroke-width:2px
    style ApiLayer fill:transparent,stroke-width:2px
    style CliLayer fill:transparent,stroke-width:2px
    linkStyle 0,1 stroke:#9ae8c1,color:#000000
    linkStyle 2,3 stroke:#26ceaa,color:#000000
    linkStyle 5,6 stroke:#7bade0,color:#000000
```

## Runtime.Oem internals

The class diagram shows the connection and settings-request path. Member lists and parameter lists
are abbreviated; `ct` means `CancellationToken`. `+` means public, `-` private, and `~` internal.
Underlined operations are static.

```mermaid
%% Class diagrams need theme CSS for per-source connector and label colors.
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#7bade0", "primaryTextColor": "#000000", "primaryBorderColor": "#666666", "lineColor": "#666666", "textColor": "#000000", "edgeLabelBackground": "#f5f5f5"}, "themeCSS": ".root{--diagram-border:#666666}@media(prefers-color-scheme:dark){.root{--diagram-border:#aaaaaa}}.root:is(.vscode-light *,.vscode-high-contrast-light *){--diagram-border:#666666}.root:is(.vscode-dark *,.vscode-high-contrast *){--diagram-border:#aaaaaa}.node path,.divider{stroke:var(--diagram-border)!important}[id*=-id_OssmDevice_],[id*=-id_IOssmDevice_OssmDevice_],[data-id^=id_OssmDevice_],.runtimeEdge{--edge-color:#7bade0;--edge-text:#000000}[id*=-id_SetOperationContext_],[data-id^=id_SetOperationContext_],.protocolEdge{--edge-color:#5049cb;--edge-text:#ffffff}[id*=-id_MotionSettings_],[id*=-id_DeviceState_],[data-id^=id_MotionSettings_],[data-id^=id_DeviceState_]{--edge-color:#9ae8c1;--edge-text:#000000}.relation{stroke:var(--edge-color)!important}[data-id] .labelBkg,[data-id] .edgeLabel,[data-id] .edgeLabel p,.runtimeEdge,.protocolEdge{background-color:var(--edge-color)!important}[data-id] .edgeLabel p,.runtimeEdge,.protocolEdge{color:var(--edge-text)!important}.edgeTerminals p{background-color:transparent!important}marker path{stroke:context-stroke!important}marker[id*=dependency] path{fill:context-stroke!important}"}}%%
classDiagram
    direction TB

    class IOssmDevice:::contract {
        <<interface>>
        +bool IsConnected
        +bool IsArmed
        +ConnectAsync(ct) Task
        +SetAsync(settings, ct) Task~SetResult~
        +GetStateAsync(bypassCache, ct) Task~DeviceState?~
        +EmergencyStopAsync(ct) Task
        +DisposeAsync() ValueTask
    }

    class OssmDevice:::runtime {
        -SemaphoreSlim io
        -SemaphoreSlim writes
        -uint lease
        -byte streamId
        -OssmDeviceOptions options
        -ApplySettingsAsync(current) Task~SetResult~
        -WriteCommandAsync(command, ct) Task
        -SendRequestAsync(...) Task~RadResponse~
        -MaintainLeaseAsync(...) Task
        -HandleMotionObservation(current, data) void
    }

    class SetOperationContext:::protocol {
        +MotionSettings Settings
        +CancellationTokenSource PendingCancellation
        +SetStatus Status
        +int CommandsWritten
        +SetResult Result
    }

    class MotionSettings:::model {
        +StrokePattern Pattern
        +int Speed
        +int Stroke
        +int Depth
        +int Sensation
        ~ToCommands() FirmwareCommand[]
    }

    class FirmwareCommand:::protocol {
        +FirmwareSetting Setting
        +int Value
    }

    class Request:::protocol {
        +Encode(...) byte[]$
        ~EncodeWithArguments(...) byte[]$
    }

    class DeviceState:::model {
        +StrokePattern Pattern
        +double? Speed
        +int Stroke
        +int Depth
        +int Sensation
        +FromBytes(data) DeviceState$
        +TryFromBytes(data, state, errors) bool$
    }

    IOssmDevice <|.. OssmDevice
    %% Multiplicity labels need spans because the renderer does not attach source IDs to them.
    OssmDevice "<span class=runtimeEdge>1</span>" --> "<span class=runtimeEdge>0..1</span>" SetOperationContext : latest request
    SetOperationContext "<span class=protocolEdge>0..*</span>" --> "<span class=protocolEdge>1</span>" MotionSettings : settings
    OssmDevice "<span class=runtimeEdge>1</span>" --> "<span class=runtimeEdge>0..1</span>" DeviceState : last known state
    MotionSettings ..> FirmwareCommand : creates
    OssmDevice ..> FirmwareCommand : writes
    OssmDevice ..> Request : serializes
    DeviceState ..> MotionSettings : validates controls

    classDef contract fill:#26ceaa,color:#000000
    classDef runtime fill:#7bade0,color:#000000
    classDef model fill:#9ae8c1,color:#000000
    classDef protocol fill:#5049cb,color:#ffffff

```

[OssmDevice](../src/OssmSharp.Runtime.Oem/OssmDevice.cs) owns the connection, control lease,
response correlation, and motion-stream subscriptions. Its `io` gate serializes operations other
than Stop; `writes` serializes issued Bluetooth transfers without holding the gate while waiting for
a terminal response.

[MotionSettings](../src/OssmSharp.Runtime.Oem/MotionSettings.cs) performs basic range checks and
converts requested speed to raw velocity by the following equation:

`rawVelocity = floor(speedPercent * strokePercent / 100)`

For example, 53% speed at 10% stroke produces 5% raw velocity. This reduces the acceleration over
short distances.

[DeviceState](../src/OssmSharp.Runtime.Oem/DeviceState.cs) parses snapshots and calculates current
speed from velocity and stroke in the same snapshot. The firmware controls motion and decides
whether it is safe.

Supporting types not in the diagram:

| Area      | Types and role                                                                                                                                                                                                                                                                            |
| --------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Discovery | [DeviceDiscovery](../src/OssmSharp.Runtime.Oem/DeviceDiscovery.cs) and [IDeviceDiscovery](../src/OssmSharp.Runtime/IDeviceDiscovery.cs), using the Shiny Bluetooth library to scan for devices.                                                                                           |
| Protocol  | [BleLifecycleDelegate](../src/OssmSharp.Runtime.Oem/Protocol/BleLifecycleDelegate.cs) forwards peripheral lifecycle callbacks. [RadJsonContext](../src/OssmSharp.Runtime.Oem/Protocol/RadJsonContext.cs) supplies JSON metadata for protocol information, responses, leases, and streams. |
