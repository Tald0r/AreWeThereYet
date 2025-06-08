// Core/Helper.cs
using SharpDX;
using ExileCore.PoEMemory.MemoryObjects;

namespace AreWeThereYet
{
    public static class Helper
    {
        private static Camera Cam => AreWeThereYet.Instance.GameController.Game.IngameState.Camera;

        public static Vector2 WorldToValidScreenPosition(Vector3 worldPos)
        {
            var wr = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle();
            var sp = Cam.WorldToScreen(worldPos);
            var p = sp + wr.Location;
            const float buffer = 50;
            if (!wr.Intersects(new RectangleF(p.X, p.Y, buffer, buffer)))
            {
                p.X = MathUtil.Clamp(p.X, wr.Left + buffer, wr.Right - buffer);
                p.Y = MathUtil.Clamp(p.Y, wr.Top + buffer, wr.Bottom - buffer);
            }
            return p;
        }

        public static float MoveTowards(float cur, float tar, float max)
        {
            var diff = tar - cur;
            if (Math.Abs(diff) <= max) return tar;
            return cur + Math.Sign(diff) * max;
        }
    }
}
