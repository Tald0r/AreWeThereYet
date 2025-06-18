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
    private bool isMoveKeyPressed = false;
        
    private Vector3 lastTargetPosition;
    private Vector3 lastPlayerPosition;
    private Entity followTarget;

    private List<TaskNode> tasks = new List<TaskNode>();

    private LineOfSight LineOfSight => AreWeThereYet.Instance.lineOfSight;

    private string _lastKnownLeaderZone = "";
    private DateTime _leaderZoneChangeTime = DateTime.MinValue;

    private void ResetPathing()
    {
        tasks = new List<TaskNode>();
        followTarget = null;
        lastTargetPosition = Vector3.Zero;
        lastPlayerPosition = Vector3.Zero;
    }

    public void AreaChange()
    {
        ResetPathing();
        if (isMoveKeyPressed)
        {
            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
            isMoveKeyPressed = false;
        }
    }

    public void StartCoroutine()
    {
        autoPilotCoroutine = new Coroutine(AutoPilotLogic(), AreWeThereYet.Instance, "AutoPilot");
        Core.ParallelRunner.Run(autoPilotCoroutine);
    }

    private PartyElementWindow GetLeaderPartyElement()
    {
        try
        {
            foreach (var partyElementWindow in PartyElements.GetPlayerInfoElementList())
            {
                if (string.Equals(partyElementWindow?.PlayerName?.ToLower(), AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value.ToLower(), StringComparison.CurrentCultureIgnoreCase))
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

    private bool IsLeaderZoneInfoReliable(PartyElementWindow leaderPartyElement)
    {
        try
        {
            // Check if zone name looks valid (not empty, not obviously stale)
            var zoneName = leaderPartyElement.ZoneName;
            var currentZone = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            
            // Invalid if empty or same as current zone when leader should be elsewhere
            if (string.IsNullOrEmpty(zoneName) || zoneName.Equals(currentZone))
                return false;
                
            // Check if zone name changed very recently (might still be updating)
            var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
            if (timeSinceChange < TimeSpan.FromMilliseconds(AreWeThereYet.Instance.Settings.AutoPilot.ZoneUpdateBuffer.Value))
                return false;
                
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AddBreadcrumbTask(Vector3 leaderPos)
    {
        try
        {
            const float LEADER_BREADCRUMB_DISTANCE = 50f; // Distance before dropping a new breadcrumb

            // Find the position of the very last movement task in our queue.
            // If there are no tasks, use the bot's last known position.
            var lastBreadcrumbPos = tasks.LastOrDefault(t => t.Type == TaskNodeType.Movement)?.WorldPosition ?? lastPlayerPosition;

            // Only add a new breadcrumb if the leader has moved far enough from the last one.
            if (Vector3.Distance(leaderPos, lastBreadcrumbPos) >= LEADER_BREADCRUMB_DISTANCE)
            {
                // The completion bound (35) should match the distance check in the task execution logic.
                tasks.Add(new TaskNode(leaderPos, 35, TaskNodeType.Movement));

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Added breadcrumb. Total movement tasks: {tasks.Count(t => t.Type == TaskNodeType.Movement)}");
                }
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"AddBreadcrumbTask failed: {ex.Message}");
        }
    }

    // This is the new, "smart" waypoint finder
    private TaskNode FindNextWaypoint(List<TaskNode> path, Vector3 playerPos)
    {
        if (path == null || !path.Any())
        {
            return null;
        }

        // Always start by targeting the very next point. This is our safe fallback.
        var bestTarget = path.First();

        // The number of waypoints to look ahead for a potential shortcut.
        const int lookAheadLimit = 5; 

        // Iterate through the next few points in the path to see if we can take a shortcut.
        // We go backwards from the furthest point to the closest.
        for (int i = Math.Min(path.Count - 1, lookAheadLimit); i > 0; i--)
        {
            var potentialShortcut = path[i];
            
            // Use the built-in .WorldToGrid() extension method for clean conversion.
            if (LineOfSight.HasLineOfSight(playerPos.WorldToGrid().ToNumerics(), potentialShortcut.WorldPosition.WorldToGrid().ToNumerics()))
            {
                // We found a valid shortcut! This is our new target.
                bestTarget = potentialShortcut;

                // Since we are taking a shortcut, we can remove all the breadcrumbs we're skipping.
                // This is crucial for keeping the path clean.
                for (int j = 0; j < i; j++)
                {
                    tasks.Remove(path[j]);
                }
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Found shortcut, skipping {i} waypoints.");
                }

                // Once we find the furthest possible shortcut, we take it and stop looking.
                return bestTarget;
            }
        }

        // If no shortcuts were found, just return the very next breadcrumb.
        return bestTarget;
    }


    private LabelOnGround GetBestPortalLabel(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var currentZoneName = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
            var realLevel = AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.RealLevel ?? 0;

            // Enhanced logic: differentiate between leveling zones and endgame content
            if (isHideout || realLevel >= 68)
            {
                // ENDGAME/HIDEOUT: Any portal is fine (maps, hideout transitions)
                var portalLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                    .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                            x.Label.IsVisible && x.ItemOnGround != null &&
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                x.ItemOnGround.Metadata.ToLower().Contains("portal")))
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Endgame/Hideout portal search: Found {portalLabels?.Count ?? 0} portals");
                }

                return isHideout && portalLabels?.Count > 0
                    ? portalLabels[random.Next(portalLabels.Count)] // Random portal in hideout
                    : portalLabels?.FirstOrDefault(); // Closest portal in endgame
            }
            else
            {
                // LEVELING ZONES: Must find portal that leads to leader's specific zone
                var portalLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                    .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                            x.Label.IsVisible && x.ItemOnGround != null &&
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                x.ItemOnGround.Metadata.ToLower().Contains("portal")) &&
                            x.Label.Text.ToLower().Contains(leaderPartyElement.ZoneName.ToLower())) // IMPORTANT KEY IMPROVEMENT: Check portal text
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    var allPortals = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                        .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                                x.Label.IsVisible && x.ItemOnGround != null &&
                                (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                    x.ItemOnGround.Metadata.ToLower().Contains("portal")))
                        .ToList();

                    AreWeThereYet.Instance.LogMessage($"Leveling zone portal search:");
                    AreWeThereYet.Instance.LogMessage($"  - Leader zone: '{leaderPartyElement.ZoneName}'");
                    AreWeThereYet.Instance.LogMessage($"  - All portals: {allPortals?.Count ?? 0}");
                    AreWeThereYet.Instance.LogMessage($"  - Matching portals: {portalLabels?.Count ?? 0}");

                    if (allPortals != null)
                    {
                        foreach (var portal in allPortals)
                        {
                            var matches = portal.Label.Text.Contains(leaderPartyElement.ZoneName);
                            AreWeThereYet.Instance.LogMessage($"    Portal: '{portal.Label.Text}' -> {(matches ? "MATCH" : "No match")}");
                        }
                    }
                }

                // EXPLICIT NULL CHECK: If no matching portals found in leveling zone, return null for teleport fallback
                if (portalLabels == null || portalLabels.Count == 0)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"No matching portal found for leader zone '{leaderPartyElement.ZoneName}' - will use teleport button fallback");
                    }
                    return null; // Force teleport button usage
                }

                return portalLabels.FirstOrDefault(); // Return closest matching portal
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetBestPortalLabel failed: {ex.Message}");
            return null; // Exception fallback
        }
    }

    private LabelOnGround GetMercenaryOptInButton()
    {
        try
        {
            // Better null checking to prevent the exception
            if (AreWeThereYet.Instance?.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels == null)
                return null;
                
            var mercenaryLabels = AreWeThereYet.Instance.GameController.Game.IngameState.IngameUi.ItemsOnGroundLabels
                .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid && 
                        x.Label.IsVisible && x.ItemOnGround != null &&
                        !string.IsNullOrEmpty(x.ItemOnGround.Metadata) &&
                        x.ItemOnGround.Metadata.ToLower().Contains("mercenary") &&
                        x.Label.Children?.Count > 2 && x.Label.Children[2] != null &&
                        x.Label.Children[2].IsVisible)
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.Pos))
                .ToList();
                
            return mercenaryLabels?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetMercenaryOptInButton failed: {ex.Message}");
            return null;
        }
    }

    private Vector2 GetMercenaryOptInButtonPosition(LabelOnGround mercenaryLabel)
    {
        try
        {
            if (mercenaryLabel?.Label?.Children?.Count > 2 && mercenaryLabel.Label.Children[2] != null)
            {
                var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
                var optInButton = mercenaryLabel.Label.Children[2];
                var buttonCenter = optInButton.GetClientRectCache.Center;
                var finalPos = new Vector2(buttonCenter.X + windowOffset.X, buttonCenter.Y + windowOffset.Y);
                
                return finalPos;
            }
            return Vector2.Zero;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetMercenaryOptInButtonPosition failed: {ex.Message}");
            return Vector2.Zero;
        }
    }


    private Vector2 GetTpButton(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
            var elemCenter = (Vector2)leaderPartyElement?.TpButton?.GetClientRectCache.Center;
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
        
        yield return new WaitTime(30 + random.Next(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency));
    }

    private IEnumerator AutoPilotLogic()
    {
        while (true)
        {
            // =================================================================
            // SECTION 1: INITIAL CHECKS & STATE SETUP
            // =================================================================
            if (!AreWeThereYet.Instance.Settings.Enable.Value || !AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value || AreWeThereYet.Instance.localPlayer == null || !AreWeThereYet.Instance.localPlayer.IsAlive ||
                !AreWeThereYet.Instance.GameController.IsForeGroundCache || MenuWindow.IsOpened || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            {
                yield return new WaitTime(100);
                continue;
            }

            followTarget = GetFollowingTarget();
            var leaderPartyElement = GetLeaderPartyElement();

            // =================================================================
            // SECTION 2: TASK GENERATION LOGIC
            // =================================================================
            // This section decides WHAT to do (add breadcrumbs, find portals, etc.)

            // Case 1: Leader is in another zone. Find a portal or TP.
            if (followTarget == null && leaderPartyElement != null && !leaderPartyElement.ZoneName.Equals(AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName))
            {
                // This is your existing, correct logic for zone transitions.
                if (!_lastKnownLeaderZone.Equals(leaderPartyElement.ZoneName))
                {
                    // Leader zone changed - start buffer timer
                    _lastKnownLeaderZone = leaderPartyElement.ZoneName;
                    _leaderZoneChangeTime = DateTime.Now;

                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"Leader zone change detected: '{_lastKnownLeaderZone}' - starting reliability check");
                    }
                }

                // Use smarter zone detection to check if leader zone info is reliable
                if (IsLeaderZoneInfoReliable(leaderPartyElement))
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"Leader zone info reliable: '{leaderPartyElement.ZoneName}' - proceeding with portal/teleport logic");
                    }

                    var portal = GetBestPortalLabel(leaderPartyElement);
                    if (portal != null)
                    {
                        if (!tasks.Any(t => t.Type == TaskNodeType.Transition))
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Found reliable portal: {portal.ItemOnGround.Metadata}");
                            }
                            tasks.Add(new TaskNode(portal, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
                        }
                    }
                    else
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage("No suitable portal found - using teleport button fallback");
                        }

                        var tpConfirmation = GetTpConfirmation();
                        if (tpConfirmation != null)
                        {
                            yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center);
                            yield return new WaitTime(200);
                            yield return Mouse.LeftClick();
                            yield return new WaitTime(1000);
                        }

                        var tpButton = GetTpButton(leaderPartyElement);
                        if (!tpButton.Equals(Vector2.Zero))
                        {
                            yield return Mouse.SetCursorPosHuman(tpButton, false);
                            yield return new WaitTime(200);
                            yield return Mouse.LeftClick();
                            yield return new WaitTime(200);
                        }
                    }
                }
                else
                {
                    // Leader zone info not reliable yet, wait for it to stabilize
                    var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
                    var bufferTime = TimeSpan.FromMilliseconds(AreWeThereYet.Instance.Settings.AutoPilot.ZoneUpdateBuffer?.Value ?? 2000);
                    
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        var remaining = bufferTime - timeSinceChange;
                        AreWeThereYet.Instance.LogMessage($"Zone info not reliable yet - waiting {remaining.TotalMilliseconds:F0}ms more (Current: '{leaderPartyElement.ZoneName}')");
                    }
                    
                    yield return new WaitTime(200); // Wait a bit longer for zone info to stabilize
                }
            }
            // Case 2: Leader is in the same zone. Decide whether to build a path or stand still.
            else if (followTarget != null)
            {
                // Reset zone tracking since we found the leader in our area.
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;
                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);

                // ** THE CRITICAL FIX IS HERE **
                // If FAR from leader -> Add breadcrumbs to the path.
                if (distanceToLeader > AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value)
                {
                    // We are far away, so we add the leader's position as a breadcrumb to follow.
                    AddBreadcrumbTask(followTarget.Pos);
                }
                // If CLOSE to leader -> Clear the path and do nothing, restoring the desired behavior.
                else
                {
                    tasks.RemoveAll(t => t.Type == TaskNodeType.Movement || t.Type == TaskNodeType.Transition);
                    // The "CloseFollow" logic is now handled by the idle state, which is more robust.
                }

                // Your quest/mercenary logic remains correct.
                var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
                if (!isHideout)
                {
                    if (!tasks.Any(t => t.Type == TaskNodeType.Loot))
                    {
                        var questLoot = GetQuestItem();
                        if (questLoot != null && Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                                AreWeThereYet.Instance.LogMessage($"Adding quest loot task - Distance: {distance:F1}, Item: {questLoot.Metadata}");
                            }
                            tasks.Add(new TaskNode(questLoot.Pos, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value, TaskNodeType.Loot));
                        }
                        else if (questLoot != null && AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                            var hasLootTask = !tasks.Any(t => t.Type == TaskNodeType.Loot);
                            AreWeThereYet.Instance.LogMessage($"Quest loot NOT added - Distance: {distance:F1}, TooFar: {distance >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value}, HasLootTask: {hasLootTask}");
                        }
                    }
                    if (!tasks.Any(t => t.Type == TaskNodeType.MercenaryOptIn))
                    {
                        var mercenaryOptIn = GetMercenaryOptInButton();
                        if (mercenaryOptIn != null && Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Found mercenary OPT-IN button - adding to tasks");
                            }
                            tasks.Add(new TaskNode(mercenaryOptIn, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value, TaskNodeType.MercenaryOptIn));
                        }
                    }
                }
                if (followTarget?.Pos != null)
                    lastTargetPosition = followTarget.Pos;
            }

            // =================================================================
            // SECTION 3: TASK EXECUTION STATE MACHINE (with Dash logic restored)
            // =================================================================
            // This section executes whatever task is at the front of the queue.

            var movementTasks = tasks.Where(t => t.Type == TaskNodeType.Movement).ToList();
            var interactionTask = tasks.FirstOrDefault(t => t.Type != TaskNodeType.Movement);

            // --- STATE 1: Handle Interaction Tasks (Highest Priority) ---
            if (interactionTask != null)
            {
                if (isMoveKeyPressed)
                {
                    Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                    isMoveKeyPressed = false;
                    yield return new WaitTime(100);
                }
                const int TASK_TIMEOUT_SECONDS = 5;
                if ((DateTime.Now - interactionTask.CreationTime).TotalSeconds > TASK_TIMEOUT_SECONDS)
                {
                    tasks.Remove(interactionTask);
                    yield return null;
                    continue;
                }
                switch (interactionTask.Type)
                {
                    case TaskNodeType.Loot:
                        {
                            interactionTask.AttemptCount++;
                            var questLoot = GetQuestItem();
                            if (questLoot == null
                                || interactionTask.AttemptCount > 2
                                || Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) >=
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                tasks.RemoveAt(0);
                                yield return null;
                            }

                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency);
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
                            // Re-validate portal exists and is still valid before attempting to use it
                            if (interactionTask.LabelOnGround?.Label?.IsValid != true ||
                                interactionTask.LabelOnGround?.IsVisible != true ||
                                interactionTask.LabelOnGround?.ItemOnGround == null)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Portal became invalid - removing transition task, will re-evaluate in main loop");
                                }

                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }

                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(60);
                            yield return Mouse.SetCursorPosAndLeftClickHuman(new Vector2(interactionTask.LabelOnGround.Label.GetClientRect().Center.X, interactionTask.LabelOnGround.Label.GetClientRect().Center.Y), 100);
                            yield return new WaitTime(300);

                            interactionTask.AttemptCount++;
                            if (interactionTask.AttemptCount > 6)
                                tasks.RemoveAt(0);
                            {
                                yield return null;
                                continue;
                            }
                        }

                    case TaskNodeType.MercenaryOptIn:
                        {
                            interactionTask.AttemptCount++;
                            var mercenaryOptIn = GetMercenaryOptInButton();

                            // Remove task if button disappeared, too many attempts, or we're too far
                            if (mercenaryOptIn == null ||
                                interactionTask.AttemptCount > 3 ||
                                Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) >=
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    var reason = mercenaryOptIn == null ? "button disappeared" :
                                                interactionTask.AttemptCount > 3 ? "too many attempts" : "too far away";
                                    AreWeThereYet.Instance.LogMessage($"Removing mercenary OPT-IN task: {reason}");
                                }

                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }

                            // Stop movement and click the button
                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency);

                            var buttonPos = GetMercenaryOptInButtonPosition(mercenaryOptIn);
                            if (!buttonPos.Equals(Vector2.Zero))
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage($"Clicking mercenary OPT-IN button at {buttonPos}");
                                }

                                yield return Mouse.SetCursorPosHuman(buttonPos, false);
                                yield return new WaitTime(200);
                                yield return Mouse.LeftClick();
                                yield return new WaitTime(500); // Wait for button to process click
                                
                                // Remove task after clicking (button should disappear)
                                tasks.RemoveAt(0);
                            }
                            else
                            {
                                // Couldn't get button position, remove task
                                tasks.RemoveAt(0);
                            }
                            
                            break;
                        }
                }
            }
            // --- STATE 2: Handle Continuous Movement ---
            else if (movementTasks.Any())
            {
                // =====================================================================
                // TIER 1: DIRECT LEADER OVERRIDE (THE NEW LOGIC)
                // =====================================================================
                
                // Check if we can just go straight to the leader, ignoring the breadcrumbs.
                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);
                
                // Define a reasonable "direct follow" range (e.g., half the screen or ~400 units)
                const float DIRECT_FOLLOW_RANGE = 400f; 
                const int MAX_TASKS_FOR_OVERRIDE = 3; 
                
                if (movementTasks.Count < MAX_TASKS_FOR_OVERRIDE &&
                    distanceToLeader < DIRECT_FOLLOW_RANGE &&
                    LineOfSight.HasLineOfSight(AreWeThereYet.Instance.playerPosition.WorldToGrid().ToNumerics(), followTarget.Pos.WorldToGrid().ToNumerics()))
                {
                    // We can see the leader and they are close enough! Abandon the old path.
                    if (movementTasks.Count > 1) // Only log if we are actually skipping something
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"Leader is visible and close. Clearing {movementTasks.Count} breadcrumbs and moving directly.");
                        }
                        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                    }

                    // Add a single, direct task to the leader's position.
                    if (!tasks.Any(t => t.Type == TaskNodeType.Movement))
                    {
                        tasks.Add(new TaskNode(followTarget.Pos, 40, TaskNodeType.Movement));
                    }
                }
                
                // Re-fetch the task list in case we just cleared it.
                movementTasks = tasks.Where(t => t.Type == TaskNodeType.Movement).ToList();
                if (!movementTasks.Any())
                {
                    // If we cleared the tasks, we need to stop moving for this frame and re-evaluate.
                    if (isMoveKeyPressed)
                    {
                        Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        isMoveKeyPressed = false;
                    }
                    yield return new WaitTime(50);
                    continue;
                }

                // =====================================================================
                // TIER 2 & 3: SMART TRAIN / STRICT FOLLOWING
                // =====================================================================
                
                // If the direct override didn't trigger, proceed with path following.
                var targetWaypoint = FindNextWaypoint(movementTasks, AreWeThereYet.Instance.playerPosition);

                // If there's nowhere to go, stop moving.
                if (targetWaypoint == null)
                {
                    if (isMoveKeyPressed)
                    {
                        Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        isMoveKeyPressed = false;
                    }
                    yield return new WaitTime(100);
                    continue;
                }

                // Check if we should dash to this specific waypoint.
                bool canDash = AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled.Value &&
                            ShouldUseDash(targetWaypoint.WorldPosition.WorldToGrid());

                if (canDash)
                {
                    // DASHING: Release the move key and perform a dash.
                    if (isMoveKeyPressed)
                    {
                        Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        isMoveKeyPressed = false;
                        yield return new WaitTime(50); // Brief pause to ensure key release is registered.
                    }

                    // Aim and press the dash key.
                    yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(targetWaypoint.WorldPosition));
                    Keyboard.KeyPress(AreWeThereYet.Instance.Settings.AutoPilot.DashKey);
                    yield return new WaitTime(250); // Wait for dash cooldown/animation before re-evaluating.
                }
                else // NOT DASHING: Perform normal continuous movement.
                {
                    // Press and hold the move key if it's not already held.
                    if (!isMoveKeyPressed)
                    {
                        Keyboard.KeyDown(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        isMoveKeyPressed = true;
                    }
                    // STEER: Continuously aim the mouse at the target waypoint.
                    yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(targetWaypoint.WorldPosition));
                }

                // CLEANUP
                var firstNode = movementTasks.First();
                var distanceToFirstNode = Vector3.Distance(AreWeThereYet.Instance.playerPosition, firstNode.WorldPosition);
                if (distanceToFirstNode <= 40f) // Completion radius
                {
                    tasks.Remove(firstNode);
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"Consumed breadcrumb. {tasks.Count(t => t.Type == TaskNodeType.Movement)} remaining.");
                    }
                }
            }
            
            // --- STATE 3: IDLE (No tasks) ---
            else
            {
                if (isMoveKeyPressed)
                {
                    Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                    isMoveKeyPressed = false;
                }
            }

            lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
            yield return new WaitTime(50);
        }
    }

    private bool ShouldUseDash(Vector2 targetPosition)
    {
        try
        {
            // Comprehensive null checks
            if (LineOfSight == null)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage("ShouldUseDash: LineOfSight is null");
                return false;
            }
            
            if (AreWeThereYet.Instance?.GameController?.Player?.GridPos == null)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage("ShouldUseDash: Player GridPos is null");
                return false;
            }
            
            if (AreWeThereYet.Instance?.Settings?.AutoPilot?.DashEnabled?.Value != true)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage("ShouldUseDash: Dash not enabled in settings");
                return false;
            }
            
            var playerPos = AreWeThereYet.Instance.GameController.Player.GridPos;
            var distance = Vector2.Distance(playerPos, targetPosition);
            
            var minDistance = AreWeThereYet.Instance.Settings.AutoPilot.Dash.DashMinDistance.Value;
            var maxDistance = AreWeThereYet.Instance.Settings.AutoPilot.Dash.DashMaxDistance.Value;
            
            if (distance < minDistance || distance > maxDistance)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage($"ShouldUseDash: Distance {distance:F1} outside dash range ({minDistance}-{maxDistance})");
                return false;
            }
            
            // Convert SharpDX.Vector2 to System.Numerics.Vector2 for HasLineOfSight
            var playerPosNumerics = new System.Numerics.Vector2(playerPos.X, playerPos.Y);
            var targetPosNumerics = new System.Numerics.Vector2(targetPosition.X, targetPosition.Y);
            
            // NEW: Only dash if this specific path segment is blocked
            if (LineOfSight._terrainData == null)
            {
                return distance > 100; // Conservative fallback
            }
            
            // Check if THIS path segment is blocked (not the entire line to leader)
            var hasLineOfSight = LineOfSight.HasLineOfSight(playerPosNumerics, targetPosNumerics);
            var shouldDash = !hasLineOfSight && distance >= minDistance;
            
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"ShouldUseDash: RESULT = {shouldDash} (distance: {distance:F1}, hasLineOfSight: {hasLineOfSight}, pathSegment: true)");
            }
            
            return shouldDash;
            }
        catch (Exception ex)
        {
            // Log the error but DON'T let it bubble up to crash the coroutine
            AreWeThereYet.Instance.LogError($"ShouldUseDash failed: {ex.Message}");
            return false; // Safe fallback - don't dash if terrain check fails
        }
    }

    private Entity GetFollowingTarget()
    {
        try
        {
            string leaderName = AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value.ToLower();
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
            var questItemLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid && 
                        x.Label.IsVisible && x.ItemOnGround != null && 
                        x.ItemOnGround.Type == EntityType.WorldItem && 
                        x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>())
                .Where(x =>
                {
                    try
                    {
                        var itemEntity = x.ItemOnGround.GetComponent<WorldItem>().ItemEntity;
                        return AreWeThereYet.Instance.GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName == "QuestItem";
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.Pos))
                .ToList();
                
            // Return the Entity from the closest quest item label
            return questItemLabels?.FirstOrDefault()?.ItemOnGround;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetQuestItem failed: {ex.Message}");
            return null;
        }
    }

    public void Render()
    {
        if (AreWeThereYet.Instance.Settings.AutoPilot.ToggleKey.PressedOnce())
        {
            AreWeThereYet.Instance.Settings.AutoPilot.Enabled.SetValueNoEvent(!AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value);
            tasks = new List<TaskNode>();
            
            // Failsafe: If we were moving, release the key when disabling.
            if (isMoveKeyPressed)
            {
                Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                isMoveKeyPressed = false;
            }
        }
        
        if (!AreWeThereYet.Instance.Settings.AutoPilot.Enabled || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
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
                AreWeThereYet.Instance.Graphics.DrawLine(portal.Label.GetClientRectCache.TopLeft, portal.Label.GetClientRectCache.TopRight, 2f, Color.Firebrick);
            }
        }
        catch (Exception)
        {
        }
        
        // Quest Item rendering
        try
        {
            var questItemLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null && x.ItemOnGround.Type == EntityType.WorldItem &&
                    x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>()).Where(x =>
                {
                    try
                    {
                        var itemEntity = x.ItemOnGround.GetComponent<WorldItem>().ItemEntity;
                        return AreWeThereYet.Instance.GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName == "QuestItem";
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();
                
            foreach (var questItem in questItemLabels)
            {
                AreWeThereYet.Instance.Graphics.DrawLine(questItem.Label.GetClientRectCache.TopLeft, questItem.Label.GetClientRectCache.TopRight, 4f, Color.Lime);
            }
        }
        catch (Exception)
        {
        }
        
        // Mercenary OPT-IN button rendering (simple version)
        try
        {
            var mercenaryLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null &&
                    x.ItemOnGround.Metadata.ToLower().Contains("mercenary") &&
                    x.Label.Children?.Count > 2 && x.Label.Children[2] != null &&
                    x.Label.Children[2].IsVisible).ToList();
                    
            foreach (var mercenary in mercenaryLabels)
            {
                var optInButton = mercenary.Label.Children[2];
                AreWeThereYet.Instance.Graphics.DrawLine(optInButton.GetClientRectCache.TopLeft, optInButton.GetClientRectCache.TopRight, 3f, Color.Cyan);
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
            
            var lineWidth = (float)AreWeThereYet.Instance.Settings.AutoPilot.Visual.TaskLineWidth.Value;
            var lineColor = AreWeThereYet.Instance.Settings.AutoPilot.Visual.TaskLineColor.Value;
            if (cachedTasks?.Count > 0)
            {
                var taskTypeName = cachedTasks[0].Type == TaskNodeType.MercenaryOptIn ? "Mercenary OPT-IN" : cachedTasks[0].Type.ToString();
                AreWeThereYet.Instance.Graphics.DrawText(
                    "Current Task: " + taskTypeName,
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
                    $"Follow Enabled: {AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value}", new System.Numerics.Vector2(500, 120));
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
        AreWeThereYet.Instance.Graphics.DrawText("Leader: " + "[ " + AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value + " ] " + (followTarget != null ? "Found" : "Null"), new System.Numerics.Vector2(500, 160));
        AreWeThereYet.Instance.Graphics.DrawLine(new System.Numerics.Vector2(490, 110), new System.Numerics.Vector2(490, 210), 1, Color.White);
    }
}
