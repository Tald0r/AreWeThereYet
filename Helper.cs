using System;
using SharpDX;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AreWeThereYet.Utils;

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

    internal static Vector2 WorldToValidScreenPosition(Vector3 worldPos)
    {
        var windowRect = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle();
        var screenPos = Camera.WorldToScreen(worldPos);
        var result = screenPos + windowRect.Location;

        var edgeBounds = 50;
        if (!windowRect.Intersects(new RectangleF(result.X, result.Y, edgeBounds, edgeBounds)))
        {
            if (result.X < windowRect.TopLeft.X) result.X = windowRect.TopLeft.X + edgeBounds;
            if (result.Y < windowRect.TopLeft.Y) result.Y = windowRect.TopLeft.Y + edgeBounds;
            if (result.X > windowRect.BottomRight.X) result.X = windowRect.BottomRight.X - edgeBounds;
            if (result.Y > windowRect.BottomRight.Y) result.Y = windowRect.BottomRight.Y - edgeBounds;
        }
        return result;
    }

    /// <summary>
    /// Helper method to draw text with background rectangle (with RenderEvent)
    /// </summary>
    internal static void DrawTextWithBackground(RenderEvent evt, string text, Vector2 position, SharpDX.Color textColor, SharpDX.Color backgroundColor, int padding)
    {
        // Estimate text size (approximate)
        var textWidth = text.Length * 7; // Rough estimation - 7 pixels per character
        var textHeight = 16; // Standard text height

        // Draw background rectangle
        var backgroundRect = new RectangleF(
            position.X - padding,
            position.Y - padding,
            textWidth + (padding * 2),
            textHeight + (padding * 2)
        );

        evt.Graphics.DrawBox(backgroundRect, backgroundColor);

        // Draw border (optional)
        evt.Graphics.DrawFrame(backgroundRect, new SharpDX.Color(255, 255, 255, 100), 1);

        // Draw text on top
        evt.Graphics.DrawText(text, position, textColor, FontAlign.Left);
    }

    /// <summary>
    /// Helper method to draw text with background rectangle (direct Graphics access for AutoPilot)
    /// </summary>
    internal static void DrawTextWithBackground(string text, System.Numerics.Vector2 position, SharpDX.Color textColor, SharpDX.Color backgroundColor, int padding)
    {
        // Convert System.Numerics.Vector2 to Vector2 for rectangle
        var pos = new Vector2(position.X, position.Y);
        
        // Estimate text size
        var textWidth = text.Length * 7;
        var textHeight = 16;
        
        // Draw background rectangle
        var backgroundRect = new RectangleF(
            pos.X - padding, 
            pos.Y - padding, 
            textWidth + (padding * 2), 
            textHeight + (padding * 2)
        );
        
        AreWeThereYet.Instance.Graphics.DrawBox(backgroundRect, backgroundColor);
        AreWeThereYet.Instance.Graphics.DrawFrame(backgroundRect, new SharpDX.Color(255, 255, 255, 100), 1);
        
        // Draw text
        AreWeThereYet.Instance.Graphics.DrawText(text, position, textColor);
    }
}
