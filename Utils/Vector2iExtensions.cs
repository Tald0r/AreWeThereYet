using System;
using GameOffsets.Native;
using SharpDX;

namespace AreWeThereYet.Utils
{
    public static class Vector2iExtensions
    {
        public static float DistanceF(this Vector2i a, Vector2i b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public static int Distance(this Vector2i a, Vector2i b)
        {
            var dx = Math.Abs(a.X - b.X);
            var dy = Math.Abs(a.Y - b.Y);
            return Math.Max(dx, dy); // Chebyshev distance for 8-directional movement
        }

        public static Vector2i Truncate(this Vector2 v)
        {
            return new Vector2i((int)v.X, (int)v.Y);
        }

        public static Vector2i Truncate(this System.Numerics.Vector2 v)
        {
            return new Vector2i((int)v.X, (int)v.Y);
        }

        public static Vector3 GridToWorld(this Vector2i gridPos, float z = 0)
        {
            const float GridToWorldMultiplier = 250f / 23f; // From Radar constants
            return new Vector3(gridPos.X * GridToWorldMultiplier, gridPos.Y * GridToWorldMultiplier, z);
        }

        public static Vector2i WorldToGridInt(this Vector3 worldPos)
        {
            const float GridToWorldMultiplier = 250f / 23f;
            return new Vector2i((int)(worldPos.X / GridToWorldMultiplier), (int)(worldPos.Y / GridToWorldMultiplier));
        }

        public static System.Numerics.Vector2 ToNumerics(this Vector2 sharpDxVector)
        {
            return new System.Numerics.Vector2(sharpDxVector.X, sharpDxVector.Y);
        }

        public static Vector2 ToSharpDx(this System.Numerics.Vector2 numericsVector)
        {
            return new Vector2(numericsVector.X, numericsVector.Y);
        }

        public static Vector2i GridToVector2i(this Vector2 gridPos)
        {
            return new Vector2i((int)gridPos.X, (int)gridPos.Y);
        }
    }
}
