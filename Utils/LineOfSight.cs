using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using ExileCore;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using Graphics = ExileCore.Graphics;

namespace AreWeThereYet.Utils
{
    public class LineOfSight
    {
        private readonly GameController _gameController;
        private int[][] _terrainData;
        private Vector2 _areaDimensions;

        // Separate storage for static terrain vs dynamic objects
        private byte[] _staticTerrainBytes;  // LayerMelee - refreshed only on area change
        private byte[] _dynamicObjectBytes;  // LayerRanged - refreshed periodically
        private DateTime _lastRangedRefresh = DateTime.MinValue;
        
        private int TargetLayerValue => AreWeThereYet.Instance.Settings.TargetLayerValue?.Value ?? 4;
        private int RangedRefreshInterval => AreWeThereYet.Instance.Settings.RangedRefreshInterval?.Value ?? 1000;

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

        private void HandleRender(RenderEvent evt)
        {
            if (!AreWeThereYet.Instance.Settings.EnableRendering) return;
            if (!AreWeThereYet.Instance.Settings.ShowTerrainDebug) return;

            if (_terrainData == null) return;

            // Check if we need to refresh ranged layer
            CheckAndRefreshRangedLayer();

            UpdateDebugGrid(_gameController.Player.GridPosNum);

            foreach (var (pos, value) in _debugPoints)
            {
                var worldPos = new Vector3(pos.GridToWorld(), _lastObserverZ);
                var screenPos = _gameController.IngameState.Camera.WorldToScreen(worldPos);

                SharpDX.Color color;
                if (_debugVisiblePoints.Contains(pos))
                {
                    color = SharpDX.Color.Yellow; // Line of sight trace
                }
                else
                {
                    // ENHANCED COLOR MAPPING for combined terrain
                    color = value switch
                    {
                        0 => new SharpDX.Color(255, 0, 0, 200),      // RED - Impassable walls/void
                        1 => new SharpDX.Color(100, 255, 100, 180),  // LIGHT GREEN - Basic walkable
                        2 => new SharpDX.Color(255, 165, 0, 200),    // ORANGE - Static objects (dashable)
                        3 => new SharpDX.Color(0, 0, 255, 180),      // BLUE - Reserved
                        4 => new SharpDX.Color(128, 0, 128, 180),    // PURPLE - Reserved  
                        5 => new SharpDX.Color(0, 200, 0, 160),      // DARK GREEN - Open walkable space
                        _ => new SharpDX.Color(128, 128, 128, 160)   // GRAY - Unknown
                    };
                }

                // Draw the terrain value number with colored background
                evt.Graphics.DrawText(
                    value.ToString(),
                    screenPos,
                    color,
                    FontAlign.Center
                );
                
                // Optional: Add small background circle for better visibility
                if (AreWeThereYet.Instance.Settings.ShowDetailedDebug?.Value == true)
                {
                    evt.Graphics.DrawEllipse(screenPos, new Vector2(8, 8), color, 2);
                }
            }
            
            // Debug info for refresh timing
            if (AreWeThereYet.Instance.Settings.ShowDetailedDebug?.Value == true)
            {
                var timeSinceRefresh = (DateTime.Now - _lastRangedRefresh).TotalMilliseconds;
                evt.Graphics.DrawText(
                    $"Ranged Refresh: {timeSinceRefresh:F0}ms ago",
                    new Vector2(10, 200),
                    SharpDX.Color.White
                );
            }
        }
        
        private void HandleAreaChange(AreaChangeEvent evt)
        {
            _areaDimensions = _gameController.IngameState.Data.AreaDimensions;
            
            var terrain = _gameController.IngameState.Data.Terrain;
            
            // Read static terrain data ONCE per area (LayerMelee)
            _staticTerrainBytes = _gameController.Memory.ReadBytes(terrain.LayerMelee.First, terrain.LayerMelee.Size);
            
            // Read dynamic object data and mark for periodic refresh (LayerRanged) 
            RefreshRangedLayer();
            
            // Combine layers and update terrain data
            CombineTerrainLayers();

            UpdateDebugGrid(_gameController.Player.GridPosNum);
        }

        private void CheckAndRefreshRangedLayer()
        {
            var timeSinceRefresh = (DateTime.Now - _lastRangedRefresh).TotalMilliseconds;
            
            if (timeSinceRefresh >= RangedRefreshInterval)
            {
                RefreshRangedLayer();
                CombineTerrainLayers();
            }
        }

