# CanSat Ground Station

A desktop ground station for receiving and monitoring CanSat telemetry. The application listens for telemetry over UDP multicast, presents live flight and link information in an Avalonia UI, records telemetry to a CSV log, and uses a 3D Kalman filter to forecast the trajectory from GPS measurements.

## Projects

| Project | Role |
| --- | --- |
| `CanSatGroundStation` | Core ground-station functionality: UDP multicast telemetry reception, packet parsing, CSV logging, application settings, and 3D Kalman state estimation and trajectory forecasting. |
| `CanSatGSUI` | Avalonia desktop application. Its dashboard displays GPS/navigation, atmospheric, acceleration, and radio telemetry, packet history, connection status, and trajectory forecasts. Its settings view configures the network stream, log destination, and forecast parameters. |
| `CanSatGroundStation.Tests` | xUnit test project covering core telemetry parsing and ground-station behavior, as well as UI view-model behavior. |

## How the pieces fit together

The UI uses the core project to receive and process telemetry. Parsed values feed the dashboard and packet history; the configured log destination is used for CSV recording. Valid GPS data initializes the Kalman estimator, which produces the displayed trajectory forecast. Network and filter settings are saved so they can be reused in later application sessions.

## Dashboard

The dashboard includes:

- GPS position, altitude, fix status, satellite count, GPS time, and mission time.
- Barometric altitude, temperature, pressure, and humidity.
- Acceleration components and total magnitude.
- Radio RSSI and signal-to-noise ratio.
- A Kalman 3D trajectory forecast, parsed telemetry, packet history, and connection/packet status.

The settings tab provides the local Ethernet IP, multicast group, UDP port, telemetry log file, and Kalman filter/forecast parameters. Use **Save & Apply Settings** to persist and apply changes, or **Reset to Defaults** to restore defaults.

## Build, run, and test

Install the .NET 10 SDK, then run these commands from the repository root:

```sh
dotnet build CanSatGroundStation.sln
dotnet run --project CanSatGSUI
dotnet test CanSatGroundStation.sln
```

The application needs to be configured for the local network and multicast stream that provide the CanSat telemetry. See the Settings tab to configure the local interface, multicast group, port, and log file.
