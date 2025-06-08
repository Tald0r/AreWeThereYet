// Utilities/InputManager.cs
using System.Windows.Forms;
using ExileCore.Shared.Nodes;

namespace AreWeThereYet.Utilities
{
    public static class InputManager
    {
        public static void RegisterHotkey(HotkeyNode node)
        {
            Input.RegisterKey(node.Value);
            node.OnValueChanged += () => Input.RegisterKey(node.Value);
        }

        public static void InitAll(AreWeThereYetSettings s)
        {
            RegisterHotkey(s.ToggleKey);
            RegisterHotkey(s.MoveKey);
            RegisterHotkey(s.DashKey);
        }
    }
}
