using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace AreWeThereYet;

public class AreWeThereYetSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    #region AutoPilot

    [Menu("AutoPilot Enabled")]
    public ToggleNode autoPilotEnabled { get; set; } = new ToggleNode(false);

    [Menu("Remove Grace Period")]
    public ToggleNode autoPilotGrace { get; set; } = new ToggleNode(true);

    [Menu("Leader Name")]
    public TextNode autoPilotLeader { get; set; } = new TextNode("");

    [Menu("Dash Enabled")]
    public ToggleNode autoPilotDashEnabled { get; set; } = new ToggleNode(false);

    [Menu("Close Follow")]
    public ToggleNode autoPilotCloseFollow { get; set; } = new ToggleNode(true);

    [Menu("Dash Key")]
    public HotkeyNode autoPilotDashKey { get; set; } = new HotkeyNode(Keys.W);

    [Menu("Move Key")]
    public HotkeyNode autoPilotMoveKey { get; set; } = new HotkeyNode(Keys.Q);

    [Menu("Toggle Key")]
    public HotkeyNode autoPilotToggleKey { get; set; } = new HotkeyNode(Keys.NumPad9);

    [Menu("Random Click Offset")]
    public RangeNode<int> autoPilotRandomClickOffset { get; set; } = new RangeNode<int>(10, 1, 100);

    [Menu("Input Frequency")]
    public RangeNode<int> autoPilotInputFrequency { get; set; } = new RangeNode<int>(50, 1, 100);

    [Menu("Keep within Distance")]
    public RangeNode<int> autoPilotPathfindingNodeDistance { get; set; } = new RangeNode<int>(200, 10, 1000);

    [Menu("Transition Distance")]
    public RangeNode<int> autoPilotClearPathDistance { get; set; } = new RangeNode<int>(500, 100, 5000);

    #endregion

    #region Visual Settings

    [Menu("Task Line Width")]
    public RangeNode<int> TaskLineWidth { get; set; } = new RangeNode<int>(3, 0, 10);

    [Menu("Task Line Color")]
    public ColorNode TaskColor { get; set; } = new ColorNode(Color.Green);

    #endregion

    #region Debug Settings

    [Menu("Enable Rendering")]
    public ToggleNode EnableRendering { get; set; } = new ToggleNode(true);

    [Menu("Show Terrain Debug")]
    public ToggleNode ShowTerrainDebug { get; set; } = new ToggleNode(false);

    [Menu("Show Detailed Debug")]
    public ToggleNode ShowDetailedDebug { get; set; } = new ToggleNode(false);

    [Menu("Target Layer Value")]
    public RangeNode<int> TargetLayerValue { get; set; } = new RangeNode<int>(2, 0, 5);

    #endregion
}
