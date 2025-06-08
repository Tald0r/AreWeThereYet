using System.Windows.Forms;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace AreWeThereYet;

public class AreWeThereYetSettings : ISettings
{
    public AreWeThereYetSettings()
    {
        Enable = new ToggleNode(false);
    }

    public ToggleNode Enable { get; set; }

    #region AutoPilot
        
    public ToggleNode autoPilotEnabled = new ToggleNode(false);
    public ToggleNode autoPilotGrace = new ToggleNode(true);
    public TextNode autoPilotLeader = new TextNode("");
    public ToggleNode autoPilotDashEnabled = new ToggleNode(false);
    public ToggleNode autoPilotCloseFollow = new ToggleNode(true);
    public HotkeyNode autoPilotDashKey = new HotkeyNode(Keys.W);
    public HotkeyNode autoPilotMoveKey = new HotkeyNode(Keys.Q);
    public HotkeyNode autoPilotToggleKey = new HotkeyNode(Keys.NumPad9);
    public ToggleNode autoPilotTakeWaypoints = new ToggleNode(true);
    public RangeNode<int> autoPilotRandomClickOffset = new RangeNode<int>(10, 1, 100);
    public RangeNode<int> autoPilotInputFrequency = new RangeNode<int>(50, 1, 100);
    public RangeNode<int> autoPilotPathfindingNodeDistance = new RangeNode<int>(200, 10, 1000);
    public RangeNode<int> autoPilotClearPathDistance = new RangeNode<int>(500, 100, 5000);

    #endregion

    #region Debug and Safety

    public ToggleNode debugMode = new ToggleNode(false);
    public ToggleNode autoQuitHotkeyEnabled = new ToggleNode(false);
    public Keys forcedAutoQuit = Keys.F12;

    #endregion
}
