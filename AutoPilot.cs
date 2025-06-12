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
using GameOffsets.Native;

namespace AreWeThereYet;

public class AutoPilot
{
    private Coroutine autoPilotCoroutine;
    private readonly Random random = new Random();

    private Vector3 lastTargetPosition;
    private Vector3 lastPlayerPosition;
    private Entity followTarget;

    private List<TaskNode> tasks = new List<TaskNode>();

    // Enhanced pathfinding components
    private FollowerPathFinder _pathfinder;
    private List<Vector2i> _currentPath;
    private int _currentPathIndex;
    private DateTime _lastPathUpdate = DateTime.MinValue;
    private readonly TimeSpan _pathUpdateInterval = TimeSpan.FromMilliseconds(1000);

    private LineOfSight LineOfSight => AreWeThereYet.Instance.lineOfSight;

    private void ResetPathing()
    {
        tasks = new List<TaskNode>();
        followTarget = null;
        lastTargetPosition = Vector3.Zero;
        lastPlayerPosition = Vector3.Zero;

        // Reset pathfinding state
        _currentPath = null;
        _currentPathIndex = 0;
        _lastPathUpdate = DateTime.MinValue;
    }

    public void AreaChange()
    {
        ResetPathing();

        // Initialize pathfinder for new area
        try
        {
            var areaDimensions = AreWeThereYet.Instance.GameController.IngameState.Data.AreaDimensions;
            _pathfinder = new FollowerPathFinder(LineOfSight, areaDimensions);
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"Failed to initialize pathfinder: {ex.Message}");
            _pathfinder = null;
        }
    }

    public void StartCoroutine()
    {
        autoPilotCoroutine = new Coroutine(AutoPilotLogic(), AreWeThereYet.Instance, "AutoPilot");
        Core.ParallelRunner.Run(autoPilotCoroutine);
    }

    // Enhanced pathfinding logic
    private bool ShouldUpdatePath(Vector3 leaderPosition)
    {
        var timeSinceUpdate = DateTime.Now - _lastPathUpdate;
        var pathUpdateInterval = TimeSpan.FromMilliseconds(AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.PathUpdateInterval.Value);
        var leaderMoved = Vector3.Distance(lastTargetPosition, leaderPosition) >
                         AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value * 0.5f;
        var pathCompleted = _currentPath == null || _currentPathIndex >= _currentPath.Count;

        return timeSinceUpdate > pathUpdateInterval || leaderMoved || pathCompleted;
    }

    private void UpdatePathToLeader(Vector3 leaderPosition)
    {
        try
        {
            if (_pathfinder == null || !AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.EnableAdvancedPathfinding.Value)
            {
                // Fallback to old behavior
                CreateSimpleTask(leaderPosition);
                return;
            }

            var playerPos = AreWeThereYet.Instance.GameController.Player.GridPos.Truncate();
            var leaderGridPos = leaderPosition.WorldToGridInt();

            // Update pathfinder's walkable grid
            _pathfinder.UpdateWalkableGrid();

            // Find path using enhanced pathfinding
            _currentPath = _pathfinder.FindPath(playerPos, leaderGridPos);
            _currentPathIndex = 0;
            _lastPathUpdate = DateTime.Now;
            lastTargetPosition = leaderPosition;

            if (_currentPath != null && _currentPath.Count > 0)
            {
                // Convert path to task system
                ConvertPathToTasks(_currentPath);

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Generated path with {_currentPath.Count} waypoints");
                }
            }
            else
            {
                // Pathfinding failed, use fallback
                CreateSimpleTask(leaderPosition);

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage("Pathfinding failed, using direct movement");
                }
            }

            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                var pathQuality = _currentPath?.Count > 0 ? "Good" : "Failed";
                var cacheStats = _pathfinder?.GetStats();
                AreWeThereYet.Instance.LogMessage($"Path: {pathQuality} | Cache: {cacheStats?.CacheHitRate} | Time: {cacheStats?.AveragePathfindingTime:F1}ms");
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"Path update failed: {ex.Message}");
            CreateSimpleTask(leaderPosition);
        }
    }

    private void ConvertPathToTasks(List<Vector2i> path)
    {
        tasks.Clear();

        // Skip first few nodes that are too close
        var startIndex = 0;
        var playerPos = AreWeThereYet.Instance.GameController.Player.GridPos.Truncate();
        var skipDistance = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.WaypointSkipDistance.Value;

        for (var i = 0; i < path.Count; i++)
        {
            if (playerPos.Distance(path[i]) > skipDistance)
            {
                startIndex = i;
                break;
            }
        }

        // Create tasks from path, combining nearby waypoints for efficiency
        var maxPathLength = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.MaxPathLength.Value;
        var step = Math.Max(1, path.Count / maxPathLength);

        for (var i = startIndex; i < path.Count; i += step)
        {
            var gridPos = path[i];
            var worldPos = gridPos.GridToWorld();

            tasks.Add(new TaskNode(worldPos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance));
        }
    }

    private void CreateSimpleTask(Vector3 leaderPosition)
    {
        // Fallback to original behavior
        tasks.Clear();
        tasks.Add(new TaskNode(leaderPosition, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance));
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

    private LabelOnGround GetBestPortalLabel(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var currentZoneName = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            if (leaderPartyElement.ZoneName.Equals(currentZoneName) || (!leaderPartyElement.ZoneName.Equals(currentZoneName) && (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout) || AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.RealLevel >= 68)
            {
                var portalLabels =
                    AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                            x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible && x.ItemOnGround != null &&
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") || x.ItemOnGround.Metadata.ToLower().Contains("portal")))
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
                var portal = GetBestPortalLabel(leaderPartyElement);
                if (portal != null)
                {
                    tasks.Add(new TaskNode(portal, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
                }
                else
                {
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
                    if (!tpButton.Equals(Vector2.Zero))
                    {
                        yield return Mouse.SetCursorPosHuman(tpButton, false);
                        yield return new WaitTime(200);
                        yield return Mouse.LeftClick();
                        yield return new WaitTime(200);
                    }
                }
            }
            else if (followTarget != null)
            {
                var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);

                // Enhanced pathfinding logic
                if (distanceToLeader >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                {
                    if (ShouldUpdatePath(followTarget.Pos))
                    {
                        UpdatePathToLeader(followTarget.Pos);
                    }
                }
                else if (distanceToLeader <= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value * 0.8f)
                {
                    // Clear path when close enough
                    _currentPath = null;
                    tasks.Clear();
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

                    var questLoot = GetQuestItem();
                    if (questLoot != null &&
                        Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value &&
                        tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) == null)
                        tasks.Add(new TaskNode(questLoot.Pos, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance, TaskNodeType.Loot));
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

                    case TaskNodeType.ClaimWaypoint:
                        {
                            if (Vector3.Distance(AreWeThereYet.Instance.playerPosition, currentTask.WorldPosition) > 150)
                            {
                                var screenPos = Helper.WorldToValidScreenPosition(currentTask.WorldPosition);
                                Input.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                                yield return new WaitTime(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency);
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
        try
        {
            // Add comprehensive null checks like CoPilot
            if (LineOfSight == null ||
                AreWeThereYet.Instance?.GameController?.Player?.GridPos == null ||
                AreWeThereYet.Instance?.Settings?.AutoPilot?.DashEnabled?.Value != true)
                return false;

            var playerPos = AreWeThereYet.Instance.GameController.Player.GridPos;
            var distance = Vector2.Distance(playerPos, targetPosition);

            // Don't dash for very short or very long distances
            if (distance < 30 || distance > 150)
                return false;

            // Convert SharpDX.Vector2 to System.Numerics.Vector2 for HasLineOfSight
            var playerPosNumerics = playerPos.ToNumerics();
            var targetPosNumerics = targetPosition.ToNumerics();

            // This is where exceptions were crashing your coroutine
            var hasLineOfSight = LineOfSight.HasLineOfSight(playerPosNumerics, targetPosNumerics);

            // Dash when there's NO line of sight (obstacles in the way)
            return !hasLineOfSight && distance >= 50;
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

        var statusBackgroundColor = AreWeThereYet.Instance.Settings.Debug.TextBackgroundColor;
        var statusPadding = AreWeThereYet.Instance.Settings.Debug.TextBackgroundPadding;

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

        // Enhanced pathfinding visualization
        if (AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.ShowPathVisualization?.Value == true &&
            _currentPath != null && _currentPath.Count > 0)
        {
            RenderPathVisualization();
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
                Helper.DrawTextWithBackground(
                    $"Follow Enabled: {AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value}",
                    new System.Numerics.Vector2(500, 120),
                    SharpDX.Color.White,
                    statusBackgroundColor,
                    statusPadding);
                Helper.DrawTextWithBackground(
                    $"Task Count: {taskCount:D} Next WP Distance: {dist:F} Target Distance: {targetDist:F}",
                    new System.Numerics.Vector2(500, 140),
                    SharpDX.Color.White,
                    statusBackgroundColor,
                    statusPadding);

            }
        }
        catch (Exception)
        {
        }

        // Enhanced debug information
        RenderDebugInfo();

        Helper.DrawTextWithBackground(
            "AutoPilot: Active",
            new System.Numerics.Vector2(350, 120),
            SharpDX.Color.LightGreen,
            statusBackgroundColor,
            statusPadding);
        Helper.DrawTextWithBackground(
            "Coroutine: " + (autoPilotCoroutine.Running ? "Active" : "Dead"),
            new System.Numerics.Vector2(350, 140),
            autoPilotCoroutine.Running ? SharpDX.Color.LightGreen : SharpDX.Color.LightCoral,
            statusBackgroundColor,
            statusPadding);
        Helper.DrawTextWithBackground(
            "Leader: " + "[ " + AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value + " ] " + (followTarget != null ? "Found" : "Null"),
            new System.Numerics.Vector2(500, 160),
            followTarget != null ? SharpDX.Color.LightGreen : SharpDX.Color.Yellow,
            statusBackgroundColor,
            statusPadding);
    }

    private void RenderPathVisualization()
    {
        try
        {
            var lineColor = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.PathVisualizationColor.Value;
            var lineWidth = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.PathVisualizationLineWidth.Value;
            const float GridToWorldMultiplier = 250f / 23f;

            for (var i = _currentPathIndex; i < _currentPath.Count - 1; i++)
            {
                var currentNode = _currentPath[i];
                var nextNode = _currentPath[i + 1];

                var currentWorld = new Vector3(
                    currentNode.X * GridToWorldMultiplier,
                    currentNode.Y * GridToWorldMultiplier,
                    0);
                var nextWorld = new Vector3(
                    nextNode.X * GridToWorldMultiplier,
                    nextNode.Y * GridToWorldMultiplier,
                    0);

                var currentScreen = Helper.WorldToValidScreenPosition(currentWorld);
                var nextScreen = Helper.WorldToValidScreenPosition(nextWorld);

                AreWeThereYet.Instance.Graphics.DrawLine(currentScreen, nextScreen, lineWidth, lineColor);
            }

            // Draw current waypoint
            if (_currentPathIndex < _currentPath.Count)
            {
                var currentWaypoint = _currentPath[_currentPathIndex];
                var waypointWorld = new Vector3(
                    currentWaypoint.X * GridToWorldMultiplier,
                    currentWaypoint.Y * GridToWorldMultiplier,
                    0);
                var waypointScreen = Helper.WorldToValidScreenPosition(waypointWorld);

                AreWeThereYet.Instance.Graphics.DrawCircleFilled(waypointScreen.ToNumerics(),
                    AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.WaypointSize.Value,
                    AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.WaypointColor.Value, 16);
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"Path visualization failed: {ex.Message}");
        }
    }

    private void RenderDebugInfo()
    {
        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
        {
            var baseY = 300;
            var lineHeight = 20;
            var currentLine = 0;
            var backgroundColor = AreWeThereYet.Instance.Settings.Debug.TextBackgroundColor;
            var textColor = SharpDX.Color.White;
            var padding = AreWeThereYet.Instance.Settings.Debug.TextBackgroundPadding;
            
            // Pathfinder status
            var text1 = $"Pathfinder: {(_pathfinder != null ? "Active" : "Inactive")}";
            var pos1 = new System.Numerics.Vector2(10, baseY + currentLine++ * lineHeight);
            Helper.DrawTextWithBackground(text1, pos1, textColor, backgroundColor, padding);
            
            if (_currentPath != null)
            {
                // Path info
                var text2 = $"Path Length: {_currentPath.Count} | Current Index: {_currentPathIndex}";
                var pos2 = new System.Numerics.Vector2(10, baseY + currentLine++ * lineHeight);
                Helper.DrawTextWithBackground(text2, pos2, textColor, backgroundColor, padding);
                    
                var timeSinceUpdate = DateTime.Now - _lastPathUpdate;
                var text3 = $"Last Path Update: {timeSinceUpdate.TotalSeconds:F1}s ago";
                var pos3 = new System.Numerics.Vector2(10, baseY + currentLine++ * lineHeight);
                Helper.DrawTextWithBackground(text3, pos3, textColor, backgroundColor, padding);
            }

            // Show pathfinding stats
            if (AreWeThereYet.Instance.Settings.Debug.ShowPathfindingStats?.Value == true && _pathfinder != null)
            {
                var stats = _pathfinder.GetStats();
                var text4 = $"Cache: {stats.CacheHitRate} | Distance Fields: {stats.ExactDistanceFields} | Direction Fields: {stats.DirectionFields}";
                var pos4 = new System.Numerics.Vector2(10, baseY + currentLine++ * lineHeight);
                Helper.DrawTextWithBackground(text4, pos4, textColor, backgroundColor, padding);
                    
                var text5 = $"Avg Pathfinding Time: {stats.AveragePathfindingTime:F2}ms";
                var pos5 = new System.Numerics.Vector2(10, baseY + currentLine++ * lineHeight);
                Helper.DrawTextWithBackground(text5, pos5, textColor, backgroundColor, padding);
            }
        }
    }
}
