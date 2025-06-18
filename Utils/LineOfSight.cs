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
        public volatile int[][] _terrainData;
        private readonly object _terrainDataLock = new object();
        
        private Vector2i _areaDimensions;
        
        // Periodic refresh for dynamic door detection
        private DateTime _lastTerrainRefresh = DateTime.MinValue;
        private int TerrainRefreshInterval => AreWeThereYet.Instance.Settings.Debug.Terrain.RefreshInterval?.Value ?? 1000;
        
        // Debug visualization (keeping your current approach but enhanced)
        private readonly List<(Vector2 Pos, int Value)> _debugPoints = new();
        private readonly List<(Vector2 Start, Vector2 End, bool IsVisible)> _debugRays = new();
        private readonly HashSet<Vector2> _debugVisiblePoints = new();
        private float _lastObserverZ;
        
        // Position tracking for movement-based updates
        private Vector2 _lastDebugGridCenter = new Vector2(float.MinValue, float.MinValue);
        private const float MOVEMENT_THRESHOLD = 5.0f; // Only update if player moved 5+ units

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

        /// <summary>
        /// Smart render handler with automatic terrain refresh
        /// </summary>
        private void HandleRender(RenderEvent evt)
        {
            try
            {
                if (!AreWeThereYet.Instance.Settings.Debug.EnableRendering) return;
                if (!AreWeThereYet.Instance.Settings.Debug.ShowTerrainDebug) return;

                // CRITICAL FIX: Always check for terrain refresh, not just during line-of-sight checks
                RefreshTerrainData();

                if (_terrainData == null) return;

                var currentPlayerPos = _gameController.Player.GridPosNum;
                
                // TERRAIN GRID: Update only when player moves (heavy operation)
                var playerMoved = Vector2.Distance(_lastDebugGridCenter, currentPlayerPos) >= MOVEMENT_THRESHOLD;
                if (playerMoved || _lastDebugGridCenter.Equals(new Vector2(float.MinValue, float.MinValue)))
                {
                    UpdateDebugGrid(currentPlayerPos);
                }

                // CURSOR RAYS: Always update (lightweight, depends on mouse movement)
                var cursorWorldPos = AreWeThereYet.Instance.Settings.Debug.Raycast.CastRayToWorldCursorPos?.Value == true
                    ? AreWeThereYet.Instance.GameController.IngameState.ServerData.WorldMousePositionNum.WorldToGrid()
                    : (Vector2?)null;

                UpdateCursorRays(currentPlayerPos, cursorWorldPos);

                // RENDER ALL: Always render existing data
                RenderTerrainGrid(evt);      // Renders cached terrain grid (stable when not moving)
                RenderCursorRays(evt);       // Renders current cursor ray (updates with mouse)
                RenderDebugInfo(evt);        // Renders current debug info (always fresh)
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"HandleRender failed: {ex.Message}");
            }
        }


        /// <summary>
        /// Lightweight cursor ray updates - clears old visible points first
        /// </summary>
        private void UpdateCursorRays(Vector2 playerPosition, Vector2? cursorPosition)
        {
            try
            {
                _cursorRays.Clear();
                
                // CRITICAL FIX: Clear old visible points before new raycast
                _debugVisiblePoints.Clear();
                
                // Always update cursor ray if enabled (mouse movement should be responsive)
                if (cursorPosition.HasValue && 
                    AreWeThereYet.Instance.Settings.Debug.Raycast.CastRayToWorldCursorPos?.Value == true &&
                    _terrainData != null)
                {
                    _lastCursorPosition = cursorPosition.Value;
                    var isVisible = HasLineOfSightInternal(playerPosition, cursorPosition.Value);
                    _cursorRays.Add((playerPosition, cursorPosition.Value, isVisible));
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"UpdateCursorRays failed: {ex.Message}");
                _cursorRays.Clear();
                _debugVisiblePoints.Clear(); // Clear on error too
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
        /// Enhanced terrain refresh with better timing and error handling
        /// </summary>
        private void RefreshTerrainData()
        {
            try
            {
                var timeSinceRefresh = (DateTime.Now - _lastTerrainRefresh).TotalMilliseconds;
                var refreshInterval = TerrainRefreshInterval;
                
                if (timeSinceRefresh >= refreshInterval)
                {
                    var oldDataExists = _terrainData != null;
                    
                    UpdateTerrainData();
                    
                    // CRITICAL FIX: Always update timer, even if update failed
                    _lastTerrainRefresh = DateTime.Now;
                    
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        var status = _terrainData != null ? "SUCCESS" : "FAILED";
                        AreWeThereYet.Instance.LogMessage($"LineOfSight: Terrain refresh {status} at {DateTime.Now:HH:mm:ss.fff} (interval: {refreshInterval}ms)");
                    }
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"RefreshTerrainData failed: {ex.Message}");
                
                // CRITICAL FIX: Still update timer even on exception to prevent endless retries
                _lastTerrainRefresh = DateTime.Now;
            }
        }


        /// <summary>
        /// Enhanced terrain data loading - usin new "per-frame cached terrain data"
        /// </summary>
        private void UpdateTerrainData()
        {
            try
            {
                if (_gameController?.IngameState?.Data == null)
                {
                    lock (_terrainDataLock) // Lock before writing
                    {
                        _terrainData = null;
                    }
                    return;
                }

                var walkableData = _gameController.IngameState.Data.RawFramePathfindingData;
                var targetingData = _gameController.IngameState.Data.RawFrameTerrainTargetingData;

                if (walkableData == null || targetingData == null || walkableData.Length == 0)
                {
                    lock (_terrainDataLock) // Lock before writing
                    {
                        _terrainData = null;
                    }
                    return;
                }

                // Create the new data outside the lock to minimize lock time
                var newTerrainData = new int[walkableData.Length][];
                for (var y = 0; y < walkableData.Length; y++)
                {
                    newTerrainData[y] = new int[walkableData[y].Length];
                    for (var x = 0; x < walkableData[y].Length; x++)
                    {
                        var walkable = walkableData[y][x];
                        var targeting = targetingData[y][x];
                        newTerrainData[y][x] = CombineTerrainLayers(walkable, targeting);
                    }
                }

                // Lock only for the final, quick assignment
                lock (_terrainDataLock)
                {
                    _terrainData = newTerrainData;
                }

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"LineOfSight: Combined terrain data - Grid: {walkableData[0].Length}x{walkableData.Length}");
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"LineOfSight: Failed to update terrain data: {ex.Message}");
                lock (_terrainDataLock) // Lock before writing on error
                {
                    _terrainData = null;
                }
            }
        }


        /// <summary>
        /// Terrain combination logic for real-time door detection
        /// Terrain value system that works with real-time updates:
        /// 0 = Impassable (walls, void, closed doors) - blocks everything
        /// 2 = Dashable/teleportable (can shoot through, can dash through)  
        /// 5 = Fully walkable (open areas, open doors)
        /// </summary>
        private int CombineTerrainLayers(int walkableValue, int targetingValue)
        {
            // WALKABLE LAYER: 5,4,3,2,1 = walkable levels, 0 = blocked
            // TARGETING LAYER: 5 = clear line of sight, 4,3,2,1 = blocked by threshold, 0 = fully blocked
            
            var threshold = AreWeThereYet.Instance.Settings.AutoPilot.Dash.TerrainValueForCollision.Value;

            if (walkableValue >= 1 && targetingValue >= threshold)
            {
                // Can walk AND clear line of sight = fully accessible
                return 5;
            }
            else if (walkableValue == 0 && targetingValue >= threshold)
            {
                // Can't walk BUT can dash/shoot through = dashable obstacle
                return 2;
            }
            else
            {
                // Everything else = completely impassable
                return 0; // Fully blocked
            }
        }

        /// <summary>
        /// Main line-of-sight check - refresh now handled by render loop
        /// </summary>
        public bool HasLineOfSight(Vector2 start, Vector2 end)
        {
            lock (_terrainDataLock)
            {
                try
                {
                    // Enhanced validation
                    if (_terrainData == null || _terrainData.Length == 0)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            AreWeThereYet.Instance.LogMessage("LineOfSight: Terrain data not available, returning true as fallback.");
                        return true; // Conservative fallback
                    }

                    var isVisible = HasLineOfSightInternal(start, end);
                    _debugRays.Add((start, end, isVisible));

                    return isVisible;
                }
                catch (Exception ex)
                {
                    AreWeThereYet.Instance.LogError($"[FATAL] HasLineOfSight CRASHED. Start: {start}, End: {end}. Terrain Data Rows: {(_terrainData?.Length ?? -1)}. Exception: {ex.Message}", 10);
                    return true; // Conservative fallback on crash
                }
            }
        }


        /// <summary>
        /// TraceMyRay-style efficient DDA line-of-sight algorithm
        /// </summary>
        private bool HasLineOfSightInternal(Vector2 start, Vector2 end)
        {
            if (_terrainData == null) return false;
            
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
        /// Configurable terrain passability check
        /// </summary>
        private bool IsTerrainPassable(Vector2 pos)
        {
            var terrainValue = GetTerrainValue(pos);
            var threshold = AreWeThereYet.Instance.Settings.AutoPilot.Dash.TerrainValueForCollision.Value;
            if (terrainValue < 0) return false; // Out of bounds

            // TraceMyRay logic: terrain values <= threshold block line of sight
            return terrainValue >= threshold;
        }


        /// <summary>
        /// Smart debug grid - only updates when player actually moves AND clears cursor traces
        /// </summary>
        private void UpdateDebugGrid(Vector2 center)
        {
            // Check if player moved enough to warrant a grid update
            var distanceMoved = Vector2.Distance(_lastDebugGridCenter, center);
            
            if (distanceMoved < MOVEMENT_THRESHOLD)
            {
                // Player hasn't moved enough - keep existing debug points
                return;
            }
            
            // Player moved significantly - update the grid
            _lastDebugGridCenter = center;
            _debugPoints.Clear();
            
            // CRITICAL FIX: Clear cursor raycast traces when terrain grid updates
            _debugVisiblePoints.Clear();
            
            var size = AreWeThereYet.Instance.Settings.Debug.Terrain.GridSize.Value;
            
            try
            {
                for (var y = -size; y <= size; y += 2) // Sample every 2nd point
                    for (var x = -size; x <= size; x += 2)
                    {
                        if (x * x + y * y > size * size) continue; // Circular pattern

                        var pos = new Vector2(center.X + x, center.Y + y);
                        var value = GetTerrainValue(pos);
                        if (value >= 0) _debugPoints.Add((pos, value));
                    }

                _lastObserverZ = _gameController.IngameState.Data.GetTerrainHeightAt(center);
                
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Debug grid updated - Player moved {distanceMoved:F1} units");
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"UpdateDebugGrid failed: {ex.Message}");
                // Don't clear on error - keep existing points visible
            }
        }


        /// <summary>
        /// Enhanced debug rendering with TraceMyRay-style color mapping
        /// </summary>
        private void RenderTerrainGrid(RenderEvent evt)
        {
            lock (_terrainDataLock)
            {
                if (_terrainData == null) return; // Null check inside lock

                foreach (var (pos, value) in _debugPoints)
                {
                    var z = AreWeThereYet.Instance.Settings.Debug.Raycast.DrawAtPlayerPlane?.Value == true
                        ? _lastObserverZ
                        : _gameController.IngameState.Data.GetTerrainHeightAt(pos);

                    var worldPos = new System.Numerics.Vector3(pos.GridToWorld(), z);
                    var screenPos = _gameController.IngameState.Camera.WorldToScreen(worldPos);

                    SharpDX.Color color;
                    if (_debugVisiblePoints.Contains(pos))
                    {
                        color = SharpDX.Color.Cyan; // Line of sight trace
                    }
                    else
                    {
                        // Enhanced color mapping based on passability
                        color = value switch
                        {
                            0 => AreWeThereYet.Instance.Settings.Debug.Terrain.Colors.Tile0.Value,
                            1 => AreWeThereYet.Instance.Settings.Debug.Terrain.Colors.Tile1.Value,
                            2 => AreWeThereYet.Instance.Settings.Debug.Terrain.Colors.Tile2.Value,
                            3 => AreWeThereYet.Instance.Settings.Debug.Terrain.Colors.Tile3.Value,
                            4 => AreWeThereYet.Instance.Settings.Debug.Terrain.Colors.Tile4.Value,
                            5 => AreWeThereYet.Instance.Settings.Debug.Terrain.Colors.Tile5.Value,
                            _ => AreWeThereYet.Instance.Settings.Debug.Terrain.Colors.TileUnknown.Value
                        };
                    }

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
        /// Enhanced debug information rendering with better timer display
        /// </summary> 
        private void RenderDebugInfo(RenderEvent evt)
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                var timeSinceRefresh = (DateTime.Now - _lastTerrainRefresh).TotalMilliseconds;
                var refreshInterval = TerrainRefreshInterval;
                
                // Better status display
                var refreshStatus = timeSinceRefresh >= refreshInterval ? "DUE" : "OK";
                var dataStatus = _terrainData != null ? "LOADED" : "NULL";

                var threshold = AreWeThereYet.Instance.Settings.AutoPilot.Dash.TerrainValueForCollision.Value;
                
                evt.Graphics.DrawText(
                    $"Terrain: Manual Memory Reading (Real-time) | Refresh: {timeSinceRefresh:F0}ms ago ({refreshStatus}) | Threshold: {threshold} | Data: {dataStatus}",
                    new Vector2(10, 200),
                    SharpDX.Color.White
                );
                
                evt.Graphics.DrawText(
                    $"Terrain Data: {_terrainData?.Length ?? 0} rows (LayerMelee + LayerRanged) | Interval: {refreshInterval}ms",
                    new Vector2(10, 220),
                    SharpDX.Color.White
                );

                // Cursor ray debug info
                if (AreWeThereYet.Instance.Settings.Debug.Raycast.CastRayToWorldCursorPos?.Value == true && _cursorRays.Count > 0)
                {
                    var cursorRayStatus = _cursorRays[0].IsVisible ? "CLEAR" : "BLOCKED";
                    var cursorRayColor = _cursorRays[0].IsVisible ? SharpDX.Color.Green : SharpDX.Color.Red;
                    
                    evt.Graphics.DrawText(
                        $"Cursor Ray: {cursorRayStatus} | Position: ({_lastCursorPosition.X:F1}, {_lastCursorPosition.Y:F1}) | Traces: {_debugVisiblePoints.Count}",
                        new Vector2(10, 240),
                        cursorRayColor
                    );
                }
            }
        }

        private bool IsInBounds(int x, int y)
        {
            // Check against actual terrain data dimensions, not area dimensions
            if (_terrainData == null || _terrainData.Length == 0) 
                return false;
                
            return x >= 0 && y >= 0 && y < _terrainData.Length && x < _terrainData[y].Length;
        }


        private int GetTerrainValue(Vector2 position)
        {
            try
            {
                var x = (int)position.X;
                var y = (int)position.Y;

                // Enhanced bounds checking with null safety
                if (_terrainData == null || _terrainData.Length == 0)
                    return -1;
                    
                if (y < 0 || y >= _terrainData.Length)
                    return -1;
                    
                if (_terrainData[y] == null || x < 0 || x >= _terrainData[y].Length)
                    return -1;

                return _terrainData[y][x];
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"GetTerrainValue failed at ({position.X:F1}, {position.Y:F1}): {ex.Message}");
                return -1;
            }
        }

        public void Clear()
        {
            lock (_terrainDataLock)
            {
                _terrainData = null;
            }
            _debugPoints.Clear();
            _debugRays.Clear();
            _debugVisiblePoints.Clear();
            _lastTerrainRefresh = DateTime.MinValue;
        }

    }
}
