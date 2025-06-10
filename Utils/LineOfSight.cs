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
        private const int TARGET_LAYER_VALUE = 4;

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

            if (_terrainData == null)
            {
                return;
            }

            UpdateDebugGrid(_gameController.Player.GridPosNum);

            foreach (var (pos, value) in _debugPoints)
            {
                var worldPos = new Vector3(pos.GridToWorld(), _lastObserverZ);
                var screenPos = _gameController.IngameState.Camera.WorldToScreen(worldPos);

                SharpDX.Color color; // Change type to SharpDX.Color
                if (_debugVisiblePoints.Contains(pos))
                {
                    color = SharpDX.Color.Yellow; // Use SharpDX.Color
                }
                else
                {
                    color = value switch
                    {
                        0 => new SharpDX.Color(0, 128, 0, 128),    // Walkable
                        1 => new SharpDX.Color(0, 0, 128, 128),    // Low obstacle
                        2 => new SharpDX.Color(255, 165, 0, 128),  // Medium obstacle
                        3 => new SharpDX.Color(255, 0, 0, 128),    // High obstacle
                        4 => new SharpDX.Color(128, 0, 128, 128),  // Blocking
                        5 => new SharpDX.Color(0, 0, 0, 128),      // Special
                        _ => new SharpDX.Color(128, 128, 128, 128) // Unknown
                    };
                }

                evt.Graphics.DrawText(
                    value.ToString(),
                    screenPos,
                    color,
                    FontAlign.Center
                );
            }
        }
        
        private void HandleAreaChange(AreaChangeEvent evt)
        {
            _areaDimensions = _gameController.IngameState.Data.AreaDimensions;
            var rawData = _gameController.IngameState.Data.RawTerrainTargetingData;

            _terrainData = new int[rawData.Length][];
            for (var y = 0; y < rawData.Length; y++)
            {
                _terrainData[y] = new int[rawData[y].Length];
                Array.Copy(rawData[y], _terrainData[y], rawData[y].Length);
            }

            UpdateDebugGrid(_gameController.Player.GridPosNum);
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

            if (dx == 0)
                return CheckVerticalLine(startX, startY, endY);
            if (dy == 0)
                return CheckHorizontalLine(startY, startX, endX);

            return CheckDiagonalLine(start, end, dx, dy);
        }

        private bool CheckVerticalLine(int x, int startY, int endY)
        {
            var step = Math.Sign(endY - startY);
            var y = startY;

            while (y != endY)
            {
                y += step;
                var pos = new Vector2(x, y);
                var terrainValue = GetTerrainValue(pos);
                _debugVisiblePoints.Add(pos);

                if (terrainValue < TARGET_LAYER_VALUE) continue;
                if (terrainValue <= TARGET_LAYER_VALUE) return false;
            }

            return true;
        }

        private bool CheckHorizontalLine(int y, int startX, int endX)
        {
            var step = Math.Sign(endX - startX);
            var x = startX;

            while (x != endX)
            {
                x += step;
                var pos = new Vector2(x, y);
                var terrainValue = GetTerrainValue(pos);
                _debugVisiblePoints.Add(pos);

                if (terrainValue < TARGET_LAYER_VALUE) continue;
                if (terrainValue <= TARGET_LAYER_VALUE) return false;
            }

            return true;
        }

        private bool CheckDiagonalLine(Vector2 start, Vector2 end, float dx, float dy)
        {
            var x = (int)start.X;
            var y = (int)start.Y;
            var stepX = Math.Sign(end.X - start.X);
            var stepY = Math.Sign(end.Y - start.Y);

            if (dx >= dy)
            {
                var deltaError = dy / dx;
                var error = 0.0f;

                while (x != (int)end.X)
                {
                    x += stepX;
                    error += deltaError;

                    if (error >= 0.5f)
                    {
                        y += stepY;
                        error -= 1.0f;
                    }

                    var pos = new Vector2(x, y);
                    var terrainValue = GetTerrainValue(pos);
                    _debugVisiblePoints.Add(pos);

                    if (terrainValue < TARGET_LAYER_VALUE) continue;
                    if (terrainValue <= TARGET_LAYER_VALUE) return false;
                }
            }
            else
            {
                var deltaError = dx / dy;
                var error = 0.0f;

                while (y != (int)end.Y)
                {
                    y += stepY;
                    error += deltaError;

                    if (error >= 0.5f)
                    {
                        x += stepX;
                        error -= 1.0f;
                    }

                    var pos = new Vector2(x, y);
                    var terrainValue = GetTerrainValue(pos);
                    _debugVisiblePoints.Add(pos);

                    if (terrainValue < TARGET_LAYER_VALUE) continue;
                    if (terrainValue <= TARGET_LAYER_VALUE) return false;
                }
            }

            return true;
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
        }
    }
}