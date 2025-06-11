using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using SharpDX;
using Graphics = ExileCore.Graphics;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace AreWeThereYet.Utils
{
    public class LineOfSight
    {
        private readonly GameController _gameController;
        private int[][] _terrainData;
        private Vector2i _areaDimensions;
        
        // Periodic refresh for dynamic door detection
        private DateTime _lastTerrainRefresh = DateTime.MinValue;
        private int TerrainRefreshInterval => AreWeThereYet.Instance.Settings.Debug.Terrain.RefreshInterval?.Value ?? 500;
        
        // TraceMyRay-style settings integration
        //private bool UseWalkableTerrainInsteadOfTargetTerrain => AreWeThereYet.Instance.Settings.UseWalkableTerrainInsteadOfTargetTerrain?.Value ?? false;
        private int TerrainValueForCollision => AreWeThereYet.Instance.Settings.Debug.Raycast.TerrainValueForCollision?.Value ?? 2;
        
        // Debug visualization (keeping your current approach but enhanced)
        private readonly List<(Vector2 Pos, int Value)> _debugPoints = new();
        private readonly List<(Vector2 Start, Vector2 End, bool IsVisible)> _debugRays = new();
        private readonly HashSet<Vector2> _debugVisiblePoints = new();
        private float _lastObserverZ;

        // Cursor ray tracking
        private readonly List<(Vector2 Start, Vector2 End, bool IsVisible)> _cursorRays = new();
        private Vector2 _lastCursorPosition;

        public LineOfSight(GameController gameController)
        {
            _gameController = gameController;

            var eventBus = EventBus.Instance;
            eventBus.Subscribe<AreaChangeEvent>(HandleAreaChange);
            eventBus.Subscribe<RenderEvent>(HandleRender);
        }

        private void HandleAreaChange(AreaChangeEvent evt)
        {
            UpdateArea();
        }

        private void HandleRender(RenderEvent evt)
        {
            if (!AreWeThereYet.Instance.Settings.Debug.EnableRendering) return;
            if (!AreWeThereYet.Instance.Settings.Debug.ShowTerrainDebug) return;

            if (_terrainData == null) return;

            // Check if we need to refresh terrain data for door detection
            RefreshTerrainData();

            // Update observer with cursor position
            var cursorWorldPos = AreWeThereYet.Instance.Settings.Debug.Raycast.CastRayToWorldCursorPos?.Value == true
                ? AreWeThereYet.Instance.GameController.IngameState.ServerData.WorldMousePositionNum.WorldToGrid()
                : (Vector2?)null;

            UpdateObserver(_gameController.Player.GridPosNum, cursorWorldPos);

            RenderTerrainGrid(evt);
            RenderCursorRays(evt);
            RenderDebugInfo(evt);
        }

        /// <summary>
        /// Update observer with optional cursor position for ray casting
        /// </summary>
        public void UpdateObserver(Vector2 playerPosition, Vector2? cursorPosition = null)
        {
            _debugVisiblePoints.Clear();
            _cursorRays.Clear();
            
            UpdateDebugGrid(playerPosition);

            // Cast ray to cursor position if enabled
            if (cursorPosition.HasValue && AreWeThereYet.Instance.Settings.Debug.Raycast.CastRayToWorldCursorPos?.Value == true)
            {
                _lastCursorPosition = cursorPosition.Value;
                var isVisible = HasLineOfSightInternal(playerPosition, cursorPosition.Value);
                _cursorRays.Add((playerPosition, cursorPosition.Value, isVisible));
            }
        }

        /// <summary>
        /// TraceMyRay-style area update with support for both terrain types
        /// </summary>
        public void UpdateArea()
        {
            _areaDimensions = _gameController.IngameState.Data.AreaDimensions;
            UpdateTerrainData();
            _lastTerrainRefresh = DateTime.Now;
        }

        /// <summary>
        /// Hybrid approach: Use TraceMyRay's simple API access with periodic refresh for door detection
        /// </summary>
        private void RefreshTerrainData()
        {
            var timeSinceRefresh = (DateTime.Now - _lastTerrainRefresh).TotalMilliseconds;
            
            if (timeSinceRefresh >= TerrainRefreshInterval)
            {
                UpdateTerrainData();
                _lastTerrainRefresh = DateTime.Now;
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"LineOfSight: Refreshed terrain data at {DateTime.Now:HH:mm:ss.fff}");
                }
            }
        }

        /// <summary>
        /// Enhanced terrain data loading - combines both pathfinding and targeting data
        /// </summary>
        private void UpdateTerrainData()
        {
            try
            {
                // Read BOTH terrain data sources (like the original dual-layer approach)
                var pathfindingData = _gameController.IngameState.Data.RawPathfindingData;    // Dynamic walkability (doors)
                var targetingData = _gameController.IngameState.Data.RawTerrainTargetingData; // Static ranged line-of-sight

                if (pathfindingData == null || targetingData == null)
                {
                    AreWeThereYet.Instance.LogError("LineOfSight: One or both terrain data sources are null");
                    return;
                }

                // Ensure both data sources have the same dimensions
                var rows = Math.Min(pathfindingData.Length, targetingData.Length);
                _terrainData = new int[rows][];
                
                for (var y = 0; y < rows; y++)
                {
                    var cols = Math.Min(pathfindingData[y].Length, targetingData[y].Length);
                    _terrainData[y] = new int[cols];
                    
                    for (var x = 0; x < cols; x++)
                    {
                        // Combine both terrain layers using the modern APIs
                        var walkableValue = pathfindingData[y][x];   // Dynamic walkability
                        var rangedValue = targetingData[y][x];       // Static ranged data
                        
                        _terrainData[y][x] = CombineModernTerrainLayers(walkableValue, rangedValue);
                    }
                }
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"LineOfSight: Combined terrain data - Pathfinding: {pathfindingData.Length}x{pathfindingData[0].Length}, Targeting: {targetingData.Length}x{targetingData[0].Length}");
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"LineOfSight: Failed to update terrain data: {ex.Message}");
            }
        }

        /// <summary>
        /// Combines modern ExileCore terrain data sources (replaces the old dual-layer byte parsing)
        /// </summary>
        private int CombineModernTerrainLayers(int walkableValue, int rangedValue)
        {
            // Modern terrain combination logic using ExileCore's processed data
            // Values are already processed by ExileCore, so we use them directly
            
            // Priority system:
            // 1. If walkable value indicates passable terrain, use it
            // 2. If walkable is blocked but ranged allows passage, mark as dashable
            // 3. Otherwise, blocked
            
            // High walkable values (4-5) = passable terrain
            if (walkableValue >= 4)
            {
                return 5; // Fully walkable (open areas, open doors)
            }
            
            // Low walkable values (0-2) = blocked, but check ranged
            if (walkableValue <= 2)
            {
                // If ranged value allows passage (3-5), it's dashable
                if (rangedValue >= 3)
                {
                    return 2; // Dashable/teleportable (closed doors you can dash through)
                }
                else
                {
                    return 0; // Completely impassable (solid walls)
                }
            }
            
            // Medium walkable values (3) = partially passable
            return 3; // Moderate terrain
        }

        /// <summary>
        /// Main line-of-sight check with automatic terrain refresh
        /// </summary>
        public bool HasLineOfSight(Vector2 start, Vector2 end)
        {
            if (_terrainData == null) return false;

            // Force refresh check before critical pathfinding decisions
            RefreshTerrainData();

            // Update debug visualization
            _debugVisiblePoints.Clear();
            UpdateDebugGrid(start);

            var isVisible = HasLineOfSightInternal(start, end);
            _debugRays.Add((start, end, isVisible));

            return isVisible;
        }

        /// <summary>
        /// TraceMyRay-style efficient DDA line-of-sight algorithm
        /// </summary>
        private bool HasLineOfSightInternal(Vector2 start, Vector2 end)
        {
            var startX = (int)start.X;
            var startY = (int)start.Y;
            var endX = (int)end.X;
            var endY = (int)end.Y;

            if (!IsInBounds(startX, startY) || !IsInBounds(endX, endY))
                return false;

            var dx = Math.Abs(endX - startX);
            var dy = Math.Abs(endY - startY);
            
            var x = startX;
            var y = startY;
            var stepX = startX < endX ? 1 : -1;
            var stepY = startY < endY ? 1 : -1;

            // Handle straight lines efficiently (TraceMyRay approach)
            if (dx == 0)
            {
                // Vertical line
                for (var i = 0; i < dy; i++)
                {
                    y += stepY;
                    var pos = new Vector2(x, y);
                    if (!IsTerrainPassable(pos)) return false;
                    _debugVisiblePoints.Add(pos);
                }
                return true;
            }

            if (dy == 0)
            {
                // Horizontal line
                for (var i = 0; i < dx; i++)
                {
                    x += stepX;
                    var pos = new Vector2(x, y);
                    if (!IsTerrainPassable(pos)) return false;
                    _debugVisiblePoints.Add(pos);
                }
                return true;
            }

            // DDA algorithm for diagonal lines (TraceMyRay approach)
            var deltaErr = Math.Abs((float)dy / dx);
            var error = 0.0f;

            if (dx >= dy)
            {
                // Drive by X
                for (var i = 0; i < dx; i++)
                {
                    x += stepX;
                    error += deltaErr;

                    if (error >= 0.5f)
                    {
                        y += stepY;
                        error -= 1.0f;
                    }

                    var pos = new Vector2(x, y);
                    if (!IsTerrainPassable(pos)) return false;
                    _debugVisiblePoints.Add(pos);
                }
            }
            else
            {
                // Drive by Y
                deltaErr = Math.Abs((float)dx / dy);
                for (var i = 0; i < dy; i++)
                {
                    y += stepY;
                    error += deltaErr;

                    if (error >= 0.5f)
                    {
                        x += stepX;
                        error -= 1.0f;
                    }

                    var pos = new Vector2(x, y);
                    if (!IsTerrainPassable(pos)) return false;
                    _debugVisiblePoints.Add(pos);
                }
            }

            return true;
        }

        /// <summary>
        /// TraceMyRay-style configurable terrain passability check
        /// </summary>
        private bool IsTerrainPassable(Vector2 pos)
        {
            var terrainValue = GetTerrainValue(pos);
            
            // TraceMyRay's simple, configurable approach
            // Values <= threshold are blocked, values > threshold are passable
            if (terrainValue <= TerrainValueForCollision)
                return false;
            
            // Optional: Add dash-through logic for specific values
            if (terrainValue == TerrainValueForCollision + 1) // e.g., value 3 if threshold is 2
            {
                return AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled?.Value == true;
            }
            
            return true; // All higher values are passable
        }

        private void UpdateDebugGrid(Vector2 center)
        {
            _debugPoints.Clear();
            const int size = 200;

            for (var y = -size; y <= size; y++)
                for (var x = -size; x <= size; x++)
                {
                    if (x * x + y * y > size * size) continue;

                    var pos = new Vector2(center.X + x, center.Y + y);
                    var value = GetTerrainValue(pos);
                    if (value >= 0) _debugPoints.Add((pos, value));
                }

            _lastObserverZ = _gameController.IngameState.Data.GetTerrainHeightAt(center);
        }

        /// <summary>
        /// Enhanced debug rendering with TraceMyRay-style color mapping
        /// </summary>
        private void RenderTerrainGrid(RenderEvent evt)
        {
            foreach (var (pos, value) in _debugPoints)
            {
                // Use DrawAtPlayerPlane setting like TraceMyRay
                var z = AreWeThereYet.Instance.Settings.Debug.Raycast.DrawAtPlayerPlane?.Value == true
                    ? _lastObserverZ
                    : _gameController.IngameState.Data.GetTerrainHeightAt(pos);
                    
                var worldPos = new Vector3(pos.GridToWorld(), z);
                var screenPos = _gameController.IngameState.Camera.WorldToScreen(worldPos);

                SharpDX.Color color;
                if (_debugVisiblePoints.Contains(pos))
                {
                    color = SharpDX.Color.Yellow; // Line of sight trace
                }
                else
                {
                    // Enhanced color mapping based on passability
                    color = value switch
                    {
                        0 => new SharpDX.Color(220, 100, 50, 200),   // Red - Impassable
                        1 => new SharpDX.Color(255, 165, 0, 200),    // Orange - Low obstacle
                        2 => new SharpDX.Color(255, 255, 0, 180),    // Yellow - Medium obstacle (default threshold)
                        3 => new SharpDX.Color(100, 255, 100, 180),  // Light Green - Dashable
                        4 => new SharpDX.Color(50, 200, 50, 160),    // Green - Walkable
                        5 => new SharpDX.Color(0, 150, 0, 160),      // Dark Green - Highly walkable
                        _ => new SharpDX.Color(128, 128, 128, 160)   // Gray - Unknown
                    };
                }

                // Draw the terrain values with colored dots or numbers
                if (AreWeThereYet.Instance.Settings.Debug.Terrain.ReplaceValuesWithDots?.Value == true)
                    evt.Graphics.DrawCircleFilled(
                        screenPos,
                        AreWeThereYet.Instance.Settings.Debug.Terrain.DotSize.Value,
                        color,
                        AreWeThereYet.Instance.Settings.Debug.Terrain.DotSegments.Value
                    );
                else
                    evt.Graphics.DrawText(
                        value.ToString(),
                        screenPos,
                        color,
                        FontAlign.Center
                    );
            }
        }

        /// <summary>
        /// Check if cursor position is visible from player
        /// </summary>
        public bool IsCursorPositionVisible()
        {
            if (_cursorRays.Count == 0) return false;
            return _cursorRays[0].IsVisible;
        }

        /// <summary>
        /// Render the cursor ray line and endpoint
        /// </summary>
        private void RenderCursorRays(RenderEvent evt)
        {
            if (AreWeThereYet.Instance.Settings.Debug.Raycast.CastRayToWorldCursorPos?.Value != true) return;

            foreach (var (start, end, isVisible) in _cursorRays)
            {
                // Use DrawAtPlayerPlane setting for consistent height
                var z = AreWeThereYet.Instance.Settings.Debug.Raycast.DrawAtPlayerPlane?.Value == true
                    ? _lastObserverZ
                    : _gameController.IngameState.Data.GetTerrainHeightAt(end);

                var startWorld = new Vector3(start.GridToWorld(), _lastObserverZ);
                var endWorld = new Vector3(end.GridToWorld(), z);

                var startScreen = _gameController.IngameState.Camera.WorldToScreen(startWorld);
                var endScreen = _gameController.IngameState.Camera.WorldToScreen(endWorld);

                // Choose color based on line-of-sight result
                var lineColor = isVisible 
                    ? new SharpDX.Color(0, 255, 0, 200)    // Green - Clear line of sight
                    : new SharpDX.Color(255, 0, 0, 200);   // Red - Blocked

                // Draw the ray line
                evt.Graphics.DrawLine(startScreen, endScreen, 2.0f, lineColor);

                // Draw endpoint circle
                var endpointColor = isVisible
                    ? new SharpDX.Color(0, 255, 0, 255)    // Bright green - Visible
                    : new SharpDX.Color(255, 0, 0, 255);   // Bright red - Blocked

                evt.Graphics.DrawCircleFilled(endScreen, 5.0f, endpointColor, 16);
            }
        }

        /// <summary>
        /// Debug information rendering
        /// </summary> 
        private void RenderDebugInfo(RenderEvent evt)
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                var timeSinceRefresh = (DateTime.Now - _lastTerrainRefresh).TotalMilliseconds;
                
                evt.Graphics.DrawText(
                    $"Terrain: Combined (Pathfinding + Targeting) | Refresh: {timeSinceRefresh:F0}ms ago | Threshold: {TerrainValueForCollision}",
                    new Vector2(10, 200),
                    SharpDX.Color.White
                );
                
                evt.Graphics.DrawText(
                    $"Terrain Data: {_terrainData?.Length ?? 0} rows (Combined layers)",
                    new Vector2(10, 220),
                    SharpDX.Color.White
                );

                // Cursor ray debug info
                if (AreWeThereYet.Instance.Settings.Debug.Raycast.CastRayToWorldCursorPos?.Value == true && _cursorRays.Count > 0)
                {
                    var cursorRayStatus = _cursorRays[0].IsVisible ? "CLEAR" : "BLOCKED";
                    var cursorRayColor = _cursorRays[0].IsVisible ? SharpDX.Color.Green : SharpDX.Color.Red;
                    
                    evt.Graphics.DrawText(
                        $"Cursor Ray: {cursorRayStatus} | Position: ({_lastCursorPosition.X:F1}, {_lastCursorPosition.Y:F1})",
                        new Vector2(10, 240),
                        cursorRayColor
                    );
                }
            }
        }

        private bool IsInBounds(int x, int y)
        {
            return x >= 0 && x < _areaDimensions.X && y >= 0 && y < _areaDimensions.Y;
        }

        private int GetTerrainValue(Vector2 position)
        {
            var x = (int)position.X;
            var y = (int)position.Y;

            if (!IsInBounds(x, y)) return -1;
            return _terrainData[y][x];
        }

        public void Clear()
        {
            _terrainData = null;
            _debugPoints.Clear();
            _debugRays.Clear();
            _debugVisiblePoints.Clear();
            _lastTerrainRefresh = DateTime.MinValue;
        }
    }
}
