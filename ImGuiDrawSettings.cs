using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ImGuiNET;
using ExileCore;
using Vector4 = System.Numerics.Vector4;

namespace AreWeThereYet;

internal class ImGuiDrawSettings
{
    // Static buffers to maintain state between frames
    private static readonly Dictionary<string, byte[]> _inputBuffers = new Dictionary<string, byte[]>();
    private static readonly Dictionary<string, bool> _hotkeyPopupStates = new Dictionary<string, bool>();

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
                // Enabled checkbox - native ImGui.Checkbox with ref parameter
                bool enabledValue = AreWeThereYet.Instance.Settings.autoPilotEnabled.Value;
                if (ImGui.Checkbox("Enabled", ref enabledValue))
                {
                    AreWeThereYet.Instance.Settings.autoPilotEnabled.Value = enabledValue;
                }

                // Grace period checkbox
                bool graceValue = AreWeThereYet.Instance.Settings.autoPilotGrace.Value;
                if (ImGui.Checkbox("Remove Grace Period", ref graceValue))
                {
                    AreWeThereYet.Instance.Settings.autoPilotGrace.Value = graceValue;
                }

                // Leader name input - native ImGui.InputText with byte buffer
                string leaderName = NativeInputText("Leader Name: ", "LeaderNameInput", 
                    AreWeThereYet.Instance.Settings.autoPilotLeader.Value, 60, ImGuiInputTextFlags.None);
                
