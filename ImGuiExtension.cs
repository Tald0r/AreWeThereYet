// UI/ImGuiExtension.cs
using System;
using System.Windows.Forms;
using ImGuiNET;

namespace AreWeThereYet
{
    internal static class ImGuiExtension
    {
        public static bool Checkbox(string label, bool value)
        {
            var v = value;
            if (ImGui.Checkbox(label, ref v)) return v;
            return value;
        }

        public static string InputText(string label, string text, int bufSize)
        {
            var buf = new byte[bufSize];
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, buf.Length - 1));
            if (ImGui.InputText(label, buf, (uint)buf.Length))
                return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
            return text;
        }

        public static int SliderInt(string label, int val, int min, int max)
        {
            var v = val;
            if (ImGui.SliderInt(label, ref v, min, max)) return v;
            return val;
        }
    }
}
