using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace AreWeThereYet
{
    public class AreWeThereYetSettings : ISettings
    {
        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        public ToggleNode AutoPilotEnabled { get; set; } = new ToggleNode(false);
        public TextNode LeaderName { get; set; } = new TextNode("");
        public ToggleNode DashEnabled { get; set; } = new ToggleNode(false);
        public ToggleNode CloseFollow { get; set; } = new ToggleNode(true);
        public HotkeyNode DashKey { get; set; } = new HotkeyNode(Keys.W);
        public HotkeyNode MoveKey { get; set; } = new HotkeyNode(Keys.Q);
        public HotkeyNode ToggleKey { get; set; } = new HotkeyNode(Keys.NumPad9);
        public ToggleNode TakeWaypoints { get; set; } = new ToggleNode(true);
        public RangeNode<int> InputFrequency { get; set; } = new RangeNode<int>(50, 1, 100);
        public RangeNode<int> NodeDistance { get; set; } = new RangeNode<int>(200, 10, 1000);
        public RangeNode<int> ClearDistance { get; set; } = new RangeNode<int>(500, 100, 5000);
    }
}
