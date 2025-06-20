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
using System.Windows.Forms;

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

    private Entity _lastKnownLeaderPortal = null;
    private string _lastKnownLeaderZone = "";
    private DateTime _leaderLastSeen  = DateTime.MinValue;
    private DateTime _leaderZoneChangeTime = DateTime.MinValue;
    private bool _isTransitioning = false;

    private void ResetPathing()
    {
        tasks = new List<TaskNode>();
        followTarget = null;
        lastTargetPosition = Vector3.Zero;
        lastPlayerPosition = Vector3.Zero;
    }

    public void AreaChange()
    {
        // If we triggered this area change ourselves...
        if (_isTransitioning)
        {
            // ...start a new coroutine to handle the post-transition grace period.
            var gracePeriodCoroutine = new Coroutine(PostTransitionGracePeriod(), AreWeThereYet.Instance, "PostTransitionGracePeriod");
            Core.ParallelRunner.Run(gracePeriodCoroutine);
        }
    
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

    private IEnumerator PostTransitionGracePeriod()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int TIMEOUT_MS = 10000; // 10-second timeout.

        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
        {
            AreWeThereYet.Instance.LogMessage("[GracePeriod] Entered post-transition grace period. Waiting for leader entity to sync...");
        }

        while (stopwatch.ElapsedMilliseconds < TIMEOUT_MS)
        {
            var leaderPartyElement = GetLeaderPartyElement();
            var followTarget = GetFollowingTarget();
            var currentAreaName = AreWeThereYet.Instance.GameController.Area.CurrentArea.DisplayName;

            // Success Condition: The leader's entity is found and they are in the same zone as us.
            if (leaderPartyElement != null && followTarget != null && leaderPartyElement.ZoneName.Equals(currentAreaName))
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"[GracePeriod] SUCCESS: Leader entity found and synced in '{currentAreaName}'. Resuming normal logic.");
                }
                stopwatch.Stop();
                _isTransitioning = false; // Unlock the main logic.
                yield break;              // Exit the coroutine.
            }

            yield return new WaitTime(100);
        }

        // If we reach here, the loop timed out. Now we must determine why.
        var finalLeaderPartyElement = GetLeaderPartyElement();
        var finalCurrentAreaName = AreWeThereYet.Instance.GameController.Area.CurrentArea.DisplayName;

        // --- THE NEW FAILSAFE LOGIC ---
        // Check for the "Same Zone, Different Instance" problem.
        if (finalLeaderPartyElement != null && finalLeaderPartyElement.ZoneName.Equals(finalCurrentAreaName))
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GracePeriod] DEADLOCK DETECTED: In same zone ('{finalCurrentAreaName}') but different instance. Forcing UI teleport to sync instances.", 10, Color.Red);
            }

            // Check for and click the "Are you sure?" confirmation box if it's open.
            var tpConfirmation = GetTpConfirmation();
            if (tpConfirmation != null)
            {
                yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center);
                yield return new WaitTime(200);
                yield return Mouse.LeftClick();
                yield return new WaitTime(1000);
            }

            // Click the teleport button on the party UI to force an instance sync.
            var tpButton = GetTpButton(finalLeaderPartyElement);
            if (!tpButton.Equals(Vector2.Zero))
            {
                yield return Mouse.SetCursorPosHuman(tpButton, false);
                yield return new WaitTime(200);
                yield return Mouse.LeftClick();
                yield return new WaitTime(200);
            }
        }
        else
        {
            // The timeout was for a different reason (e.g., leader zoned again). Let the main logic handle it.
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage("[GracePeriod] TIMEOUT: Leader entity did not sync. Resuming logic with fallback.", 5, Color.Orange);
            }
        }

        _isTransitioning = false; // Unlock the main logic in all timeout cases.
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
            
            // var ingameUi = AreWeThereYet.Instance.GameController.IngameState.IngameUi;
            
            // if (new List<Element> { ingameUi.TreePanel, ingameUi.AtlasTreePanel, ingameUi.OpenLeftPanel, ingameUi.OpenRightPanel, ingameUi.InventoryPanel, ingameUi.SettingsPanel, ingameUi.ChatPanel.Children.FirstOrDefault() }.Any(panel => panel != null && panel.IsVisible))
            // {
            //     Keyboard.KeyPress(Keys.Escape);
            //     yield return new WaitTime(150);
            //     continue;
            // }

            followTarget = GetFollowingTarget();
            var leaderPartyElement = GetLeaderPartyElement();

            // =================================================================
            // SECTION 2: TASK GENERATION LOGIC
            // =================================================================

            // Case 1: The leader is currently visible and in the same zone.
            if (followTarget != null)
            {
                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);
                
                // Update the timestamp since we can see the leader.
                _leaderLastSeen = DateTime.Now;

                // --- PRIORITY 1: DETECT A COMPLETED SAME-ZONE TELEPORT ---
                // This MUST run before we update any state variables for the current frame.

                // Calculate how far the leader moved since the last frame.
                
                var distanceMoved = Vector3.Distance(followTarget.Pos, lastTargetPosition);
                const float TELEPORT_DISTANCE_THRESHOLD = 150f;

                // Condition A: The leader moved an impossible distance in a single tick.
                if (distanceMoved > TELEPORT_DISTANCE_THRESHOLD)
                {
                    AreWeThereYet.Instance.LogMessage($"DEBUG1: The leader moved an impossible distance in a single tick: {distanceMoved}");
                    
                    // Condition B: We have a memory of the leader being near a portal right before they moved.
                    if (_lastKnownLeaderPortal != null && _lastKnownLeaderPortal.IsValid)
                    {
                        AreWeThereYet.Instance.LogMessage($"DEBUG2: We have a memory of the leader being near a portal right before they moved.");
                        // We have confirmed a same-zone teleport.
                        if (!tasks.Any(t => t.Type == TaskNodeType.Transition))
                        {
                            AreWeThereYet.Instance.LogMessage($"DEBUG3: We have confirmed a same-zone teleport.");
                            var portalLabel = AreWeThereYet.Instance.GameController.IngameState.IngameUi.ItemsOnGroundLabels
                                                .FirstOrDefault(x => x.ItemOnGround.Id == _lastKnownLeaderPortal.Id);

                            if (portalLabel != null)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage($"[SameZoneTeleport] Leader teleported {distanceMoved:F0} units. Using last known portal: {_lastKnownLeaderPortal.Metadata}");
                                }
                                // Clear all old movement tasks and create a single, high-priority transition task.
                                tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                                tasks.Insert(0, new TaskNode(portalLabel, 50, TaskNodeType.Transition));
                                _lastKnownLeaderPortal = null; // Consume the memory

                                // IMPORTANT: Update lastTargetPosition immediately to prevent re-triggering on the next frame.
                                lastTargetPosition = followTarget.Pos;

                                continue; // Commit to the transition and restart the main loop.
                            }
                        }
                    }
                }

                // --- PRIORITY 2: RECORD THE LEADER'S CURRENT POSITION FOR THE *NEXT* FRAME ---
                var leaderActor = followTarget.GetComponent<Actor>();
                if (leaderActor?.CurrentAction?.Target is { } target && (target.Type is EntityType.AreaTransition or EntityType.Portal or EntityType.TownPortal))
                {
                    _lastKnownLeaderPortal = target;
                    AreWeThereYet.Instance.LogMessage($"DEBUG4: Last known Leader Portal: {_lastKnownLeaderPortal}");
                }
                else
                {
                    // If the leader isn't actively targeting a portal, check if they are standing near one.
                    var closestPortalLabel = GetBestPortalLabel(new PartyElementWindow { ZoneName = "" }); // ZoneName is not used for this check.
                    if (closestPortalLabel != null && Vector3.Distance(followTarget.Pos, closestPortalLabel.ItemOnGround.Pos) < 100)
                    {
                        AreWeThereYet.Instance.LogMessage($"DEBUG5: Portal near Leader: {_lastKnownLeaderPortal}");
                        _lastKnownLeaderPortal = closestPortalLabel.ItemOnGround;
                    }
                    else
                    {
                        AreWeThereYet.Instance.LogMessage($"DEBUG6: Last known Leader Portal WAS: {_lastKnownLeaderPortal}");
                        _lastKnownLeaderPortal = null;
                        AreWeThereYet.Instance.LogMessage($"DEBUG7: Last known Leader Portal WAS NULLED");
                    }
                }

                // --- PRIORITY 3: Generate path for normal following. ---
                // This logic will only run if no teleport was detected.
                if (distanceToLeader >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                {
                    if (lastTargetPosition != Vector3.Zero && distanceMoved > AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                    {
                        var transition = GetBestPortalLabel(leaderPartyElement);
                        if (transition != null && transition.ItemOnGround.DistancePlayer < 80)
                            tasks.Add(new TaskNode(transition, 200, TaskNodeType.Transition));
                    }
                    else if (tasks.Count == 0 && distanceMoved < 2000 && distanceToLeader > 200 && distanceToLeader < 2000)
                    {
                        tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance));
                    }

                    else if (tasks.Count > 0)
                    {
                        var distanceFromLastTask = Vector3.Distance(tasks.Last().WorldPosition, followTarget.Pos);
                        if (distanceFromLastTask >= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance)
                            tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance));
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
                    if (AreWeThereYet.Instance.Settings.AutoPilot.CloseFollow.Value)
                    {
                        if (distanceToLeader >= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value)
                            tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance));
                    }

                    // --- Quest Item and other interaction logic ---
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
            
            // Case 2: The leader entity is NOT currently visible.
            else if (followTarget == null && leaderPartyElement != null && !_isTransitioning)
            {
                // --- SAFETY CHECK ---
                // If we saw the leader less than 1 second ago, it's likely a temporary sync issue (like stairs).
                if ((DateTime.Now - _leaderLastSeen).TotalMilliseconds < 1000)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage("[SyncWait] Leader entity lost. Waiting 1s for potential same-zone transition sync.");
                    }
                    yield return new WaitTime(100);
                    continue; // Restart the loop to re-check for the leader.
                }

                // If more than 1 second has passed, we are confident the leader has truly changed zones.
                // Now, execute your full 3-layer fallback system.
                
                if (tasks.Any(t => t.Type == TaskNodeType.Transition))
                {
                    yield return new WaitTime(100);
                    continue;
                }

                // --- LAYER 1: Try the precise portal memory first. This is the best-case scenario. ---
                if (_lastKnownLeaderPortal != null && _lastKnownLeaderPortal.IsValid)
                {
                    var portalLabel = AreWeThereYet.Instance.GameController.IngameState.IngameUi.ItemsOnGroundLabels
                                        .FirstOrDefault(x => x.ItemOnGround.Id == _lastKnownLeaderPortal.Id);

                    if (portalLabel != null)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"[PortalMemory] Leader is gone. Using exact portal memory: {portalLabel.ItemOnGround.Metadata}.");
                        }
                        tasks.Insert(0, new TaskNode(portalLabel, 50, TaskNodeType.Transition));
                    }

                    // Use the memory once, then clear it to prevent getting stuck on a stale portal reference.
                    _lastKnownLeaderPortal = null;
                }
                // --- LAYER 2: If memory failed, wait for reliable zone info and then scan for the correct portal. ---
                else
                {
                    // This logic handles the zone name update and buffer time.
                    if (!_lastKnownLeaderZone.Equals(leaderPartyElement.ZoneName))
                    {
                        _lastKnownLeaderZone = leaderPartyElement.ZoneName;
                        _leaderZoneChangeTime = DateTime.Now;
                    }

                    if (IsLeaderZoneInfoReliable(leaderPartyElement))
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"[PortalScan] Zone info is reliable ('{leaderPartyElement.ZoneName}'). Scanning for matching portal on ground.");
                        }

                        // Now that we have a reliable zone name, scan for a portal with matching text.
                        var portal = GetBestPortalLabel(leaderPartyElement);
                        if (portal != null)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"[PortalScan] SUCCESS: Found matching portal on ground: '{portal.Label.Text}'.");
                            }
                            tasks.Add(new TaskNode(portal, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
                        }
                        // --- LAYER 3: If the scan also fails, use the final fallback: the UI teleport button. ---
                        else
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage("[Fallback] No matching portal found on ground. Using party UI teleport button.");
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
                        // The party UI is not ready yet. We must wait.
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage("[PortalScan] Waiting for reliable zone info from party UI...");
                        }
                        yield return new WaitTime(200);
                    }
                }
            }


            // =================================================================
            // SECTION 3: TASK EXECUTION STATE MACHINE
            // =================================================================
            // This section executes whatever task is at the front of the queue.

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
                        if (AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled &&
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

                            // SET THE FLAG: We are about to change zones.
                            _isTransitioning = true;

                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
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
            lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
            yield return new WaitTime(50);
        }
    }
    
    private bool ShouldUseDash(Vector2 targetPosition)
    {
        try
        {
            // Comprehensive null checks
            if (LineOfSight == null || 
                AreWeThereYet.Instance?.GameController?.Player?.GridPos == null ||
                AreWeThereYet.Instance?.Settings?.AutoPilot?.DashEnabled?.Value != true)
                return false;

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

            // This is where exceptions were crashing your coroutine
            var hasLineOfSight = LineOfSight.HasLineOfSight(playerPosNumerics, targetPosNumerics);
            var shouldDash = !hasLineOfSight && distance >= minDistance;
            
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"ShouldUseDash: RESULT = {shouldDash} (distance: {distance:F1}, hasLineOfSight: {hasLineOfSight})");
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
                    new Vector2(500, 280));
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
                    $"Follow Enabled: {AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value}", new System.Numerics.Vector2(500, 220));
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Task Count: {taskCount:D} Next WP Distance: {dist:F} Target Distance: {targetDist:F}",
                    new System.Numerics.Vector2(500, 240));
            }
        }
        catch (Exception)
        {
        }

        AreWeThereYet.Instance.Graphics.DrawText("AutoPilot: Active", new System.Numerics.Vector2(350, 220));
        AreWeThereYet.Instance.Graphics.DrawText("Coroutine: " + (autoPilotCoroutine.Running ? "Active" : "Dead"), new System.Numerics.Vector2(350, 240));
        AreWeThereYet.Instance.Graphics.DrawText("Leader: " + "[ " + AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value + " ] " + (followTarget != null ? "Found" : "Null"), new System.Numerics.Vector2(500, 260));
        AreWeThereYet.Instance.Graphics.DrawLine(new System.Numerics.Vector2(490, 210), new System.Numerics.Vector2(490, 310), 1, Color.White);
    }
}
