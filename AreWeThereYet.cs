using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using SharpDX;
using AreWeThereYet.Utils;

namespace AreWeThereYet;

public class AreWeThereYet : BaseSettingsPlugin<AreWeThereYetSettings>
{
    internal static AreWeThereYet Instance;
    internal AutoPilot autoPilot = new AutoPilot();
    internal LineOfSight lineOfSight;

    private List<Buff> buffs;
    internal DateTime lastTimeAny;
    internal Entity localPlayer;
    internal Life player;
    internal Vector3 playerPosition;
    private Coroutine skillCoroutine;

    public override bool Initialise()
    {
        if (Instance == null)
            Instance = this;
        
        PluginLog.Initialize(DirectoryFullName);
        
        GameController.LeftPanel.WantUse(() => Settings.Enable);
        skillCoroutine = new Coroutine(WaitForAreaChange(), this);
        Core.ParallelRunner.Run(skillCoroutine);
        Input.RegisterKey(Settings.AutoPilot.ToggleKey.Value);
        Settings.AutoPilot.ToggleKey.OnValueChanged += () => { Input.RegisterKey(Settings.AutoPilot.ToggleKey.Value); };

        lineOfSight = new LineOfSight(GameController);

        autoPilot.StartCoroutine();
        return true;
    }

    internal Vector2 GetMousePosition()
    {
        return new Vector2(GameController.IngameState.MousePosX, GameController.IngameState.MousePosY);
    }

    private IEnumerator WaitForAreaChange()
    {
        while (localPlayer == null || GameController.IsLoading || !GameController.InGame)
            yield return new WaitTime(200);

        yield return new WaitTime(1000);
    }

    public override void AreaChange(AreaInstance area)
    {
        base.AreaChange(area);
        
        // Simple area change - LineOfSight handles everything automatically
        EventBus.Instance.Publish(new AreaChangeEvent());
        autoPilot.AreaChange();
    }

    public override void Render()
    {
        try
        {
            if (!Settings.Enable) return;

            try
            {
                if (Settings.AutoPilot.Enabled && Settings.AutoPilot.RemoveGracePeriod && buffs != null && buffs.Exists(x => x.Name == "grace_period"))
                {
                    Keyboard.KeyPress(Settings.AutoPilot.MoveKey);
                }
                autoPilot.Render();

                // Publish render event for LineOfSight debug visualization
                EventBus.Instance.Publish(new RenderEvent(Graphics));
            }
            catch (Exception e)
            {
                LogError(e.ToString());
            }

            if (GameController?.Game?.IngameState?.Data?.LocalPlayer == null || GameController?.IngameState?.IngameUi == null)
                return;
            var chatField = GameController?.IngameState?.IngameUi?.ChatPanel?.ChatInputElement?.IsVisible;
            if (chatField != null && (bool)chatField)
                return;

            localPlayer = GameController.Game.IngameState.Data.LocalPlayer;
            player = localPlayer.GetComponent<Life>();
            buffs = localPlayer.GetComponent<Buffs>().BuffsList;
            playerPosition = localPlayer.Pos;

            if (GameController.Area.CurrentArea.IsHideout || GameController.Area.CurrentArea.IsTown ||
                GameController.IngameState.IngameUi.NpcDialog.IsVisible ||
                GameController.IngameState.IngameUi.SellWindow.IsVisible || MenuWindow.IsOpened ||
                !GameController.InGame || GameController.IsLoading) return;

            if (buffs.Exists(x => x.Name == "grace_period") ||
                !GameController.IsForeGroundCache)
                return;

        }
        catch (Exception e)
        { LogError(e.ToString()); }
    }
}
