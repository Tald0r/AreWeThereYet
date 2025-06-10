using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using SharpDX;
using AreWeThereYet.Utils;

namespace AreWeThereYet;

public class AutoPilot
{
    private Coroutine autoPilotCoroutine;
    private readonly Random random = new Random();
        
    private Vector3 lastTargetPosition;
    private Vector3 lastPlayerPosition;
    private Entity followTarget;

    private List<TaskNode> tasks = new List<TaskNode>();

    private LineOfSight LineOfSight => AreWeThereYet.Instance.lineOfSight;

    private void ResetPathing()
    {
        tasks = new List<TaskNode>();
        followTarget = null;
        lastTargetPosition = Vector3.Zero;
        lastPlayerPosition = Vector3.Zero;
    }

    private PartyElementWindow GetLeaderPartyElement()
    {
        try
        {
            foreach (var partyElementWindow in PartyElements.GetPlayerInfoElementList())
            {
                if (string.Equals(partyElementWindow?.PlayerName?.ToLower(), AreWeThereYet.Instance.Settings.autoPilotLeader.Value.ToLower(), StringComparison.CurrentCultureIgnoreCase))
                {
                    return partyElementWindow;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private LabelOnGround GetBestPortalLabel(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var currentZoneName = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            if(leaderPartyElement.ZoneName.Equals(currentZoneName) || (!leaderPartyElement.ZoneName.Equals(currentZoneName) && (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout) || AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.RealLevel >= 68)
            {
                var portalLabels =
                    AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                            x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible && x.ItemOnGround != null && 
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") || x.ItemOnGround.Metadata.ToLower().Contains("portal") ))
                        .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos)).ToList();

                return AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout != null && (bool)AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.IsHideout
                    ? portalLabels?[random.Next(portalLabels.Count)]
                    : portalLabels?.FirstOrDefault();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private Vector2 GetTpButton(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
            var elemCenter = (Vector2) leaderPartyElement?.TpButton?.GetClientRectCache.Center;
            var finalPos = new Vector2(elemCenter.X + windowOffset.X, elemCenter.Y + windowOffset.Y);
				
            return finalPos;
        }
        catch
        {
            return Vector2.Zero;
        }
    }

    private Element GetTpConfirmation()
    {
        try
        {
            var ui = AreWeThereYet.Instance.GameController?.IngameState?.IngameUi?.PopUpWindow;

            if (ui.Children[0].Children[0].Children[0].Text.Equals("Are you sure you want to teleport to this player's location?"))
                return ui.Children[0].Children[0].Children[3].Children[0];

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void AreaChange()
    {
        ResetPathing();
            
    }

    public void StartCoroutine()
    {
        autoPilotCoroutine = new Coroutine(AutoPilotLogic(), AreWeThereYet.Instance, "AutoPilot");
        Core.ParallelRunner.Run(autoPilotCoroutine);
    }

    private IEnumerator MouseoverItem(Entity item)
    {
        var uiLoot = AreWeThereYet.Instance.GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(I => I.IsVisible && I.ItemOnGround.Id == item.Id);
        if (uiLoot == null) yield return null;
        var clickPos = uiLoot?.Label?.GetClientRect().Center;
        if (clickPos != null)
        {
            Mouse.SetCursorPos(new Vector2(
                clickPos.Value.X + random.Next(-15, 15),
                clickPos.Value.Y + random.Next(-10, 10)));
        }
	        
        yield return new WaitTime(30 + random.Next(AreWeThereYet.Instance.Settings.autoPilotInputFrequency));
    }

    private IEnumerator AutoPilotLogic()
    {
        while (true)
        {
            if (!AreWeThereYet.Instance.Settings.Enable.Value || !AreWeThereYet.Instance.Settings.autoPilotEnabled.Value || AreWeThereYet.Instance.localPlayer == null || !AreWeThereYet.Instance.localPlayer.IsAlive || 
                !AreWeThereYet.Instance.GameController.IsForeGroundCache || MenuWindow.IsOpened || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            {
                yield return new WaitTime(100);
                continue;
            }
		        
            followTarget = GetFollowingTarget();
            var leaderPartyElement = GetLeaderPartyElement();

            if (followTarget == null && !leaderPartyElement.ZoneName.Equals(AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName)) {
                var portal = GetBestPortalLabel(leaderPartyElement);
                if (portal != null) {
                    tasks.Add(new TaskNode(portal, AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Value, TaskNodeType.Transition));
                } else {
                    var tpConfirmation = GetTpConfirmation();
                    if (tpConfirmation != null)
                    {
                        yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect()
                            .Center);
                        yield return new WaitTime(200);
                        yield return Mouse.LeftClick();
                        yield return new WaitTime(1000);
                    }
						
                    var tpButton = GetTpButton(leaderPartyElement);
                    if(!tpButton.Equals(Vector2.Zero))
                    {
                        yield return Mouse.SetCursorPosHuman(tpButton, false);
                        yield return new WaitTime(200);
                        yield return Mouse.LeftClick();
                        yield return new WaitTime(200);
                    }
                }
            } else if (followTarget != null) {
                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);
                if (distanceToLeader >= AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value)
                {
                    var distanceMoved = Vector3.Distance(lastTargetPosition, followTarget.Pos);
                    if (lastTargetPosition != Vector3.Zero && distanceMoved > AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value)
                    {
                        var transition = GetBestPortalLabel(leaderPartyElement);
                        if (transition != null && transition.ItemOnGround.DistancePlayer < 80)
                            tasks.Add(new TaskNode(transition,200, TaskNodeType.Transition));
                    }
                    else if (tasks.Count == 0 && distanceMoved < 2000 && distanceToLeader > 200 && distanceToLeader < 2000)
                    {
                        tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance));
                    }
							
                    else if (tasks.Count > 0)
                    {
                        var distanceFromLastTask = Vector3.Distance(tasks.Last().WorldPosition, followTarget.Pos);
                        if (distanceFromLastTask >= AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance)
                            tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance));
                    }
                }
                else
                {
                    if (tasks.Count > 0)
                    {
                        for (var i = tasks.Count - 1; i >= 0; i--)
                            if (tasks[i].Type == TaskNodeType.Movement || tasks[i].Type == TaskNodeType.Transition)
                                tasks.RemoveAt(i);
                        yield return null;
                    }
                    if (AreWeThereYet.Instance.Settings.autoPilotCloseFollow.Value)
                    {
                        if (distanceToLeader >= AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Value)
                            tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance));
                    }

                    var questLoot = GetQuestItem();
                    if (questLoot != null &&
                        Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) < AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value &&
                        tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) == null)
                        tasks.Add(new TaskNode(questLoot.Pos, AreWeThereYet.Instance.Settings.autoPilotClearPathDistance, TaskNodeType.Loot));
                }
                if (followTarget?.Pos != null)
                    lastTargetPosition = followTarget.Pos;
            }

            if (tasks?.Count > 0)
            {
                var currentTask = tasks.First();
                var taskDistance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, currentTask.WorldPosition);
                var playerDistanceMoved = Vector3.Distance(AreWeThereYet.Instance.playerPosition, lastPlayerPosition);

                if (currentTask.Type == TaskNodeType.Transition && 
                    playerDistanceMoved >= AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value)
                {
                    tasks.RemoveAt(0);
                    lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
                    yield return null;
                    continue;
                }
                switch (currentTask.Type)
                {
                    case TaskNodeType.Movement:
                        if (AreWeThereYet.Instance.Settings.autoPilotDashEnabled &&
                        ShouldUseDash(currentTask.WorldPosition.WorldToGrid()))
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyPress(AreWeThereYet.Instance.Settings.autoPilotDashKey);
                            yield return new WaitTime(random.Next(25) + 30);
                        }
                        else
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Input.KeyDown(AreWeThereYet.Instance.Settings.autoPilotMoveKey);
                            yield return new WaitTime(random.Next(25) + 30);
                            Input.KeyUp(AreWeThereYet.Instance.Settings.autoPilotMoveKey);
                        }

                        if (taskDistance <= AreWeThereYet.Instance.Settings.autoPilotPathfindingNodeDistance.Value * 1.5)
                            tasks.RemoveAt(0);
                        yield return null;
                        yield return null;
                        continue;
                    case TaskNodeType.Loot:
                    {
                        currentTask.AttemptCount++;
                        var questLoot = GetQuestItem();
                        if (questLoot == null
                            || currentTask.AttemptCount > 2
                            || Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) >=
                            AreWeThereYet.Instance.Settings.autoPilotClearPathDistance.Value)
                        {
                            tasks.RemoveAt(0);
                            yield return null;
                        }

                        Input.KeyUp(AreWeThereYet.Instance.Settings.autoPilotMoveKey);
                        yield return new WaitTime(AreWeThereYet.Instance.Settings.autoPilotInputFrequency);
                        if (questLoot != null)
                        {
                            var targetInfo = questLoot.GetComponent<Targetable>();
                            switch (targetInfo.isTargeted)
                            {
                                case false:
                                    yield return MouseoverItem(questLoot);
                                    break;
                                case true:
                                    yield return Mouse.LeftClick();
                                    yield return new WaitTime(1000);
                                    break;
                            }
                        }

                        break;
                    }
                    case TaskNodeType.Transition:
                    {
                        Input.KeyUp(AreWeThereYet.Instance.Settings.autoPilotMoveKey);
                        yield return new WaitTime(60);
                        yield return Mouse.SetCursorPosAndLeftClickHuman(new Vector2(currentTask.LabelOnGround.Label.GetClientRect().Center.X, currentTask.LabelOnGround.Label.GetClientRect().Center.Y), 100);
                        yield return new WaitTime(300);

                        currentTask.AttemptCount++;
                        if (currentTask.AttemptCount > 6)
                            tasks.RemoveAt(0);
                        {
                            yield return null;
                            continue;
                        }
                    }

                    case TaskNodeType.ClaimWaypoint:
                    {
                        if (Vector3.Distance(AreWeThereYet.Instance.playerPosition, currentTask.WorldPosition) > 150)
                        {
                            var screenPos = Helper.WorldToValidScreenPosition(currentTask.WorldPosition);
                            Input.KeyUp(AreWeThereYet.Instance.Settings.autoPilotMoveKey);
                            yield return new WaitTime(AreWeThereYet.Instance.Settings.autoPilotInputFrequency);
                            yield return Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
                            yield return new WaitTime(1000);
                        }
                        currentTask.AttemptCount++;
                        if (currentTask.AttemptCount > 3)
                            tasks.RemoveAt(0);
                        {
                            yield return null;
                            continue;
                        }
                    }
                }
            }
            lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
            yield return new WaitTime(50);
        }
    }
    
    private bool ShouldUseDash(Vector2 targetPosition)
    {
        if (LineOfSight == null)
            return false;

        var playerPos = AreWeThereYet.Instance.GameController.Player.GridPos;
        var distance = Vector2.Distance(playerPos, targetPosition);
        
        // Don't dash for very short or very long distances
        if (distance < 30 || distance > 150)
            return false;
        
        // Convert SharpDX.Vector2 to System.Numerics.Vector2 for HasLineOfSight
        var playerPosNumerics = new System.Numerics.Vector2(playerPos.X, playerPos.Y);
        var targetPosNumerics = new System.Numerics.Vector2(targetPosition.X, targetPosition.Y);

        // Use line of sight to determine if there's an obstacle to dash through
        var hasLineOfSight = LineOfSight.HasLineOfSight(playerPosNumerics, targetPosNumerics);
        
        // Dash when there's NO line of sight (obstacles in the way)
        // but the distance is reasonable for dashing
        return !hasLineOfSight && distance >= 50;
    }

    private Entity GetFollowingTarget()
    {
        try
        {
            string leaderName = AreWeThereYet.Instance.Settings.autoPilotLeader.Value.ToLower();
            return AreWeThereYet.Instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player].FirstOrDefault(x => string.Equals(x.GetComponent<Player>()?.PlayerName.ToLower(), leaderName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static Entity GetQuestItem()
    {
        try
        {
            return AreWeThereYet.Instance.GameController.EntityListWrapper.Entities
                .Where(e => e?.Type == EntityType.WorldItem && e.IsTargetable && e.HasComponent<WorldItem>())
                .FirstOrDefault(e =>
                {
                    var itemEntity = e.GetComponent<WorldItem>().ItemEntity;
                    return AreWeThereYet.Instance.GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName ==
                           "QuestItem";
                });
        }
        catch
        {
            return null;
        }
    }
		
    public void Render()
    {
        if (AreWeThereYet.Instance.Settings.autoPilotToggleKey.PressedOnce())
        {
            AreWeThereYet.Instance.Settings.autoPilotEnabled.SetValueNoEvent(!AreWeThereYet.Instance.Settings.autoPilotEnabled.Value);
            tasks = new List<TaskNode>();				
        }
			
        if (!AreWeThereYet.Instance.Settings.autoPilotEnabled || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            return;

        try
        {
            var portalLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null &&
                    (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                     x.ItemOnGround.Metadata.ToLower().Contains("portal"))).ToList();

            foreach (var portal in portalLabels)
            {
                AreWeThereYet.Instance.Graphics.DrawLine(portal.Label.GetClientRectCache.TopLeft, portal.Label.GetClientRectCache.TopRight, 2f,Color.Firebrick);
            }
        }
        catch (Exception)
        {
        }
			
        try
        {
            var taskCount = 0;
            var dist = 0f;
            var cachedTasks = tasks;

            var lineWidth = (float)AreWeThereYet.Instance.Settings.TaskLineWidth.Value;
            var lineColor = AreWeThereYet.Instance.Settings.TaskColor.Value;
            if (cachedTasks?.Count > 0)
            {
                AreWeThereYet.Instance.Graphics.DrawText(
                    "Current Task: " + cachedTasks[0].Type,
                    new Vector2(500, 180));
                foreach (var task in cachedTasks.TakeWhile(task => task?.WorldPosition != null))
                {
                    if (taskCount == 0)
                    {
                        AreWeThereYet.Instance.Graphics.DrawLine(
                            Helper.WorldToValidScreenPosition(AreWeThereYet.Instance.playerPosition),
                            Helper.WorldToValidScreenPosition(task.WorldPosition), lineWidth, lineColor);
                        dist = Vector3.Distance(AreWeThereYet.Instance.playerPosition, task.WorldPosition);
                    }
                    else
                    {
                        AreWeThereYet.Instance.Graphics.DrawLine(Helper.WorldToValidScreenPosition(task.WorldPosition),
                            Helper.WorldToValidScreenPosition(cachedTasks[taskCount - 1].WorldPosition), lineWidth, lineColor);
                    }

                    taskCount++;
                }
            }
            if (AreWeThereYet.Instance.localPlayer != null)
            {
                var targetDist = Vector3.Distance(AreWeThereYet.Instance.playerPosition, lastTargetPosition);
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Follow Enabled: {AreWeThereYet.Instance.Settings.autoPilotEnabled.Value}", new System.Numerics.Vector2(500, 120));
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Task Count: {taskCount:D} Next WP Distance: {dist:F} Target Distance: {targetDist:F}",
                    new System.Numerics.Vector2(500, 140));
					
            }
        }
        catch (Exception)
        {
        }

        AreWeThereYet.Instance.Graphics.DrawText("AutoPilot: Active", new System.Numerics.Vector2(350, 120));
        AreWeThereYet.Instance.Graphics.DrawText("Coroutine: " + (autoPilotCoroutine.Running ? "Active" : "Dead"), new System.Numerics.Vector2(350, 140));
        AreWeThereYet.Instance.Graphics.DrawText("Leader: " + "[ "+ AreWeThereYet.Instance.Settings.autoPilotLeader.Value + " ] " +(followTarget != null ? "Found" : "Null"), new System.Numerics.Vector2(500, 160));
        AreWeThereYet.Instance.Graphics.DrawLine(new System.Numerics.Vector2(490, 110), new System.Numerics.Vector2(490,210), 1, Color.White);
    }
}
