# Logistics Terminal for Big Ambitions

**English** | [简体中文](README.zh-CN.md)

A phone app for [Big Ambitions](https://store.steampowered.com/app/1331550/Big_Ambitions/) that shows your whole supply chain on one screen: what's in each warehouse and factory, where goods come from and go, how much each factory needs per week, and how much each store sells. It can also adjust import amounts and store restock targets in one click.

The UI follows the game language: Chinese when the game is in Chinese, English otherwise.

## Screenshots

These come from the design mockup (fictional data, Chinese UI). The in-game window has the same layout.

| Weekly supply (per warehouse) | Factory dashboard |
|---|---|
| ![Weekly supply](docs/screenshots/overview-weekly-supply.png) | ![Factory](docs/screenshots/factory.png) |

| Warehouse: outgoing | Store restock |
|---|---|
| ![Warehouse outgoing](docs/screenshots/warehouse-outgoing.png) | ![Store](docs/screenshots/store.png) |

| Stock overview | Warehouse: incoming |
|---|---|
| ![Stock overview](docs/screenshots/overview-stock.png) | ![Warehouse incoming](docs/screenshots/warehouse-incoming.png) |

## Features

- **Overview**
  - Stock of every good across all warehouses and factories.
  - **Weekly supply** per warehouse: weekly need vs. weekly supply, with a ± surplus/shortage.
  - **Store restock targets** for all stores in one list.
- **Warehouses**
  - Stock and how long it lasts.
  - Incoming: import contracts, wholesale contracts and transfers in.
  - Outgoing: factories are served first, then your stores, with the weekly ± that remains.
- **Factories**
  - Each machine group: shifts per week, daily and weekly material use, daily output.
  - Where yesterday's output went: shipped to your own buildings, exported, and export revenue.
  - Which warehouse supplies each raw material.
- **Stores**
  - Average and best-day sales over the last 7 days.
  - Current restock target, a suggested target (best day × 2, so one missed delivery doesn't empty the shelf) and a verdict.
- **One-click actions.** Each one shows a confirmation list first:
  - **Order / Top up** one material.
  - **Match weekly imports to need.** Raises or lowers import amounts to the weekly need, rounded up to 10.
  - **Apply suggested restock targets** for one store or all stores.

Open it from the phone (the **Logistics Terminal** app) or press **F8**. **Esc** closes it.

## How it works

- **Factory raw materials** are always imported into a warehouse first. The logistics manager then moves them to the factory at 2:00 every day, so ordering is done per warehouse.
- **Weekly need** = raw materials for the factories that warehouse supplies (recipe amount × scheduled machine hours) + last 7 days of sales of the stores it supplies.
- **Factory output** isn't recorded by the game. The mod snapshots factory stock at every midnight, before the 2:00 deliveries, and works output out from the stock change and delivery records. The numbers appear after two midnights with the mod installed. Snapshots are saved under `%USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\LogisticsTerminal\`.
- **Exports and export revenue** come straight from the game's records.
- **Ordering** edits your *existing* purchasing contracts:
  - It sets the item's weekly amount and destination warehouse.
  - It activates the contract as a weekly order if it was paused or one-time.
  - It respects importers' weekly limits.
  - It doesn't create new importer partnerships, which in the game need a phone call and a free purchasing agent. If no contract can buy an item, the mod tells you.
  - Wholesale contracts and goods made by your own factories are never changed.

> Ordering and restock changes modify your save like the in-game menus do, and imports cost money on the next Monday delivery. Make a save before trying them the first time.

## Install

### Steam Workshop
Coming soon.

### Manual
1. Download `LogisticsTerminal.dll` from [Releases](../../releases), or build it yourself (below).
2. Put it in:
   ```
   %USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal\LogisticsTerminal\
   ```
3. Start the game. Local mods are always active.

## Build from source

Requirements: the [.NET SDK](https://dotnet.microsoft.com/download) (6 or newer) and an installed copy of Big Ambitions. The project compiles against the game's own assemblies, which aren't included in this repository.

```sh
dotnet build -c Release
# If the game isn't in the default Steam path:
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Big Ambitions"
```

The build copies the DLL into `ModsLocal\LogisticsTerminal` automatically.

### Layout

| File | What it does |
|---|---|
| `src/TerminalMod.cs` | Mod entry point (official Big Ambitions mod API), F8 hotkey |
| `src/PhoneButtonInjector.cs` | Adds the app button to the in-game phone |
| `src/TerminalWindow.cs` | The window (IMGUI, scaled to screen pixels) |
| `src/GameData.cs` | Reads warehouses, factories, stores, contracts and logistics plans from the save |
| `src/ImportOrdering.cs` | Order / top up / match imports, and store restock changes |
| `src/Ledger.cs` | Midnight stock snapshots for factory output |
| `src/Lang.cs` | Chinese / English text |

## Compatibility

Built and tested against Big Ambitions on Unity 2022.3 with the official mod loader. A game update that renames internal fields can break the mod. If something looks wrong, please open an issue and include the lines containing `LogisticsTerminal` from `Player.log`.

## License

[MIT](LICENSE). This is a fan-made mod, not affiliated with or endorsed by Hovgaard Games.
