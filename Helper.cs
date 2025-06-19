using System;
using System.Numerics;
using SharpDX;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Helpers;

namespace AreWeThereYet;

public static class Helper
{
    internal static Random random = new Random();
    private static Camera Camera => AreWeThereYet.Instance.GameController.Game.IngameState.Camera;

    internal static float MoveTowards(float cur, float tar, float max)
    {
        if (Math.Abs(tar - cur) <= max)
            return tar;
        return cur + Math.Sign(tar - cur) * max;
    }

    internal static System.Numerics.Vector2 WorldToValidScreenPosition(System.Numerics.Vector3 worldPos)
    {
        // Convert to SharpDX types at the beginning to work with ExileCore's native functions.
        var sharpDxWorldPos = new SharpDX.Vector3(worldPos.X, worldPos.Y, worldPos.Z);
        var windowRect = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle();

        // Do all screen calculations using SharpDX, as intended by the framework.
        var screenPos = Camera.WorldToScreen(sharpDxWorldPos);
        var result = screenPos + windowRect.Location;

        var edgeBounds = 50;
        if (!windowRect.Intersects(new SharpDX.RectangleF(result.X, result.Y, edgeBounds, edgeBounds)))
        {
            if (result.X < windowRect.TopLeft.X) result.X = windowRect.TopLeft.X + edgeBounds;
            if (result.Y < windowRect.TopLeft.Y) result.Y = windowRect.TopLeft.Y + edgeBounds;
            if (result.X > windowRect.BottomRight.X) result.X = windowRect.BottomRight.X - edgeBounds;
            if (result.Y > windowRect.BottomRight.Y) result.Y = windowRect.BottomRight.Y - edgeBounds;
        }

        // Convert back to System.Numerics.Vector2 ONLY at the very end.
        return new System.Numerics.Vector2(result.X, result.Y);
    }

    public static System.Numerics.Vector2 ToNumerics(this SharpDX.Vector2 dxVector)
    {
        return new System.Numerics.Vector2(dxVector.X, dxVector.Y);
    }
}
