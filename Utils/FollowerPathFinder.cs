using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GameOffsets.Native;

namespace AreWeThereYet.Utils
{
    public class FollowerPathFinder
    {
        private readonly ConcurrentDictionary<Vector2i, Dictionary<Vector2i, float>> _exactDistanceField = new();
        private readonly ConcurrentDictionary<Vector2i, byte[][]> _directionField = new();
        private readonly Vector2i _gridDimensions;
        private readonly LineOfSight _lineOfSight;
        private readonly PathCache _pathCache;

        // Performance monitoring
        private int _pathfindingCalls = 0;
        private double _totalPathfindingTime = 0;
        private DateTime _lastPerformanceReport = DateTime.Now;

        // Costs as constants for easy tuning
        private const float WALK_COST = 1.0f;
        private const float DIAGONAL_WALK_COST = 1.414f;
        private const float DASH_COST = 30.0f; // High cost makes it a last resort

        // Direction offsets for 8-directional movement
        private static readonly List<Vector2i> NeighborOffsets = new()
        {
            new Vector2i(0, 1),   // North
            new Vector2i(1, 1),   // Northeast
            new Vector2i(1, 0),   // East
            new Vector2i(1, -1),  // Southeast
            new Vector2i(0, -1),  // South
            new Vector2i(-1, -1), // Southwest
            new Vector2i(-1, 0),  // West
            new Vector2i(-1, 1),  // Northwest
        };

        public FollowerPathFinder(LineOfSight lineOfSight, Vector2i gridDimensions)
        {
            _lineOfSight = lineOfSight;
            _gridDimensions = gridDimensions;
            _pathCache = new PathCache(150, TimeSpan.FromMinutes(3));
        }

        private static IEnumerable<Vector2i> GetNeighbors(Vector2i tile)
        {
            return NeighborOffsets.Select(offset => tile + offset);
        }

        private bool IsValidPathingTile(Vector2i tile)
        {
            // This now queries the raw terrain value directly from LineOfSight.
            var terrainValue = _lineOfSight.GetTerrainValue(new System.Numerics.Vector2(tile.X, tile.Y));
            
            // A tile is considered "walkable" for pathfinding if it's not a solid wall (0) or out of bounds (-1).
            // The cost of walking on it is handled by GetMovementCost.
            return terrainValue != 0 && terrainValue != -1;
        }

        private float GetMovementCost(Vector2i from, Vector2i to)
        {
            // This is the core of the new logic. It assigns a cost based on the terrain type.
            var terrainValue = _lineOfSight.GetTerrainValue(new System.Numerics.Vector2(to.X, to.Y));

            switch (terrainValue)
            {
                case 5: // Open walkable space
                case 1: // Basic walkable terrain
                    var dx = Math.Abs(to.X - from.X);
                    var dy = Math.Abs(to.Y - from.Y);
                    // Return standard cost for walking, with a penalty for diagonals.
                    return (dx == 1 && dy == 1) ? DIAGONAL_WALK_COST : WALK_COST;

                case 2: // Dashable objects (yellow tiles)
                        // Return the high dash cost, but only if dashing is enabled. Otherwise, it's impassable.
                    return AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled?.Value == true
                        ? DASH_COST
                        : float.PositiveInfinity;

                default: // Includes impassable walls (0) and any other unknown tile values.
                    return float.PositiveInfinity;
            }
        }


        public List<Vector2i> FindPath(Vector2i start, Vector2i target)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Check cache first
                if (_pathCache.TryGetPath(start, target, out var cachedPath))
                {
                    return ValidateCachedPath(cachedPath, start, target);
                }

                // Check if we have a cached direction field for this target
                if (_directionField.TryGetValue(target, out var directionField))
                {
                    var pathFromDirection = FindPathUsingDirectionField(start, target, directionField);
                    if (pathFromDirection != null)
                    {
                        _pathCache.CachePath(start, target, pathFromDirection);
                        return pathFromDirection;
                    }
                }

                // Check if we have exact distance data for this target
                if (_exactDistanceField.TryGetValue(target, out var exactDistances))
                {
                    var pathFromDistance = FindPathUsingExactDistances(start, target, exactDistances);
                    if (pathFromDistance != null)
                    {
                        _pathCache.CachePath(start, target, pathFromDistance);
                        return pathFromDistance;
                    }
                }

