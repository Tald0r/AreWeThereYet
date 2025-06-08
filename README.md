# README.md

# AreWeThereYet

A standalone AutoPilot plugin for Path of Exile that follows party leaders and automates basic movement tasks.

## Features

- Follow party leader automatically
- Portal detection and usage
- Waypoint collection - obsolate will be removed
- Quest item pickup
- Dash support for movement skills
- Grace period handling
- Configurable hotkeys and settings

## Installation

1. Place the plugin files in your ExileAPI plugins directory
2. Build the project or use the compiled DLL
3. Configure the leader name in the plugin settings
4. Set appropriate hotkeys for movement and dash abilities

## Configuration

- **Leader Name**: Set the name of the party leader to follow
- **Movement Key**: Key used for basic movement
- **Dash Key**: Key used for dash/movement skills
- **Toggle Key**: Hotkey to enable/disable the autopilot
- **Follow Distance**: Distance to maintain from leader
- **Transition Distance**: Detection range for portals and transitions

## Dependencies

- ExileCore API
- ImGui.NET
- SharpDX
- .NET Framework 4.8

## Safety Features

- Grace period detection
- UI interaction prevention
- Configurable input delays
