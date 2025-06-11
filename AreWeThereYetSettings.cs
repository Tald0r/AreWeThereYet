using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace AreWeThereYet;

public static class ColorExtensions
{
    public static Color ToSharpDx(this System.Drawing.Color color)
    {
        return new Color(color.R, color.G, color.B, color.A);
    }
}

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
    
    public RangeNode<int> RandomClickOffset { get; set; } = new(10, 1, 100);
    public RangeNode<int> InputFrequency { get; set; } = new(50, 1, 100);
    public RangeNode<int> KeepWithinDistance { get; set; } = new(200, 10, 1000);
    public RangeNode<int> TransitionDistance { get; set; } = new(500, 100, 5000);

    public VisualSettings Visual { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public class VisualSettings
    {
        public RangeNode<int> TaskLineWidth { get; set; } = new(3, 0, 10);
        public ColorNode TaskLineColor { get; set; } = new(System.Drawing.Color.Green.ToSharpDx());
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
        public RangeNode<int> TerrainValueForCollision { get; set; } = new(2, 0, 5);
    }

    [Submenu(CollapsedByDefault = false)]
    public class TerrainSettings
    {
        public ToggleNode ReplaceValuesWithDots { get; set; } = new(false);
        public RangeNode<float> DotSize { get; set; } = new(3.0f, 1.0f, 100.0f);
        public RangeNode<int> DotSegments { get; set; } = new(16, 3, 6);
        public RangeNode<int> RefreshInterval { get; set; } = new(500, 100, 2000);
    }
}
