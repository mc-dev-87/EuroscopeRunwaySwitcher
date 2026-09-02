# EuroscopeRunwaySwitcher

EuroscopeRunwaySwitcher is a lightweight Windows utility designed for vATC on the VATSIM network. It allows controllers to quickly apply and verify runway configurations in EuroScope using individual airport presets or predefined West and East configurations. The application reads the current EuroScope runway state and highlights active presets with a green background.

<img width="1020" height="460" alt="image" src="https://github.com/user-attachments/assets/6c8b26a0-d789-46ea-ad3f-20b71b35662d" />

## Features

- Applies configured departure and arrival runways for individual airports.
- Switches all configured airports between **West** and **East** runway presets.
- Supports separate departure and arrival runways, such as `29/33` for EPWA.
- Reads and verifies the actual runway state in EuroScope after every change.
- Highlights active runway presets with a green button background.
- Uses an external JSON file for airport grouping, order and runway configuration.
- Temporarily locks mouse and keyboard input while applying changes to prevent accidental interference with automated EuroScope clicks.
- Restores the original mouse position after the operation.

## Configuration File

> [!IMPORTANT]
> The application requires a **runways.json** file in the same directory as **EuroScopeRunwayPresets.exe**. If the file is missing or invalid, the application will display an error and stop.

The configuration file defines:

- The airport columns and display order.
- The ICAO code of each airport.
- Departure and arrival runways for the West preset.
- Departure and arrival runways for the East preset.

Example:

```json
{
  "columns": [
    {
      "airports": [
        {
          "code": "EPWA",
          "west": { "dep": "29", "arr": "33" },
          "east": { "dep": "15", "arr": "11" }
        },
        {
          "code": "EPWR",
          "west": { "dep": "29", "arr": "29" },
          "east": { "dep": "11", "arr": "11" }
        }
      ]
    }
  ]
}
```

When departure and arrival use the same runway, the button displays one runway number. When they differ, the button displays both values in `DEP/ARR` format.

## How to Use

1. Download the latest release and extract all files into one directory.
2. Keep **EuroScopeRunwayPresets.exe** and **runways.json** together.
3. Start EuroScope and open the **Active airport/runway selector** window.
4. Run **EuroScopeRunwayPresets.exe** and approve the Windows administrator prompt.
5. Click an airport preset, **All RWY West** or **All RWY East**.
6. Wait until the operation is complete. Mouse and keyboard input will be temporarily locked while runway settings are being changed.
7. Use **Refresh** to read the current runway state again.

> [!WARNING]
> Do not close EuroScope or its Active airport/runway selector while a preset is being applied. Input control returns automatically when the operation finishes or an error occurs. Windows also releases the input lock when `Ctrl+Alt+Delete` is pressed.

> [!IMPORTANT]
> The application requests administrator privileges because Windows requires elevated rights to temporarily lock mouse and keyboard input. This application is signed with a self-signed certificate to verify its authenticity. When you first run the app, Windows may display a SmartScreen warning. You can safely bypass this warning by clicking "More info" and then "Run anyway".

## Building from Source

1. Download or clone the repository on Windows.
2. Run **BUILD.cmd**.
3. The compiled executable and a copy of **runways.json** will be created in the **build** directory.

The build script uses the C# compiler included with Microsoft .NET Framework 4 and the Windows UI Automation assemblies.