                if (leaderName != AreWeThereYet.Instance.Settings.autoPilotLeader.Value)
                {
                    if (string.IsNullOrWhiteSpace(leaderName))
                    {
                        AreWeThereYet.Instance.Settings.autoPilotLeader.Value = "DefaultLeader";
                    }
                    else
                    {
                        AreWeThereYet.Instance.Settings.autoPilotLeader.Value = 
                            new string(leaderName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                    }
                }

                // Dash enabled checkbox
                bool dashValue = AreWeThereYet.Instance.Settings.autoPilotDashEnabled.Value;
                if (ImGui.Checkbox("Dash Enabled", ref dashValue))
                {
                    AreWeThereYet.Instance.Settings.autoPilotDashEnabled.Value = dashValue;
                }

                // Close follow checkbox
                bool closeFollowValue = AreWeThereYet.Instance.Settings.autoPilotCloseFollow.Value;
                if (ImGui.Checkbox("Close Follow", ref closeFollowValue))
                {
                    AreWeThereYet.Instance.Settings.autoPilotCloseFollow.Value = closeFollowValue;
                }

                // Hotkey selectors - native ImGui.Button + ImGui.BeginPopupModal
                Keys dashKey = NativeHotkeySelector("Dash Key: " + AreWeThereYet.Instance.Settings.autoPilotDashKey.Value, 
                    "DashKeyPopup", AreWeThereYet.Instance.Settings.autoPilotDashKey.Value);
                if (dashKey != AreWeThereYet.Instance.Settings.autoPilotDashKey.Value)
                {
                    AreWeThereYet.Instance.Settings.autoPilotDashKey.Value = dashKey;
                }

                Keys moveKey = NativeHotkeySelector("Move Key: " + AreWeThereYet.Instance.Settings.autoPilotMoveKey.Value, 
                    "MoveKeyPopup", AreWeThereYet.Instance.Settings.autoPilotMoveKey.Value);
                if (moveKey != AreWeThereYet.Instance.Settings.autoPilotMoveKey.Value)
                {
                    AreWeThereYet.Instance.Settings.autoPilotMoveKey.Value = moveKey;
                }

                Keys toggleKey = NativeHotkeySelector("Toggle Key: " + AreWeThereYet.Instance.Settings.autoPilotToggleKey.Value, 
                    "ToggleKeyPopup", AreWeThereYet.Instance.Settings.autoPilotToggleKey.Value);
                if (toggleKey != AreWeThereYet.Instance.Settings.autoPilotToggleKey.Value)
                {
                    AreWeThereYet.Instance.Settings.autoPilotToggleKey.Value = toggleKey;
                }

                // Integer sliders - native ImGui.SliderInt with ref parameters
                int inputFreq = AreWeThereYet.Instance.Settings.autoPilotInputFrequency.Value;
                if (ImGui.SliderInt("Input Freq.", ref inputFreq, 
                    AreWeThereYet.Instance.Settings.autoPilotInputFrequency.Min, 
                    AreWeThereYet.Instance.Settings.autoPilotInputFrequency.Max))
                {
                    AreWeThereYet.Instance.Settings.autoPilotInputFrequency.Value = inputFreq;
                }

                int pathfindingDistance = AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Value;
                if (ImGui.SliderInt("Keep within Distance", ref pathfindingDistance, 
                    AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Min, 
                    AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Max))
                {
                    AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Value = pathfindingDistance;
                }

                int clearPathDistance = AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value;
                if (ImGui.SliderInt("Transition Distance", ref clearPathDistance, 
                    AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Min, 
                    AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Max))
                {
                    AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value = clearPathDistance;
                }
            }
            ImGui.PopStyleColor();
            ImGui.PopID();
        }
        catch (Exception e)
        {
            AreWeThereYet.Instance.LogError(e.ToString());
        }
    }

    /// <summary>
    /// Native ImGui.InputText implementation that handles string conversion
    /// </summary>
    private static string NativeInputText(string label, string bufferId, string currentValue, 
        uint maxLength, ImGuiInputTextFlags flags)
    {
        // Ensure buffer exists for this input field
        if (!_inputBuffers.ContainsKey(bufferId))
        {
            _inputBuffers[bufferId] = new byte[maxLength];
        }

        var buffer = _inputBuffers[bufferId];
        
        // Clear buffer and copy current value
        Array.Clear(buffer, 0, buffer.Length);
        if (!string.IsNullOrEmpty(currentValue))
        {
            var currentValueBytes = Encoding.UTF8.GetBytes(currentValue);
            int copyLength = Math.Min(currentValueBytes.Length, (int)maxLength - 1);
            Array.Copy(currentValueBytes, buffer, copyLength);
        }

        // Use native ImGui.InputText
        ImGui.InputText(label, buffer, maxLength, flags);
        
        // Convert back to string
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    /// <summary>
    /// Native ImGui hotkey selector using Button + PopupModal
    /// </summary>
    private static Keys NativeHotkeySelector(string buttonName, string popupId, Keys currentKey)
    {
        Keys resultKey = currentKey;

        // Initialize popup state if needed
        if (!_hotkeyPopupStates.ContainsKey(popupId))
        {
            _hotkeyPopupStates[popupId] = false;
        }

        // Button to open hotkey selector
        if (ImGui.Button(buttonName))
        {
            ImGui.OpenPopup(popupId);
            _hotkeyPopupStates[popupId] = true;
        }

        // Popup modal for key selection
        bool popupOpen = true;
        if (ImGui.BeginPopupModal(popupId, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"Press new key to change '{currentKey}' or Esc to cancel.");

            // Check for escape key to cancel
            if (Input.GetKeyState(Keys.Escape))
            {
                ImGui.CloseCurrentPopup();
                _hotkeyPopupStates[popupId] = false;
            }
            else
            {
                // Check for any key press (excluding mouse buttons and escape)
                foreach (Keys key in Enum.GetValues(typeof(Keys)))
                {
                    if (Input.GetKeyState(key))
                    {
                        if (key != Keys.Escape && key != Keys.LButton && 
                            key != Keys.RButton && key != Keys.MButton)
                        {
                            resultKey = key;
                            ImGui.CloseCurrentPopup();
                            _hotkeyPopupStates[popupId] = false;
                            break;
                        }
                    }
                }
            }

            ImGui.EndPopup();
        }

        // Handle popup closure
        if (!popupOpen)
        {
            _hotkeyPopupStates[popupId] = false;
        }

        return resultKey;
    }
}
