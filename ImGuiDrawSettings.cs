// ImGuiDrawSettings.cs
using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace AreWeThereYet;

internal class ImGuiDrawSettings
{
    private static Vector4 _donationColorTarget = new Vector4(0.454f, 0.031f, 0.768f, 1f);
    private static Vector4 _donationColorCurrent = new Vector4(0.454f, 0.031f, 0.768f, 1f);
    private static void SetText(string pText)
    {
        var staThread = new Thread(
            delegate()
            {
                Clipboard.SetText(pText);
            });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
    }

    internal static void DrawImGuiSettings()
    {
        var green = new Vector4(0.102f, 0.388f, 0.106f, 1.000f);
        var red = new Vector4(0.388f, 0.102f, 0.102f, 1.000f);

        var collapsingHeaderFlags = ImGuiTreeNodeFlags.CollapsingHeader;
        ImGui.Text("AreWeThereYet - AutoPilot Plugin");

        try
        {
            ImGui.PushStyleColor(ImGuiCol.Header, AreWeThereYet.Instance.Settings.autoPilotEnabled ? green : red);
            ImGui.PushID(0);
            if (ImGui.TreeNodeEx("Auto Pilot", collapsingHeaderFlags))
            {
                AreWeThereYet.Instance.Settings.autoPilotEnabled.Value =
                    ImGuiExtension.Checkbox("Enabled", AreWeThereYet.Instance.Settings.autoPilotEnabled.Value);
                AreWeThereYet.Instance.Settings.autoPilotGrace.Value =
                    ImGuiExtension.Checkbox("Remove Grace Period", AreWeThereYet.Instance.Settings.autoPilotGrace.Value);
                AreWeThereYet.Instance.Settings.autoPilotLeader = ImGuiExtension.InputText("Leader Name: ", AreWeThereYet.Instance.Settings.autoPilotLeader, 60, ImGuiInputTextFlags.None);
                if (string.IsNullOrWhiteSpace(AreWeThereYet.Instance.Settings.autoPilotLeader.Value))
                {
                    AreWeThereYet.Instance.Settings.autoPilotLeader.Value = "DefaultLeader";
                }
                else
                {
                    AreWeThereYet.Instance.Settings.autoPilotLeader.Value = new string(AreWeThereYet.Instance.Settings.autoPilotLeader.Value.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                }
                AreWeThereYet.Instance.Settings.autoPilotDashEnabled.Value = ImGuiExtension.Checkbox(
                    "Dash Enabled", AreWeThereYet.Instance.Settings.autoPilotDashEnabled.Value);
                AreWeThereYet.Instance.Settings.autoPilotCloseFollow.Value = ImGuiExtension.Checkbox(
                    "Close Follow", AreWeThereYet.Instance.Settings.autoPilotCloseFollow.Value);
                AreWeThereYet.Instance.Settings.autoPilotDashKey.Value = ImGuiExtension.HotkeySelector(
                    "Dash Key: " + AreWeThereYet.Instance.Settings.autoPilotDashKey.Value, AreWeThereYet.Instance.Settings.autoPilotDashKey);
                AreWeThereYet.Instance.Settings.autoPilotMoveKey.Value = ImGuiExtension.HotkeySelector(
                    "Move Key: " + AreWeThereYet.Instance.Settings.autoPilotMoveKey.Value, AreWeThereYet.Instance.Settings.autoPilotMoveKey);
                AreWeThereYet.Instance.Settings.autoPilotToggleKey.Value = ImGuiExtension.HotkeySelector(
                    "Toggle Key: " + AreWeThereYet.Instance.Settings.autoPilotToggleKey.Value, AreWeThereYet.Instance.Settings.autoPilotToggleKey);
                AreWeThereYet.Instance.Settings.autoPilotTakeWaypoints.Value = ImGuiExtension.Checkbox(
                    "Take Waypoints", AreWeThereYet.Instance.Settings.autoPilotTakeWaypoints.Value);
                AreWeThereYet.Instance.Settings.autoPilotInputFrequency.Value =
                    ImGuiExtension.IntSlider("Input Freq.", AreWeThereYet.Instance.Settings.autoPilotInputFrequency);
                AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Value =
                    ImGuiExtension.IntSlider("Keep within Distance", AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance);
                AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value =
                    ImGuiExtension.IntSlider("Transition Distance", AreWeThereYet.Instance.Settings.autoPilotClearPathDistance);
            }
        }
        catch (Exception e)
        {
            AreWeThereYet.Instance.LogError(e.ToString());
        }

        try
        {
            if (_donationColorCurrent.X == _donationColorTarget.X &&
                _donationColorCurrent.Y == _donationColorTarget.Y &&
                _donationColorCurrent.Z == _donationColorTarget.Z)
            {
                _donationColorTarget = new Vector4(Helper.random.NextFloat(0, 1), Helper.random.NextFloat(0, 1),
                    Helper.random.NextFloat(0, 1), 1f);
            }
            else
            {
                var deltaTime = 1f / 60f;
                    
                _donationColorCurrent.X = Helper.MoveTowards(_donationColorCurrent.X, _donationColorTarget.X, deltaTime);
                _donationColorCurrent.Y = Helper.MoveTowards(_donationColorCurrent.Y, _donationColorTarget.Y, deltaTime);
                _donationColorCurrent.Z = Helper.MoveTowards(_donationColorCurrent.Z, _donationColorTarget.Z, deltaTime);
            }
            ImGui.PushStyleColor(ImGuiCol.Header, _donationColorCurrent);
            ImGui.PushID(99999);
            if (ImGui.TreeNodeEx("Debug and Safety", collapsingHeaderFlags))
            {
                AreWeThereYet.Instance.Settings.debugMode.Value = ImGuiExtension.Checkbox("Turn on Debug Mode",
                    AreWeThereYet.Instance.Settings.debugMode.Value);
                AreWeThereYet.Instance.Settings.autoQuitHotkeyEnabled.Value = ImGuiExtension.Checkbox("Enable Auto Quit Hotkey",
                    AreWeThereYet.Instance.Settings.autoQuitHotkeyEnabled.Value);
            }
        }
        catch (Exception e)
        {
            AreWeThereYet.Instance.LogError(e.ToString());
        }
    }
}
