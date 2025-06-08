// Core/AutoPilot.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared;
using SharpDX;

namespace AreWeThereYet
{
    public class AutoPilot
    {
        private Coroutine coroutine;
        private readonly Random rnd = new Random();
        private Vector3 lastTarget, lastPlayer;
        private Entity followTarget;
        private bool usedWp;
        private List<TaskNode> tasks = new List<TaskNode>();
        private byte[,] tiles;
        private int rows, cols;





        private PartyElementWindow GetLeaderParty()
        {
            return PartyElements.GetPlayerInfoElementList()
                .FirstOrDefault(p => string.Equals(p.PlayerName, AreWeThereYet.Instance.Settings.LeaderName.Value, StringComparison.OrdinalIgnoreCase));
        }

        private LabelOnGround GetBestPortal(PartyElementWindow leader)
        {
            var zone = AreWeThereYet.Instance.GameController.Area.CurrentArea.DisplayName;
            if (leader.ZoneName != zone && !AreWeThereYet.Instance.GameController.Area.CurrentArea.IsHideout)
                return null;
            var labels = AreWeThereYet.Instance.GameController.IngameState.IngameUi.ItemsOnGroundLabels
                .Where(x => x.IsVisible && x.ItemOnGround.Metadata.ToLower().Contains("portal"))
                .OrderBy(x => Vector3.Distance(lastTarget, x.ItemOnGround.Pos))
                .ToList();
            return labels.FirstOrDefault();
        }

        private ExileCore.PoEMemory.Elements.Element GetTeleportConfirmation()
        {
            try
            {
                var popUp = AreWeThereYet.Instance.GameController.IngameState.IngameUi.PopUpWindow;
                // The “Are you sure?” text sits at Children[0].Children[0].Children[0]
                var msg = popUp?.Children[0]?.Children[0]?.Children[0]?.Text;
                if (msg != null && msg.Contains("Are you sure you want to teleport"))
                    // The “Yes” button is at Children[0].Children[0].Children[3].Children[0]
                    return popUp.Children[0].Children[0].Children[3].Children[0];
            }
            catch { /* swallow NREs if UI isn’t fully constructed */ }
            return null;
        }

        public void AreaChange()
        {
            tasks.Clear();
            followTarget = null;
            lastTarget = lastPlayer = Vector3.Zero;
            usedWp = false;

            var tc = AreWeThereYet.Instance.GameController.IngameState.Data.Terrain;
            var melee = AreWeThereYet.Instance.GameController.Memory.ReadBytes(tc.LayerMelee.First, tc.LayerMelee.Size);
            cols = (int)(tc.NumCols - 1) * 23; rows = (int)(tc.NumRows - 1) * 23;
            if ((cols & 1) > 0) cols++;
            tiles = new byte[cols, rows];
            var idx = 0;
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < cols; x += 2)
                {
                    var b = melee[idx + (x >> 1)];
                    tiles[x, y]   = (byte)((b & 0xf) > 0 ? 1 : 255);
                    tiles[x+1, y] = (byte)((b >> 4) > 0 ? 1 : 255);
                }
                idx += tc.BytesPerRow;
            }
            
