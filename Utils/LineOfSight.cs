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
        private int TerrainRefreshInterval => AreWeThereYet.Instance.Settings.TerrainRefreshInterval?.Value ?? 500;
        
        // TraceMyRay-style settings integration
        private bool UseWalkableTerrainInsteadOfTargetTerrain => AreWeThereYet.Instance.Settings.UseWalkableTerrainInsteadOfTargetTerrain?.Value ?? false;
        private int TerrainValueForCollision => AreWeThereYet.Instance.Settings.TerrainValueForCollision?.Value ?? 2;
        
        // Debug visualization (keeping your current approach but enhanced)
        private readonly List<(Vector2 Pos, int Value)> _debugPoints = new();
        private readonly List<(Vector2 Start, Vector2 End, bool IsVisible)> _debugRays = new();
        private readonly HashSet<Vector2> _debugVisiblePoints = new();
        private float _lastObserverZ;

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
            if (!AreWeThereYet.Instance.Settings.EnableRendering) return;
            if (!AreWeThereYet.Instance.Settings.ShowTerrainDebug) return;

            if (_terrainData == null) return;

            // Check if we need to refresh terrain data for door detection
            RefreshTerrainData();

            UpdateDebugGrid(_gameController.Player.GridPosNum);
            RenderTerrainGrid(evt);
            RenderDebugInfo(evt);
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
                
                if (AreWeThereYet.Instance.Settings.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"LineOfSight: Refreshed terrain data at {DateTime.Now:HH:mm:ss.fff}");
                }
            }
        }

        /// <summary>
        /// TraceMyRay-style terrain data loading - much simpler than dual-layer approach
        /// </summary>
        private void UpdateTerrainData()
        {
            try
            {
                // Use TraceMyRay's approach - direct API access with configurable terrain type
                var rawData = UseWalkableTerrainInsteadOfTargetTerrain
                    ? _gameController.IngameState.Data.RawPathfindingData
                    : _gameController.IngameState.Data.RawTerrainTargetingData;

                _terrainData = new int[rawData.Length][];
                for (var y = 0; y < rawData.Length; y++)
                {
                    _terrainData[y] = new int[rawData[y].Length];
                    Array.Copy(rawData[y], _terrainData[y], rawData[y].Length);
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"LineOfSight: Failed to update terrain data: {ex.Message}");
            }
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
                return AreWeThereYet.Instance.Settings.autoPilotDashEnabled?.Value == true;
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
                var z = AreWeThereYet.Instance.Settings.DrawAtPlayerPlane?.Value == true
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
                        3 => new SharpDX.Color(100, 255, 100, 180), // Light Green - Dashable
                        4 => new SharpDX.Color(50, 200, 50, 160),    // Green - Walkable
                        5 => new SharpDX.Color(0, 150, 0, 160),      // Dark Green - Highly walkable
                        _ => new SharpDX.Color(128, 128, 128, 160)   // Gray - Unknown
                    };
                }

                // Draw the terrain values with colored dots or numbers
                if (AreWeThereYet.Instance.Settings.ReplaceTerrainValuesWithDots?.Value == true)
                    evt.Graphics.DrawCircleFilled(
                        screenPos,
                        AreWeThereYet.Instance.Settings.TerrainDotSize.Value,
                        color,
                        AreWeThereYet.Instance.Settings.TerrainDotSegments.Value
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
        /// Debug information rendering
        /// </summary>
        private void RenderDebugInfo(RenderEvent evt)
        {
            if (AreWeThereYet.Instance.Settings.ShowDetailedDebug?.Value == true)
            {
                var timeSinceRefresh = (DateTime.Now - _lastTerrainRefresh).TotalMilliseconds;
                var terrainType = UseWalkableTerrainInsteadOfTargetTerrain ? "Pathfinding" : "Targeting";
                
                evt.Graphics.DrawText(
                    $"Terrain: {terrainType} | Refresh: {timeSinceRefresh:F0}ms ago | Threshold: {TerrainValueForCollision}",
                    new Vector2(10, 200),
                    SharpDX.Color.White
                );
                
                evt.Graphics.DrawText(
                    $"Terrain Data: {_terrainData?.Length ?? 0} rows",
                    new Vector2(10, 220),
                    SharpDX.Color.White
                );
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
