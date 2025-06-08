// AreWeThereYet.cs
using System;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.Shared;
using SharpDX;

namespace AreWeThereYet
{
    public class AreWeThereYet : BaseSettingsPlugin<AreWeThereYetSettings>
    {
        internal static AreWeThereYet Instance;
        private AutoPilot autoPilot;

        public override bool Initialise()
        {
            if (Instance == null) Instance = this;
            GameController.LeftPanel.WantUse(() => Settings.Enable.Value);

            Input.RegisterKey(Settings.ToggleKey.Value);
            Input.RegisterKey(Settings.MoveKey.Value);
            Input.RegisterKey(Settings.DashKey.Value);
            
            Settings.ToggleKey.OnValueChanged += () => Input.RegisterKey(Settings.ToggleKey.Value);
            Settings.MoveKey.  OnValueChanged += () => Input.RegisterKey(Settings.MoveKey.Value);
            Settings.DashKey.  OnValueChanged += () => Input.RegisterKey(Settings.DashKey.Value);
            
            autoPilot = new AutoPilot();
            autoPilot.StartCoroutine();
            return true;
        }

        public override void AreaChange(AreaInstance area)
        {
            base.AreaChange(area);
            autoPilot.AreaChange();
        }

        public override void DrawSettings()
        {
            if (Settings.Enable.Value)
                ImGuiDrawSettings.DrawImGuiSettings();
        }

        public override void Render()
        {
            try
            {
                if (!Settings.Enable.Value) return;
                autoPilot.Render();
            }
            catch (Exception e)
            {
                LogError(e.ToString());
            }
        }
    }
}
