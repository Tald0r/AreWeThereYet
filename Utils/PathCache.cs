using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using GameOffsets.Native;

namespace AreWeThereYet.Utils
{
    public class PathCache
    {
        private readonly ConcurrentDictionary<PathCacheKey, CachedPath> _cache = new();
        private readonly int _maxCacheSize;
        private readonly TimeSpan _cacheExpiry;

        public PathCache(int maxCacheSize = 100, TimeSpan? cacheExpiry = null)
        {
            _maxCacheSize = maxCacheSize;
            _cacheExpiry = cacheExpiry ?? TimeSpan.FromMinutes(2);
        }

        public bool TryGetPath(Vector2i start, Vector2i target, out List<Vector2i> path)
        {
            var key = new PathCacheKey(start, target);
            
            if (_cache.TryGetValue(key, out var cachedPath) &&
                DateTime.Now - cachedPath.CreatedAt < _cacheExpiry)
            {
                cachedPath.UseCount++;
                cachedPath.LastUsed = DateTime.Now;
                path = new List<Vector2i>(cachedPath.Path);
                return true;
            }
            
            path = null;
            return false;
        }

        public void CachePath(Vector2i start, Vector2i target, List<Vector2i> path)
        {
            if (path == null || path.Count == 0)
                return;

            CleanupExpiredEntries();
            
            if (_cache.Count >= _maxCacheSize)
            {
                RemoveLeastUsedEntry();
            }
            
            var key = new PathCacheKey(start, target);
            _cache[key] = new CachedPath
            {
                Path = new List<Vector2i>(path),
                CreatedAt = DateTime.Now,
                LastUsed = DateTime.Now,
                UseCount = 1
            };
        }

        private void CleanupExpiredEntries()
        {
            var expiredKeys = new List<PathCacheKey>();
            var now = DateTime.Now;
            
            foreach (var kvp in _cache)
            {
                if (now - kvp.Value.CreatedAt > _cacheExpiry)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }
            
            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }
        }

        private void RemoveLeastUsedEntry()
        {
            PathCacheKey? leastUsedKey = null;
            var minScore = double.MaxValue;
            
            foreach (var kvp in _cache)
            {
                var timeSinceLastUse = (DateTime.Now - kvp.Value.LastUsed).TotalMinutes;
                var score = kvp.Value.UseCount / (1 + timeSinceLastUse);
                
                if (score < minScore)
                {
                    minScore = score;
                    leastUsedKey = kvp.Key;
                }
            }
            
            if (leastUsedKey.HasValue)
            {
                _cache.TryRemove(leastUsedKey.Value, out _);
            }
        }

        public void Clear()
        {
            _cache.Clear();
        }

        public int Count => _cache.Count;

        private struct PathCacheKey : IEquatable<PathCacheKey>
        {
            public Vector2i Start { get; }
            public Vector2i Target { get; }

            public PathCacheKey(Vector2i start, Vector2i target)
            {
                Start = start;
                Target = target;
            }

            public bool Equals(PathCacheKey other)
            {
                return Start.Equals(other.Start) && Target.Equals(other.Target);
            }

            public override bool Equals(object obj)
            {
                return obj is PathCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Start, Target);
            }
        }

        private class CachedPath
        {
            public List<Vector2i> Path { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastUsed { get; set; }
            public int UseCount { get; set; }
        }
    }
}
