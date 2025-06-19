﻿using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Color = SharpDX.Color;

namespace AreWeThereYet;


public class AreWeThereYetSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public AutoPilotSettings AutoPilot { get; set; } = new();
    public DebugSettings Debug { get; set; } = new();
}

[Submenu(CollapsedByDefault = false)]
public class AutoPilotSettings
{
    public ToggleNode Enabled { get; set; } = new(false);
    public ToggleNode RemoveGracePeriod { get; set; } = new(true);
    public TextNode LeaderName { get; set; } = new("");
    public ToggleNode DashEnabled { get; set; } = new(false);
    public ToggleNode CloseFollow { get; set; } = new(true);

    public HotkeyNode DashKey { get; set; } = new(Keys.W);
    public HotkeyNode MoveKey { get; set; } = new(Keys.Q);
    public HotkeyNode ToggleKey { get; set; } = new(Keys.NumPad9);

    // public RangeNode<int> RandomClickOffset { get; set; } = new(10, 1, 100);  -- not implemented right now
    public RangeNode<int> InputFrequency { get; set; } = new(50, 1, 100);
    public RangeNode<int> KeepWithinDistance { get; set; } = new(200, 10, 1000);
    public RangeNode<int> TransitionDistance { get; set; } = new(500, 100, 5000);

    [Menu("Zone Update Buffer (ms)")]
    public RangeNode<int> ZoneUpdateBuffer { get; set; } = new(1000, 500, 5000);

    public VisualSettings Visual { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public class VisualSettings
    {
        public RangeNode<int> TaskLineWidth { get; set; } = new(3, 0, 10);
        public ColorNode TaskLineColor { get; set; } = new(SharpDX.Color.Magenta);
    }

    public DashSettings Dash { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public class DashSettings
    {
        public RangeNode<int> TerrainValueForCollision { get; set; } = new(3, 0, 5);
        
        [Menu("Minimum Dash Distance")]
        public RangeNode<int> DashMinDistance { get; set; } = new(10, 0, 1000);
        
        [Menu("Maximum Dash Distance")]
        public RangeNode<int> DashMaxDistance { get; set; } = new(200, 0, 1000);
        
        [Menu("Heuristic Distance (when no terrain data)")]
        public RangeNode<int> HeuristicThreshold { get; set; } = new(100, 50, 500);
    }
}

[Submenu(CollapsedByDefault = true)]
public class DebugSettings
{
    public ToggleNode EnableRendering { get; set; } = new(true);
    public ToggleNode ShowTerrainDebug { get; set; } = new(false);
    public ToggleNode ShowDetailedDebug { get; set; } = new(false);
    
    public RaycastSettings Raycast { get; set; } = new();
    public TerrainSettings Terrain { get; set; } = new();

    [Submenu(CollapsedByDefault = false)]
    public class RaycastSettings
    {
        public ToggleNode CastRayToWorldCursorPos { get; set; } = new(true);
        public ToggleNode DrawAtPlayerPlane { get; set; } = new(true);
    }

    [Submenu(CollapsedByDefault = false)]
    public class TerrainSettings
    {
        public RangeNode<int> GridSize { get; set; } = new(80, 1, 1000);
        public ToggleNode ReplaceValuesWithDots { get; set; } = new(false);
        public RangeNode<float> DotSize { get; set; } = new(3.0f, 1.0f, 100.0f);
        public RangeNode<int> DotSegments { get; set; } = new(16, 3, 6);
        public RangeNode<int> RefreshInterval { get; set; } = new(500, 100, 2000);

        public TerrainColor Colors { get; set; } = new();

        [Submenu(CollapsedByDefault = false)]
        public class TerrainColor
        {
            // Red - Impassable
            [Menu("Tile0 - Impassable")]
            public ColorNode Tile0 { get; set; } = new(Color.Red);
            // Light Green - Basic walkable
            [Menu("Tile1 - Basic walkable")]
            public ColorNode Tile1 { get; set; } = new(Color.LightGreen);

            // Yellow - Static objects (dashable)
            [Menu("Tile2 - Static objects (dashable)")]
            public ColorNode Tile2 { get; set; } = new(Color.Yellow);

            // Blue - Reserved
            [Menu("Tile3 - Reserved")]
            public ColorNode Tile3 { get; set; } = new(Color.Blue);

            // Purple - Reserved
            [Menu("Tile4 - Reserved")]
            public ColorNode Tile4 { get; set; } = new(Color.Purple);

            // Dark Green - Open walkable space
            [Menu("Tile5 - Open walkable space")]
            public ColorNode Tile5 { get; set; } = new(Color.Green);

            // Gray - Unknown - EntityColors.Shadow.Value ?
            [Menu("TileUnknown - Unknown")]
            public ColorNode TileUnknown { get; set; } = new(Color.Gray);
        }
    }
}