        private void RefreshRangedLayer()
        {
            try
            {
                var terrain = _gameController.IngameState.Data.Terrain;
                _dynamicObjectBytes = _gameController.Memory.ReadBytes(terrain.LayerRanged.First, terrain.LayerRanged.Size);
                _lastRangedRefresh = DateTime.Now;
                
                // Debug logging
                if (AreWeThereYet.Instance.Settings.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"LineOfSight: Refreshed ranged layer at {_lastRangedRefresh:HH:mm:ss.fff}");
                }
            }
            catch (Exception ex)
            {
                AreWeThereYet.Instance.LogError($"LineOfSight: Failed to refresh ranged layer: {ex.Message}");
            }
        }

        private void CombineTerrainLayers()
        {
            if (_staticTerrainBytes == null || _dynamicObjectBytes == null) return;

            var terrain = _gameController.IngameState.Data.Terrain;
            var numCols = (int)(terrain.NumCols - 1) * 23;
            var numRows = (int)(terrain.NumRows - 1) * 23;
            if ((numCols & 1) > 0) numCols++;

            // Initialize combined terrain data
            _terrainData = new int[numRows][];
            
            for (var y = 0; y < numRows; y++)
            {
                _terrainData[y] = new int[numCols];
                var dataIndex = y * terrain.BytesPerRow;
                
                for (var x = 0; x < numCols; x += 2)
                {
                    // LAYER 1: Static terrain (LayerMelee) - refreshed only on area change
                    var meleeB = _staticTerrainBytes[dataIndex + (x >> 1)];
                    var melee1 = (meleeB & 0xf) > 0 ? 1 : 255;  // 1=walkable, 255=wall
                    var melee2 = (meleeB >> 4) > 0 ? 1 : 255;
                    
                    // LAYER 2: Dynamic objects (LayerRanged) - refreshed periodically
                    var rangedB = _dynamicObjectBytes[dataIndex + (x >> 1)];
                    var ranged1 = (rangedB & 0xf) > 3 ? 2 : 255;  // 2=dashable object, 255=blocked
                    var ranged2 = (rangedB >> 4) > 3 ? 2 : 255;
                    
                    // COMBINE LAYERS: Create unified terrain values
                    _terrainData[y][x] = CombineTerrainLayers(melee1, ranged1);
                    if (x + 1 < numCols)
                        _terrainData[y][x + 1] = CombineTerrainLayers(melee2, ranged2);
                }
            }
        }

        private int CombineTerrainLayers(int meleeValue, int rangedValue)
        {
            // Unified terrain value system:
            // 0 = Impassable wall/void
            // 1 = Walkable floor  
            // 2 = Static objects (doors, chests, decorations) - dashable
            // 3 = Reserved for future use
            // 4 = Reserved for future use  
            // 5 = Open walkable space (high confidence)

            if (meleeValue == 1)  // Basic terrain is walkable
            {
                return 5;  // Open walkable space
            }
            else if (meleeValue == 255)  // Basic terrain blocked
            {
                if (rangedValue == 2)  // But has static objects
                {
                    return 2;  // Static objects (dashable)
                }
                else
                {
                    return 0;  // Impassable wall
                }
            }

            return 1;  // Default walkable
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

        public bool HasLineOfSight(Vector2 start, Vector2 end)
        {
            if (_terrainData == null) return false;

            // Check if we need to refresh ranged layer before line-of-sight check
            CheckAndRefreshRangedLayer();

            // Update debug visualization
            _debugVisiblePoints.Clear();
            UpdateDebugGrid(start);

            var isVisible = HasLineOfSightInternal(start, end);
            _debugRays.Add((start, end, isVisible));

            return isVisible;
        }

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

            // Handle straight lines 
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

            // DDA algorithm for diagonal lines 
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

        private bool IsTerrainPassable(Vector2 pos)
        {
            var terrainValue = GetTerrainValue(pos);

            // ENHANCED LOGIC for combined terrain
            switch (terrainValue)
            {
                case 0:  // Impassable walls/void
                    return false;

                case 2:  // Static objects (doors, chests, decorations) - dashable
                         // Only passable if dash is enabled, otherwise block
                    return AreWeThereYet.Instance.Settings.autoPilotDashEnabled?.Value == true;

                case 1:  // Basic walkable terrain
                case 5:  // Open walkable space
                    return true;  // Always passable

                case 3:  // Reserved terrain type
                case 4:  // Reserved terrain type
                    return true;  // Conservative - assume passable for now

                default:
                    // Unknown terrain values - conservative approach
                    return terrainValue > 2;  // Block low values, allow high values
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
            _staticTerrainBytes = null;
            _dynamicObjectBytes = null;
            _debugPoints.Clear();
            _debugRays.Clear();
            _debugVisiblePoints.Clear();
            _lastRangedRefresh = DateTime.MinValue;
        }
    }
}
