# Broken Arrow Player Tracker

A Windows desktop app that detects players in Broken Arrow multiplayer lobbies and displays their public BATrace statistics.

## Features

- Automatically detects lobby entry, exit, and player changes
- Displays ELO, global rank, level, ranked games, K/D, and Turtle Index
- Shows Steam avatars and profile links
- Supports English, numeric, Korean, Chinese, Japanese, and Russian player names
- Updates individual rows without rebuilding the entire table
- Uses player IDs when available to distinguish players with identical names
- Stores local player notes and displays them when the player is encountered again
- Separates allied and enemy players after matchmaking
- Shows the three map-vote candidates with US and RU win rates
- Recovers missing match players from the GameRoom roster log
- Exports diagnostic information to a TXT file
- Detects the Steam installation automatically and supports manual path selection

## Usage

1. Download `BrokenArrowSquadTracker.exe` from [Releases](../../releases).
2. Run the executable and approve the Windows administrator prompt.
3. Start Broken Arrow and join a multiplayer lobby.

The tracker uses the built-in Windows `pktmon` utility for packet detection, so administrator privileges are required. No additional packet-capture driver is installed.

## Requirements

- Windows x64
- .NET 10 Windows Desktop Runtime x64
- Steam and Broken Arrow

If the required .NET runtime is missing, the Microsoft .NET AppHost dialog can guide you through the installation.

## Data and privacy

Player statistics are retrieved from public BATrace data. The tracker prefers a confirmed player ID when available and keeps detected players visible even when no statistics are found. Player notes and application settings are stored only on the local PC.
