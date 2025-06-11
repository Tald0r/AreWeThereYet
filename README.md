# AreWeThereYet

AutoPilot plugin for Path of Exile that follows party leaders with intelligent terrain detection and pathfinding.

## Core Features

- **Leader Following**: Automatically follows specified party leader
- **Smart Pathfinding**: Advanced terrain detection with real-time door state monitoring
- **Portal Management**: Detects and uses portals/area transitions automatically
- **Dash Integration**: Intelligent dash usage through obstacles and closed doors
- **Quest Items**: Automatic pickup of quest items near the leader

## Terrain Debugging

- **Real-time Raycast**: Enable cursor position raycast to test line-of-sight
- **Terrain Visualization**: Color-coded terrain values showing walkable/blocked areas
- **Door Detection**: Live monitoring of door states (open/closed) for follower logic
- **Debug Settings**: Comprehensive terrain debugging tools under Debug → Raycast settings

## Quick Setup

1. Install in your ExileCore plugins directory
2. Set **Leader Name** in AutoPilot settings
3. Configure **Movement Key** and **Dash Key** 
4. Use **Toggle Key** to enable/disable following
5. Enable terrain debug visualization to fine-tune pathfinding

## Key Settings

- **AutoPilot → Leader Name**: Player name to follow
- **AutoPilot → Keep within Distance**: Follow distance (default: 200)
- **Debug → Raycast → Cast Ray To World Cursor Position**: Enable cursor raycast debugging
- **Debug → Terrain → Refresh Interval**: Real-time terrain update frequency (default: 500ms)
