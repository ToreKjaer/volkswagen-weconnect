﻿# Volkswagen WeConnect Class Library for C#

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

Built upon the lovely work by [robinostlund](https://github.com/robinostlund/volkswagencarnet/tree/master) (Home Assistant integration).

This repository contains a C# class library to interact with Volkswagen's WeConnect services. It provides an easy way to integrate and manage Volkswagen WeConnect functionalities within your applications.

## Disclaimer
Use at your own risk. This library is not affiliated with Volkswagen AG. It is based on reverse-engineering of the official WeConnect API (at least I assume). Volkswagen may change the API at any time, which may break this library.

## Features

- **Authentication**: Handles authentication seamlessly in the background.
- **Vehicles**: Retrieves a list of vehicles registered within ones WeConnect account.
- **Charge data**: Retrieves charge data (SOC, estimated range in km, plug state, charge state and charge power in kW) for a specific vehicle.

More features may be added in the future, such as climate control, lock/unlock, etc.

## Installation

Installation is done via NuGet:

```bash
Install-Package torekjaer/volkswagen-weconnect
```

## Usage

```csharp
using volkswagen_weconnect;

var auth = new VwAuth("username", "password");
using (var connection = new VwConnection(auth, loggerFactory))
{
    List<Vehicle> vehicles = connection.GetVehicles();
    Charge chargeData = vehicles[0].GetChargeData();
}
```

The same authentication token is used for all calls within the same `VwConnection` instance.

## Contributing
Contributions are welcome! Please submit a pull request or open an issue to discuss your ideas.