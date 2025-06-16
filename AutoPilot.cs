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

    private string _lastKnownLeaderZone = "";
    private DateTime _leaderZoneChangeTime = DateTime.MinValue;

    //Leader path tracking
    private readonly List<Vector3> _leaderPathHistory = new List<Vector3>();
    private Vector3 _lastRecordedLeaderPos = Vector3.Zero;
    private DateTime _lastLeaderPosUpdate = DateTime.MinValue;
    private const float LEADER_BREADCRUMB_DISTANCE = 30f; // Record breadcrumb every 30 units
    private const int MAX_BREADCRUMBS = 50; // Keep last 50 breadcrumbs

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

    /// <summary>
    /// Record leader's movement path for us to follow
    /// </summary>
    private void RecordLeaderPath(Vector3 leaderPos)
    {
        try
        {
            // Only record new breadcrumb if leader moved enough distance
            var distanceMoved = Vector3.Distance(_lastRecordedLeaderPos, leaderPos);
            
            if (distanceMoved >= LEADER_BREADCRUMB_DISTANCE || _lastRecordedLeaderPos == Vector3.Zero)
            {
                _leaderPathHistory.Add(leaderPos);
                _lastRecordedLeaderPos = leaderPos;
                _lastLeaderPosUpdate = DateTime.Now;
                
                // ADDED: Clean up completed breadcrumbs regularly
                CleanupCompletedBreadcrumbs();
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Recorded leader breadcrumb #{_leaderPathHistory.Count} at distance {distanceMoved:F1}");
                }
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"RecordLeaderPath failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Create movement tasks following leader's actual path
    /// </summary>
    private void CreatePathFollowingTasks()
    {
        try
        {
            // Clear existing movement tasks
            for (var i = tasks.Count - 1; i >= 0; i--)
            {
                if (tasks[i].Type == TaskNodeType.Movement)
                    tasks.RemoveAt(i);
            }
            
            // If no breadcrumbs available, create direct task as fallback
            if (_leaderPathHistory.Count == 0)
            {
                if (followTarget != null)
                {
                    tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance, TaskNodeType.Movement));
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage("No breadcrumbs available - created direct task as fallback");
                    }
                }
                return;
            }
            
            var playerPos = AreWeThereYet.Instance.playerPosition;
            
            // FIXED: Find the NEXT breadcrumb in sequence, not just "unreached" ones
            var nextBreadcrumbIndex = FindNextSequentialBreadcrumb(playerPos);
            
            if (nextBreadcrumbIndex == -1)
            {
                // All breadcrumbs completed - go to leader's current position
                if (followTarget != null)
                {
                    tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance, TaskNodeType.Movement));
                    
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage("All breadcrumbs completed - going to leader's current position");
                    }
                }
                return;
            }
            
            // FIXED: Create tasks for NEXT sequential breadcrumbs only (follow exact path)
            var tasksToCreate = Math.Min(2, _leaderPathHistory.Count - nextBreadcrumbIndex); // Only next 1-2 breadcrumbs
            
            for (var i = 0; i < tasksToCreate; i++)
            {
                var breadcrumbIndex = nextBreadcrumbIndex + i;
                var breadcrumb = _leaderPathHistory[breadcrumbIndex];
                
                tasks.Add(new TaskNode(breadcrumb, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance, TaskNodeType.Movement));
            }
            
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"Created {tasksToCreate} sequential path tasks starting from breadcrumb #{nextBreadcrumbIndex + 1}");
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"CreatePathFollowingTasks failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Find the next breadcrumb in sequence that the bot needs to visit
    /// </summary>
    private int FindNextSequentialBreadcrumb(Vector3 playerPos)
    {
        try
        {
            var completionDistance = AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value * 0.5f; // Smaller threshold for precise following
            
            // Find the first breadcrumb that hasn't been reached yet
            for (var i = 0; i < _leaderPathHistory.Count; i++)
            {
                var distanceToBreadcrumb = Vector3.Distance(playerPos, _leaderPathHistory[i]);
                
                if (distanceToBreadcrumb > completionDistance)
                {
                    // This breadcrumb hasn't been reached yet - this is our next target
                    return i;
                }
            }
            
            // All breadcrumbs have been reached
            return -1;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"FindNextSequentialBreadcrumb failed: {ex.Message}");
            return 0; // Fallback to first breadcrumb
        }
    }
    
    /// <summary>
    /// Clean up completed breadcrumbs to prevent list from growing too large
    /// </summary>
    private void CleanupCompletedBreadcrumbs()
    {
        try
        {
            var playerPos = AreWeThereYet.Instance.playerPosition;
            var completionDistance = AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value * 0.5f;
            
            // Remove breadcrumbs from the START of the list that have been completed
            var completedCount = 0;
            for (var i = 0; i < _leaderPathHistory.Count; i++)
            {
                var distanceToBreadcrumb = Vector3.Distance(playerPos, _leaderPathHistory[i]);
                
                if (distanceToBreadcrumb <= completionDistance)
                {
                    completedCount++;
                }
                else
                {
                    // Found first uncompleted breadcrumb - stop counting
                    break;
                }
            }
            
            // Remove completed breadcrumbs from the front of the list
            if (completedCount > 0)
            {
                _leaderPathHistory.RemoveRange(0, completedCount);
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Cleaned up {completedCount} completed breadcrumbs - {_leaderPathHistory.Count} remaining");
                }
            }
            
            // Also limit total breadcrumbs to prevent memory issues
            if (_leaderPathHistory.Count > MAX_BREADCRUMBS)
            {
                var removeCount = _leaderPathHistory.Count - MAX_BREADCRUMBS;
                _leaderPathHistory.RemoveRange(0, removeCount);
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Removed {removeCount} old breadcrumbs to stay within limit");
                }
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"CleanupCompletedBreadcrumbs failed: {ex.Message}");
        }
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
            if (!AreWeThereYet.Instance.Settings.Enable.Value || !AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value || AreWeThereYet.Instance.localPlayer == null || !AreWeThereYet.Instance.localPlayer.IsAlive || 
                !AreWeThereYet.Instance.GameController.IsForeGroundCache || MenuWindow.IsOpened || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            {
                yield return new WaitTime(100);
                continue;
            }
            
            followTarget = GetFollowingTarget();
            var leaderPartyElement = GetLeaderPartyElement();
            
            if (followTarget == null && !leaderPartyElement.ZoneName.Equals(AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName))
            {
                // Track zone changes for buffer timing
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
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"Found reliable portal: {portal.ItemOnGround.Metadata}");
                        }
                        tasks.Add(new TaskNode(portal, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
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
            else if (followTarget != null)
            {
                // Reset zone tracking when leader is found
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;
                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);
                
                if (distanceToLeader >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                {
                    var distanceMoved = Vector3.Distance(lastTargetPosition, followTarget.Pos);
                    if (lastTargetPosition != Vector3.Zero && distanceMoved > AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                    {
                        var transition = GetBestPortalLabel(leaderPartyElement);
                        if (transition != null && transition.ItemOnGround.DistancePlayer < 80)
                            tasks.Add(new TaskNode(transition, 200, TaskNodeType.Transition));
                    }
                }
                else
                {
                    // FIXED: Always use path following for normal distances
                    if (distanceToLeader > AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value)
                    {
                        // Record leader's current position for path tracking
                        RecordLeaderPath(followTarget.Pos);
                        
                        // Create tasks following leader's actual path instead of direct line
                        if (tasks.Count == 0 || tasks.Count(t => t.Type == TaskNodeType.Movement) < 3)
                        {
                            CreatePathFollowingTasks();
                        }
                    }
                    else
                    {
                        // Close range - clear movement tasks, only use direct positioning for very close distances
                        for (var i = tasks.Count - 1; i >= 0; i--)
                            if (tasks[i].Type == TaskNodeType.Movement || tasks[i].Type == TaskNodeType.Transition)
                                tasks.RemoveAt(i);
                        yield return null;
                        
                        // Only create direct-line task for very close positioning (< KeepWithinDistance)
                        if (AreWeThereYet.Instance.Settings.AutoPilot.CloseFollow.Value &&
                            distanceToLeader >= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value * 0.8f)
                        {
                            tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance));
                        }
                    }
                    
                    var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
                    if (!isHideout)
                    {
                        var questLoot = GetQuestItem();
                        if (questLoot != null &&
                            Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value &&
                            tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) == null)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                                AreWeThereYet.Instance.LogMessage($"Adding quest loot task - Distance: {distance:F1}, Item: {questLoot.Metadata}");
                            }
                            tasks.Add(new TaskNode(questLoot.Pos, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance, TaskNodeType.Loot));
                        }
                        else if (questLoot != null && AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                            var hasLootTask = tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) != null;
                            AreWeThereYet.Instance.LogMessage($"Quest loot NOT added - Distance: {distance:F1}, TooFar: {distance >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value}, HasLootTask: {hasLootTask}");
                        }
                        
                        var mercenaryOptIn = GetMercenaryOptInButton();
                        if (mercenaryOptIn != null &&
                            Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value &&
                            tasks.FirstOrDefault(I => I.Type == TaskNodeType.MercenaryOptIn) == null)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Found mercenary OPT-IN button - adding to tasks");
                            }
                            tasks.Add(new TaskNode(mercenaryOptIn, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance, TaskNodeType.MercenaryOptIn));
                        }
                    }
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
                    playerDistanceMoved >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                {
                    tasks.RemoveAt(0);
                    lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
                    yield return null;
                    continue;
                }
                switch (currentTask.Type)
                {
                    case TaskNodeType.Movement:
                        if (AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled.Value &&
                        ShouldUseDash(currentTask.WorldPosition.WorldToGrid()))
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyPress(AreWeThereYet.Instance.Settings.AutoPilot.DashKey);
                            yield return new WaitTime(random.Next(25) + 30);
                        }
                        else
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Input.KeyDown(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(random.Next(25) + 30);
                            Input.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        }
                        
                        if (taskDistance <= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value * 1.5)
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
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                tasks.RemoveAt(0);
                                yield return null;
                            }
                            
                            Input.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
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
                            if (currentTask.LabelOnGround?.Label?.IsValid != true ||
                                currentTask.LabelOnGround?.IsVisible != true ||
                                currentTask.LabelOnGround?.ItemOnGround == null)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Portal became invalid - removing transition task, will re-evaluate in main loop");
                                }
                                
                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }
                            
                            Input.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
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
                        
                    case TaskNodeType.MercenaryOptIn:
                        {
                            currentTask.AttemptCount++;
                            var mercenaryOptIn = GetMercenaryOptInButton();
                            
                            // Remove task if button disappeared, too many attempts, or we're too far
                            if (mercenaryOptIn == null ||
                                currentTask.AttemptCount > 3 ||
                                Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) >=
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    var reason = mercenaryOptIn == null ? "button disappeared" :
                                                currentTask.AttemptCount > 3 ? "too many attempts" : "too far away";
                                    AreWeThereYet.Instance.LogMessage($"Removing mercenary OPT-IN task: {reason}");
                                }
                                
                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }
                            
                            // Stop movement and click the button
                            Input.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
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
