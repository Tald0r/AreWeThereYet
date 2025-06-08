// UI/ImGuiDrawSettings.cs
using System.Linq;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace AreWeThereYet
{
    internal static class ImGuiDrawSettings
    {
        private static readonly Vector4 Green = new Vector4(0.102f, 0.388f, 0.106f, 1f);
        private static readonly Vector4 Red   = new Vector4(0.388f, 0.102f, 0.102f, 1f);

        public static void DrawImGuiSettings()
        {
            ImGui.Text("AreWeThereYet Plugin Settings");
            ImGui.PushStyleColor(ImGuiCol.Header, AreWeThereYet.Instance.Settings.AutoPilotEnabled.Value ? Green : Red);
            if (ImGui.CollapsingHeader("Auto Pilot"))
            {
                var s = AreWeThereYet.Instance.Settings;
                s.AutoPilotEnabled.Value = ImGui.Checkbox("Enabled", s.AutoPilotEnabled.Value);
                s.LeaderName.Value        = ImGui.InputText("Leader Name", s.LeaderName.Value, 60);
                s.DashEnabled.Value       = ImGui.Checkbox("Dash Enabled", s.DashEnabled.Value);
                s.CloseFollow.Value       = ImGui.Checkbox("Close Follow", s.CloseFollow.Value);
                s.DashKey.Value           = HotkeySelector("Dash Key", s.DashKey.Value);
                s.MoveKey.Value           = HotkeySelector("Move Key", s.MoveKey.Value);
                s.ToggleKey.Value         = HotkeySelector("Toggle Key", s.ToggleKey.Value);
                s.TakeWaypoints.Value     = ImGui.Checkbox("Take Waypoints", s.TakeWaypoints.Value);
                s.InputFrequency.Value    = ImGui.SliderInt("Input Frequency", s.InputFrequency.Value, s.InputFrequency.Min, s.InputFrequency.Max);
                s.NodeDistance.Value      = ImGui.SliderInt("Node Distance", s.NodeDistance.Value, s.NodeDistance.Min, s.NodeDistance.Max);
                s.ClearDistance.Value     = ImGui.SliderInt("Clear Distance", s.ClearDistance.Value, s.ClearDistance.Min, s.ClearDistance.Max);
            }
            ImGui.PopStyleColor();
        }

        private static Keys HotkeySelector(string label, Keys current)
        {
            ImGui.Text(label + ": " + current);
            if (ImGui.IsItemClicked())
            {
                foreach (Keys k in System.Enum.GetValues(typeof(Keys)))
                    if (ImGui.IsKeyPressed((uint)k))
                        return k;
            }
            return current;
        }
    }
}