                // Generate new pathfinding data
                var newPath = GenerateNewPath(start, target);
                if (newPath != null && newPath.Count > 0)
                {
                    _pathCache.CachePath(start, target, newPath);
                }

                return newPath;
            }
            catch (Exception ex)
            {
                PluginLog.Log($"Pathfinding failed: {ex.Message}", LogLevel.Error);
                return null;
            }
            finally
            {
                stopwatch.Stop();
                _pathfindingCalls++;
                _totalPathfindingTime += stopwatch.Elapsed.TotalMilliseconds;

                // Report performance every 30 seconds
                if ((DateTime.Now - _lastPerformanceReport).TotalSeconds > 30)
                {
                    ReportPerformance();
                    _lastPerformanceReport = DateTime.Now;
                }
            }
        }

        private List<Vector2i> ValidateCachedPath(List<Vector2i> cachedPath, Vector2i start, Vector2i target)
        {
            // Quick validation - check if key waypoints are still walkable
            var checkPoints = new[] { 0, cachedPath.Count / 4, cachedPath.Count / 2, cachedPath.Count * 3 / 4, cachedPath.Count - 1 };
            
            foreach (var index in checkPoints)
            {
                if (index < cachedPath.Count && !IsValidPathingTile(cachedPath[index]))
                {
                    // Path is invalid, recalculate
                    return GenerateNewPath(start, target);
                }
            }
            
            return cachedPath;
        }

        private List<Vector2i> FindPathUsingDirectionField(Vector2i start, Vector2i target, byte[][] directionField)
        {
            if (directionField[start.Y][start.X] == 0)
                return null; // No path

            var path = new List<Vector2i>();
            var current = start;
            var maxSteps = _gridDimensions.X + _gridDimensions.Y; // Prevent infinite loops

            while (current != target && path.Count < maxSteps)
            {
                var directionIndex = directionField[current.Y][current.X];
                if (directionIndex == 0)
                    return null; // Path blocked

                var next = current + NeighborOffsets[directionIndex - 1];
                path.Add(next);
                current = next;
            }

            return path;
        }

        private List<Vector2i> FindPathUsingExactDistances(Vector2i start, Vector2i target, Dictionary<Vector2i, float> exactDistances)
        {
            if (!exactDistances.ContainsKey(start) || float.IsPositiveInfinity(exactDistances[start]))
                return null;

            var path = new List<Vector2i>();
            var current = start;
            var maxSteps = _gridDimensions.X + _gridDimensions.Y;

            while (current != target && path.Count < maxSteps)
            {
                var bestNeighbor = GetNeighbors(current)
                    .Where(IsValidPathingTile)
                    .MinBy(neighbor => exactDistances.GetValueOrDefault(neighbor, float.PositiveInfinity));

                if (bestNeighbor.Equals(default(Vector2i)) || 
                    float.IsPositiveInfinity(exactDistances.GetValueOrDefault(bestNeighbor, float.PositiveInfinity)))
                    return null;

                path.Add(bestNeighbor);
                current = bestNeighbor;
            }

            return path;
        }

        private List<Vector2i> GenerateNewPath(Vector2i start, Vector2i target)
        {
            if (!IsValidPathingTile(start) || !IsValidPathingTile(target))
                return null;

            // Run Dijkstra's algorithm from target (like Radar does)
            var exactDistances = RunDijkstra(target);
            if (exactDistances == null || !exactDistances.ContainsKey(start))
                return null;

            // Check settings to decide whether to generate the direction field
            if (AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.GenerateDirectionField.Value)
            {
                // This logic is adapted from the Radar project to optimize memory.
                // It converts the large exact distance map into a small, fast direction map.
                var directionGrid = new byte[_gridDimensions.Y][];
                for (int y = 0; y < _gridDimensions.Y; y++)
                {
                    directionGrid[y] = new byte[_gridDimensions.X];
                    for (int x = 0; x < _gridDimensions.X; x++)
                    {
                        var currentPos = new Vector2i(x, y);
                        if (!exactDistances.ContainsKey(currentPos))
                        {
                            directionGrid[y][x] = 0; // No path from here
                            continue;
                        }

                        // Find the neighbor that is closest to the target
                        var bestNeighbor = GetNeighbors(currentPos)
                            .Where(IsValidPathingTile)
                            .MinBy(neighbor => exactDistances.GetValueOrDefault(neighbor, float.PositiveInfinity));

                        if (bestNeighbor.Equals(default(Vector2i)))
                        {
                            directionGrid[y][x] = 0; // No valid neighbors
                        }
                        else
                        {
                            // Store the direction to the best neighbor as a byte
                            var direction = bestNeighbor - currentPos;
                            var directionIndex = NeighborOffsets.IndexOf(direction);
                            if (directionIndex != -1)
                            {
                                directionGrid[y][x] = (byte)(directionIndex + 1);
                            }
                            else
                            {
                                directionGrid[y][x] = 0; // Should not happen, but as a safeguard
                            }
                        }
                    }
                }
                // Store the generated direction field for future path requests
                _directionField[target] = directionGrid;
                
                // IMPORTANT: Remove the large exact distance field to save memory
                _exactDistanceField.TryRemove(target, out _);
            }
            else
            {
                // If the setting is off, just store the exact distances (high memory usage)
                _exactDistanceField[target] = exactDistances;
            }

            // Use the exact distances to find the path for the *current* request.
            // This works whether the setting is on or off because we still have the `exactDistances`
            // variable available from the initial Dijkstra run.
            return FindPathUsingExactDistances(start, target, exactDistances);
        }

        private Dictionary<Vector2i, float> RunDijkstra(Vector2i target)
        {
            var distances = new Dictionary<Vector2i, float> { [target] = 0 };
            var visited = new HashSet<Vector2i>();
            var queue = new BinaryHeap<float, Vector2i>();
            queue.Add(0, target);

            var processedNodes = 0;
            var maxNodesToProcess = _gridDimensions.X * _gridDimensions.Y / 10; // Limit processing

            while (queue.TryRemoveTop(out var current) && processedNodes < maxNodesToProcess)
            {
                var currentPos = current.Value;
                var currentDistance = current.Key;

                if (visited.Contains(currentPos))
                    continue;

                visited.Add(currentPos);
                processedNodes++;

                foreach (var neighbor in GetNeighbors(currentPos))
                {
                    if (!IsValidPathingTile(neighbor) || visited.Contains(neighbor))
                        continue;

                    // Get a variable cost instead of a fixed one.
                    var newDistance = currentDistance + GetMovementCost(currentPos, neighbor);
                    
                    if (newDistance >= float.PositiveInfinity) // Don't consider impassable paths
                        continue;

                    if (!distances.ContainsKey(neighbor) || newDistance < distances[neighbor])
                    {
                        distances[neighbor] = newDistance;
                        queue.Add(newDistance, neighbor);
                    }
                }
            }

            return distances;
        }

        private void ReportPerformance()
        {
            if (_pathfindingCalls > 0)
            {
                var avgTime = _totalPathfindingTime / _pathfindingCalls;
                AreWeThereYet.Instance.LogMessage(
                    $"Pathfinding Performance: {_pathfindingCalls} calls, avg {avgTime:F2}ms per call");
            }
            
            // Reset counters
            _pathfindingCalls = 0;
            _totalPathfindingTime = 0;
        }

        public void ClearCache()
        {
            _exactDistanceField.Clear();
            _directionField.Clear();
            _pathCache.Clear();
        }

        public PathfindingStats GetStats()
        {
            return new PathfindingStats
            {
                CacheHitRate = _pathCache.Count,
                ExactDistanceFields = _exactDistanceField.Count,
                DirectionFields = _directionField.Count,
                AveragePathfindingTime = _pathfindingCalls > 0 ? _totalPathfindingTime / _pathfindingCalls : 0
            };
        }
    }

    public class PathfindingStats
    {
        public int CacheHitRate { get; set; }
        public int ExactDistanceFields { get; set; }
        public int DirectionFields { get; set; }
        public double AveragePathfindingTime { get; set; }
    }
}