            terrainBytes = CoPilot.Instance.GameController.Memory.ReadBytes(terrain.LayerRanged.First, terrain.LayerRanged.Size);
            numCols = (int)(terrain.NumCols - 1) * 23;
            numRows = (int)(terrain.NumRows - 1) * 23;
            if ((numCols & 1) > 0)
                numCols++;
            dataIndex = 0;
            for (var y = 0; y < numRows; y++)
            {
                for (var x = 0; x < numCols; x += 2)
                {
                    var b = terrainBytes[dataIndex + (x >> 1)];

                    var current = tiles[x, y];
                    if(current == 255)
                        tiles[x, y] = (byte)((b & 0xf) > 3 ? 2 : 255);
                    current = tiles[x+1, y];
                    if (current == 255)
                        tiles[x + 1, y] = (byte)((b >> 4) > 3 ? 2 : 255);
                }
                dataIndex += terrain.BytesPerRow;
            }
        }
        public void StartCoroutine()
        {
            coroutine = new Coroutine(RunLogic(), AreWeThereYet.Instance, "AreWeThereYet");
            Core.ParallelRunner.Run(coroutine);
        }

        private Entity GetFollowEntity()
        {
            var name = AreWeThereYet.Instance.Settings.LeaderName.Value.ToLower();
            return AreWeThereYet.Instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]
                .FirstOrDefault(e => e.GetComponent<Player>()?.PlayerName.ToLower() == name);
        }

        private IEnumerator RunLogic()
        {
            while (true)
            {
                var s = AreWeThereYet.Instance.Settings;
                var ctl = AreWeThereYet.Instance.GameController;
                if (!s.AutoPilotEnabled.Value || ctl.IsLoading || !ctl.InGame || ctl.MenuWindow.IsOpened || ctl.LocalPlayer?.IsAlive != true)
                {
                    yield return new WaitTime(100);
                    continue;
                }

                followTarget = GetFollowEntity();
                var partyElem = GetLeaderParty();

                if (followTarget == null && partyElem != null && partyElem.ZoneName != ctl.Area.CurrentArea.DisplayName)
                {
                    var portal = GetBestPortal(partyElem);
                    if (portal != null)
                        tasks.Add(new TaskNode(portal, s.ClearDistance.Value, TaskNodeType.Transition));
                    else
                    {
                        // Teleport UI click
                        var tpBtn = partyElem.TpButton;
                        if (tpBtn != null)
                        {
                            var pos = tpBtn.GetClientRectCache().Center + ctl.Window.GetWindowRectangle().TopLeft;
                            yield return Mouse.SetCursorPosHuman(pos);
                            yield return new WaitTime(200);
                            yield return Mouse.LeftClick();
                            yield return new WaitTime(500);
                        }
                    }
                }
                else if (followTarget != null)
                {
                    var dist = Vector3.Distance(ctl.LocalPlayer.Pos, followTarget.Pos);
                    if (dist > s.ClearDistance.Value)
                    {
                        if (!tasks.Any())
                            tasks.Add(new TaskNode(followTarget.Pos, s.NodeDistance.Value));
                        else
                        {
                            var last = tasks.Last();
                            if (Vector3.Distance(last.WorldPosition, followTarget.Pos) >= s.NodeDistance.Value)
                                tasks.Add(new TaskNode(followTarget.Pos, s.NodeDistance.Value));
                        }
                    }
                    else
                    {
                        // Close-follow logic
                        if (s.CloseFollow.Value && dist > s.NodeDistance.Value && !tasks.Any())
                            tasks.Add(new TaskNode(followTarget.Pos, s.NodeDistance.Value));

                        // Loot
                        var loot = ctl.EntityListWrapper.Entities
                            .FirstOrDefault(e => e.Type == EntityType.WorldItem && e.HasComponent<WorldItem>() &&
                                                 ctl.Files.BaseItemTypes.Translate(e.GetComponent<WorldItem>().ItemEntity.Path).ClassName == "QuestItem");
                        if (loot != null && Vector3.Distance(ctl.LocalPlayer.Pos, loot.Pos) < s.ClearDistance.Value)
                            tasks.Add(new TaskNode(loot.Pos, s.ClearDistance.Value, TaskNodeType.Loot));

                        // Waypoint
                        if (!usedWp && s.TakeWaypoints.Value)
                        {
                            var wp = ctl.EntityListWrapper.Entities.FirstOrDefault(e => e.Type == EntityType.Waypoint &&
                                       Vector3.Distance(ctl.LocalPlayer.Pos, e.Pos) < s.ClearDistance.Value);
                            if (wp != null)
                            {
                                usedWp = true;
                                tasks.Add(new TaskNode(wp.Pos, s.ClearDistance.Value, TaskNodeType.ClaimWaypoint));
                            }
                        }
                    }
                    lastTarget = followTarget.Pos;
                }

                // Execute tasks
                if (tasks.Count > 0)
                {
                    var task = tasks[0];
                    var pdist = Vector3.Distance(AreWeThereYet.Instance.GameController.LocalPlayer.Pos, task.WorldPosition);
                    switch (task.Type)
                    {
                        case TaskNodeType.Movement:
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(task.WorldPosition));
                            yield return new WaitTime(rnd.Next(25) + 30);
                            Input.KeyDown(AreWeThereYet.Instance.Settings.MoveKey.Value);
                            yield return new WaitTime(rnd.Next(25) + 30);
                            Input.KeyUp(AreWeThereYet.Instance.Settings.MoveKey.Value);
                            if (pdist <= s.NodeDistance.Value * 1.5f)
                                tasks.RemoveAt(0);
                            break;
                        case TaskNodeType.Loot:
                            Input.KeyUp(AreWeThereYet.Instance.Settings.MoveKey.Value);
                            yield return new WaitTime(s.InputFrequency.Value);
                            // simple click
                            yield return Mouse.LeftClick();
                            tasks.RemoveAt(0);
                            break;
                        case TaskNodeType.Transition:
                            Input.KeyUp(AreWeThereYet.Instance.Settings.MoveKey.Value);
                            yield return new WaitTime(60);
                            var label = task.LabelOnGround.Label.GetClientRect().Center;
                            yield return Mouse.SetCursorPosAndLeftClickHuman(label, 100);
                            yield return new WaitTime(300);
                            task.AttemptCount++;
                            if (task.AttemptCount > 6) tasks.RemoveAt(0);
                            break;
                        case TaskNodeType.ClaimWaypoint:
                            if (pdist > 150)
                            {
                                var pos = Helper.WorldToValidScreenPosition(task.WorldPosition);
                                Input.KeyUp(AreWeThereYet.Instance.Settings.MoveKey.Value);
                                yield return new WaitTime(s.InputFrequency.Value);
                                yield return Mouse.SetCursorPosAndLeftClickHuman(pos, 100);
                                yield return new WaitTime(1000);
                            }
                            task.AttemptCount++;
                            if (task.AttemptCount > 3) tasks.RemoveAt(0);
                            break;
                    }
                }

                lastPlayer = AreWeThereYet.Instance.GameController.LocalPlayer.Pos;
                yield return new WaitTime(50);
            }
        }

        public void Render()
        {
            var s = AreWeThereYet.Instance.Settings;
            if (s.ToggleKey.PressedOnce())
            {
                s.AutoPilotEnabled.Value = !s.AutoPilotEnabled.Value;
                tasks.Clear();
            }

            if (!s.AutoPilotEnabled.Value || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
                return;

            // draw lines
            foreach (var t in tasks.Take(1))
            {
                AreWeThereYet.Instance.Graphics.DrawLine(
                    Helper.WorldToValidScreenPosition(AreWeThereYet.Instance.GameController.LocalPlayer.Pos),
                    Helper.WorldToValidScreenPosition(t.WorldPosition), 2f, Color.Pink);
            }
            AreWeThereYet.Instance.Graphics.DrawText(
                $"AreWeThereYet: {(coroutine.Running ? "Running" : "Stopped")}",
                new SharpDX.Vector2(400, 100));
        }
    }
}
