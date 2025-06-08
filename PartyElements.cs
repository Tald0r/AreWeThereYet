// Core/PartyElements.cs
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Elements;

namespace AreWeThereYet
{
    public class PartyElementWindow
    {
        public string PlayerName { get; set; }
        public string ZoneName { get; set; }
        public Element TpButton { get; set; }
    }

    public static class PartyElements
    {
        public static IEnumerable<PartyElementWindow> GetPlayerInfoElementList()
        {
            var panel = AreWeThereYet.Instance.GameController?.IngameState?.IngameUi?.PartyPanel;
            if (panel == null) yield break;

            foreach (var child in panel.Children)
            {
                var nameElem = child.Children.FirstOrDefault(x => !string.IsNullOrEmpty(x.Text));
                var zoneElem = child.Children.Skip(1).FirstOrDefault(x => !string.IsNullOrEmpty(x.Text));
                var tpBtn = child.Children.FirstOrDefault(x => x.Path.Contains("TeleportButton"));
                if (nameElem != null)
                    yield return new PartyElementWindow
                    {
                        PlayerName = nameElem.Text,
                        ZoneName = zoneElem?.Text,
                        TpButton = tpBtn
                    };
            }
        }
    }
}
