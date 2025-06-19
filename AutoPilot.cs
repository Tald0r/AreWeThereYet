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
using ExileCore.Shared.Helpers;
using AreWeThereYet.Utils;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Color = SharpDX.Color;

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

    private Entity _lastKnownLeaderPortal = null;
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
            const float LEADER_BREADCRUMB_DISTANCE = 50f;
            var lastBreadcrumbPos = tasks.LastOrDefault(t => t.Type == TaskNodeType.Movement)?.WorldPosition ?? lastPlayerPosition;

            if (Vector3.Distance(leaderPos, lastBreadcrumbPos) >= LEADER_BREADCRUMB_DISTANCE)
            {
                // The modern WorldToGrid() directly converts SharpDX.Vector3 to System.Numerics.Vector2
                System.Numerics.Vector2 leaderGridPos = leaderPos.WorldToGrid();

                if (TryFindNearestWalkablePosition(leaderGridPos, out var walkableGridPos))
                {
                    // The modern GridToWorld() directly converts System.Numerics.Vector2 to System.Numerics.Vector3
                    Vector3 walkableWorldPos = walkableGridPos.GridToWorld(leaderPos.Z);

                    tasks.Add(new TaskNode(walkableWorldPos, 40, TaskNodeType.Movement));
                }
            }
        }
        catch (Exception ex) { AreWeThereYet.Instance.LogError($"AddBreadcrumbTask failed: {ex.Message}"); }
    }


    private bool TryFindNearestWalkablePosition(System.Numerics.Vector2 originalGridPos, out System.Numerics.Vector2 walkableGridPos)
    {
        walkableGridPos = originalGridPos;

        // Check if the original position is already walkable.
        if (LineOfSight.IsTerrainPassable(originalGridPos))
        {
            return true;
        }

        // If not, search in an expanding spiral for the nearest walkable tile.
        for (int radius = 1; radius <= 15; radius++) // Search up to a 15-tile radius
        {
            for (int i = -radius; i <= radius; i++)
            {
                for (int j = -radius; j <= radius; j++)
                {
                    // Only check the shell of the spiral
                    if (Math.Abs(i) != radius && Math.Abs(j) != radius) continue;

                    System.Numerics.Vector2 checkPos = new System.Numerics.Vector2(originalGridPos.X + i, originalGridPos.Y + j);
                    if (LineOfSight.IsTerrainPassable(checkPos))
                    {
                        walkableGridPos = checkPos;
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
                        {
                            AreWeThereYet.Instance.LogMessage($"[AddBreadcrumb] Leader pos {originalGridPos} was invalid. Snapped to nearest walkable tile {walkableGridPos}.", 5, Color.OrangeRed);
                        }
                        return true;
                    }
                }
            }
        }

        // If no walkable tile is found within the search radius, fail.
        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
        {
            AreWeThereYet.Instance.LogMessage($"[AddBreadcrumb] Could not find any walkable tile near {originalGridPos}. Aborting task.", 5, Color.Red);
        }
        return false;
    }

    private TaskNode FindNextWaypoint(List<TaskNode> path, Vector3 playerPos)
    {
        if (path == null || !path.Any())
        {
            return null;
        }

        var playerGridPos = playerPos.WorldToGrid();

        // Look ahead for a valid, visible shortcut.
        const int lookAheadLimit = 5;
        for (int i = Math.Min(path.Count - 1, lookAheadLimit); i > 0; i--)
        {
            var potentialShortcutNode = path[i];

            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
            {
                AreWeThereYet.Instance.LogMessage($"[FindNext] Checking shortcut to node {i} at {potentialShortcutNode.WorldPosition}", 5, Color.Gray);
            }

            if (LineOfSight.HasLineOfSight(playerGridPos, potentialShortcutNode.WorldPosition.WorldToGrid()))
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
                {
                    AreWeThereYet.Instance.LogMessage($"[FindNext] -> SUCCESS! Can see node {i}. Taking shortcut.", 5, Color.LimeGreen);
                }
                return potentialShortcutNode;
            }
        }

        // If no shortcuts are visible, our best target is the very next point on the path.
        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value && path.Any())
        {
            AreWeThereYet.Instance.LogMessage($"[FindNext] -> No shortcuts found. Defaulting to next node at {path.First().WorldPosition}", 5, Color.Orange);
        }
        return path.First();
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
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.PosNum))
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
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.PosNum))
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
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.PosNum))
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
            var center = leaderPartyElement?.TpButton?.GetClientRectCache.Center;
            if (center == null)
                return Vector2.Zero;
            var elemCenter = new Vector2(center.Value.X, center.Value.Y);
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
            // SECTION 1: INITIAL CHECKS & UI CLEANUP
            // =================================================================
            if (!AreWeThereYet.Instance.Settings.Enable.Value || !AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value || AreWeThereYet.Instance.localPlayer == null || !AreWeThereYet.Instance.localPlayer.IsAlive ||
                !AreWeThereYet.Instance.GameController.IsForeGroundCache || MenuWindow.IsOpened || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            {
                yield return new WaitTime(100);
                continue;
            }
            var ingameUi = AreWeThereYet.Instance.GameController.IngameState.IngameUi;
            if (new List<Element> { ingameUi.TreePanel, ingameUi.AtlasTreePanel, ingameUi.OpenLeftPanel, ingameUi.OpenRightPanel, ingameUi.InventoryPanel, ingameUi.SettingsPanel, ingameUi.ChatPanel.Children.FirstOrDefault() }.Any(panel => panel != null && panel.IsVisible))
            {
                Keyboard.KeyPress(Keys.Escape);
                yield return new WaitTime(150);
                continue;
            }

            followTarget = GetFollowingTarget();
            var leaderPartyElement = GetLeaderPartyElement();

            // =================================================================
            // SECTION 2: TASK GENERATION LOGIC (with Portal Memory)
            // =================================================================
            // This section decides WHAT to do (add breadcrumbs, find portals, etc.)

            // --- NEW CASE 1.5: We have a portal memory and the leader has just zoned. Follow them! ---
            if (followTarget == null && _lastKnownLeaderPortal != null && _lastKnownLeaderPortal.IsValid)
            {
                if (!tasks.Any(t => t.Type == TaskNodeType.Transition))
                {
                    var portalLabel = AreWeThereYet.Instance.GameController.IngameState.IngameUi.ItemsOnGroundLabels
                                        .FirstOrDefault(x => x.ItemOnGround.Id == _lastKnownLeaderPortal.Id);
                    if (portalLabel != null)
                    {
                        // Insert at the front to make it the absolute highest priority.
                        tasks.Insert(0, new TaskNode(portalLabel, 50, TaskNodeType.Transition));
                    }
                }
                // Use the memory once, then clear it to prevent getting stuck.
                _lastKnownLeaderPortal = null;
            }
            // Case 1: Leader is in a different zone (standard TP/portal logic).
            else if (followTarget == null && leaderPartyElement != null && !leaderPartyElement.ZoneName.Equals(AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName))
            {
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
                            yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center.ToNumerics());
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
            // Case 2: Leader is in the same zone.
            else if (followTarget != null)
            {
                // --- NEW LOGIC: Record when the leader targets a portal ---
                var leaderActor = followTarget.GetComponent<Actor>();
                if (leaderActor?.CurrentAction?.Target is { } target && (target.Type is EntityType.AreaTransition or EntityType.Portal or EntityType.TownPortal))
                {
                    _lastKnownLeaderPortal = target;
                }

                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.PosNum);
                if (distanceToLeader > AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value)
                {
                    // We are far away, so we add the leader's position as a breadcrumb to follow.
                    AddBreadcrumbTask(followTarget.PosNum);
                }
                // If CLOSE to leader -> Clear the path and do nothing, restoring the desired behavior.
                else
                {
                    tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                }
                var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
                if (!isHideout)
                {
                    if (!tasks.Any(t => t.Type == TaskNodeType.Loot))
                    {
                        var questLoot = GetQuestItem();
                        if (questLoot != null && Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.PosNum) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.PosNum);
                                AreWeThereYet.Instance.LogMessage($"Adding quest loot task - Distance: {distance:F1}, Item: {questLoot.Metadata}");
                            }
                            tasks.Add(new TaskNode(questLoot.PosNum, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value, TaskNodeType.Loot));
                        }
                        else if (questLoot != null && AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.PosNum);
                            var hasLootTask = !tasks.Any(t => t.Type == TaskNodeType.Loot);
                            AreWeThereYet.Instance.LogMessage($"Quest loot NOT added - Distance: {distance:F1}, TooFar: {distance >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value}, HasLootTask: {hasLootTask}");
                        }
                    }
                    if (!tasks.Any(t => t.Type == TaskNodeType.MercenaryOptIn))
                    {
                        var mercenaryOptIn = GetMercenaryOptInButton();
                        if (mercenaryOptIn != null && Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.PosNum) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Found mercenary OPT-IN button - adding to tasks");
                            }
                            tasks.Add(new TaskNode(mercenaryOptIn, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value, TaskNodeType.MercenaryOptIn));
                        }
                    }
                }
                if (followTarget?.PosNum != null)
                    lastTargetPosition = followTarget.PosNum;
            }

            // =================================================================
            // SECTION 3: TASK EXECUTION STATE MACHINE
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
                                || Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.PosNum) >=
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
                                Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.PosNum) >=
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
                // 1. QUERY: Ask our pure function for the best target.
                var targetWaypoint = FindNextWaypoint(movementTasks, AreWeThereYet.Instance.playerPosition);

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

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
                {
                    AreWeThereYet.Instance.LogMessage($"[Movement] Target chosen: {targetWaypoint.WorldPosition.WorldToGrid()}. Path has {movementTasks.Count} nodes.", 5, Color.Yellow);
                }
    
                // 2. DECIDE: Check if we should dash to that target.
                bool canDash = AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled.Value &&
                               ShouldUseDash(targetWaypoint.WorldPosition.WorldToGrid());

                // 3. ACT: Perform the dash or move action.
                if (canDash)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
                    {
                        AreWeThereYet.Instance.LogMessage($"[Movement] -> ACTION: Dashing.", 5, Color.Cyan);
                    }
        
                    // DASHING: Release the move key and perform a dash.
                    if (isMoveKeyPressed)
                    {
                        Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        isMoveKeyPressed = false;
                        yield return new WaitTime(50);
                    }

                    // Aim and press the dash key.
                    yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(targetWaypoint.WorldPosition));
                    Keyboard.KeyPress(AreWeThereYet.Instance.Settings.AutoPilot.DashKey);
                    yield return new WaitTime(250); // Wait for dash cooldown/animation before re-evaluating.
                }
                else // NOT DASHING: Perform normal continuous movement.
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
                    {
                        AreWeThereYet.Instance.LogMessage($"[Movement] -> ACTION: Moving.", 5, Color.White);
                    }
        
                    // Press and hold the move key if it's not already held.
                    if (!isMoveKeyPressed)
                    {
                        Keyboard.KeyDown(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        isMoveKeyPressed = true;
                    }
                    // STEER: Continuously aim the mouse at the target waypoint.
                    yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(targetWaypoint.WorldPosition));
                }

                // 4. CLEANUP: Now that the action is done, the main loop cleans up the task list.
                int targetIndex = movementTasks.IndexOf(targetWaypoint);
                if (targetIndex > 0)
                {
                    // If we took a shortcut, remove the skipped breadcrumbs.
                    for (int i = 0; i < targetIndex; i++)
                    {
                        tasks.Remove(movementTasks[i]);
                    }
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
                    {
                        AreWeThereYet.Instance.LogMessage($"[Cleanup] Took shortcut, consumed {targetIndex} breadcrumbs.", 5, Color.LimeGreen);
                    }
                }

                // Always check if we have reached the current first node.
                var firstNode = tasks.FirstOrDefault(t => t.Type == TaskNodeType.Movement);
                if (firstNode != null && Vector3.Distance(AreWeThereYet.Instance.playerPosition, firstNode.WorldPosition) <= 40f)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug.Value)
                    {
                        AreWeThereYet.Instance.LogMessage($"[Cleanup] Reached node {firstNode.WorldPosition.WorldToGrid()}. Consuming.", 5, Color.Goldenrod);
                    }
                    tasks.Remove(firstNode);
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
            
            if (AreWeThereYet.Instance?.GameController?.Player?.GridPosNum == null)
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
            
            var playerPos = AreWeThereYet.Instance.GameController.Player.GridPosNum;
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
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.PosNum))
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
                AreWeThereYet.Instance.Graphics.DrawLine(portal.Label.GetClientRectCache.TopLeft.ToNumerics(), portal.Label.GetClientRectCache.TopRight.ToNumerics(), 2f, Color.Firebrick);
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
                AreWeThereYet.Instance.Graphics.DrawLine(questItem.Label.GetClientRectCache.TopLeft.ToNumerics(), questItem.Label.GetClientRectCache.TopRight.ToNumerics(), 4f, Color.Lime);
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
                AreWeThereYet.Instance.Graphics.DrawLine(optInButton.GetClientRectCache.TopLeft.ToNumerics(), optInButton.GetClientRectCache.TopRight.ToNumerics(), 3f, Color.Cyan);
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
