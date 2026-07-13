using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Facepunch;
using UnityEngine;
using UnityEngine.Networking;

//SimpleSymmetry created with PluginMerge v(1.0.8.0) by MJSU @ https://github.com/dassjosh/Plugin.Merge
namespace Oxide.Plugins
{
    [Info("SimpleSymmetry", "Shady14u", "1.2.8")]
    [Description("Effortlessly create symmetrical base designs")]
    public partial class SimpleSymmetry : RustPlugin
    {
        #region 0.SimpleSymmetry.cs
        private static Color _color = Color.blue;
        private readonly int _constructionMask = LayerMask.GetMask("Construction");
        private readonly int _terrainMask = LayerMask.GetMask("Terrain");
        private readonly List<ulong> _waterFoundations = new();

        private void AdjustSymmetry(PlayerBuildOptions buildOptions, BasePlayer player, SymmetryType symmetryType)
        {
            buildOptions.SymmetryType = symmetryType;
            SendMessage(player, $"Symmetry set to {symmetryType}.");
            ShowCurrentSymmetryPoint(player);
        }

        private bool CanAfford(BasePlayer player, List<ItemAmount> cost)
        {
            foreach (var itemAmount in cost)
            {
                if (player.inventory.GetAmount(itemAmount.itemid) < itemAmount.amount)
                {
                    return false;
                }
            }

            foreach (var itemAmount in cost)
            {
                player.inventory.Take(null, itemAmount.itemid, (int) itemAmount.amount);
                player.Command("note.inv " + itemAmount.itemid + " " + (float) (itemAmount.amount * -1.0));
            }

            return true;
        }

        private bool CanPlace(BaseEntity foundation, BasePlayer player)
        {
            var buildOptions = GetBuildOptions(player.userID.Get());
            var dist = Vector3.Distance(buildOptions.StartPoint, foundation.transform.position);
            if (buildOptions.SymmetryEnabled && dist > _config.MaxRadiusForBuilding)
            {
                SendMessage(player, "You are to far from your center point to use symmetry");
                return false;
            }

            var volumes = PrefabAttribute.server.FindAll<DeployVolume>(foundation.prefabID);
            var trans = foundation.transform;

            if (DeployVolume.Check(trans.position, trans.rotation, volumes))
            {
                SendMessage(player, "Not enough space");
                return false;
            }

            if (player.IsBuildingBlocked(trans.position, trans.rotation, foundation.bounds))
            {
                SendMessage(player, "You don't have permission to build here");
                return false;
            }

            if (foundation.ShortPrefabName.Contains("foundation") && !CheckFoundationCollision(foundation))
            {
                return false;
            }

            return !IsToCloseToRoad(foundation);
        }


        private bool CheckFoundationCollision(BaseEntity entity)
        {
            var ray = new Ray(entity.transform.position, Vector3.down);
            Physics.Raycast(ray, out var hitInfo, 100, LayerMask.GetMask("Terrain"));
            return hitInfo.distance <= 2.8 && !SocketMod_TerrainCheck.IsInTerrain(entity.transform.position, true);
        }

        private static string CreateButton(ref CuiElementContainer container, string anchorMin, string anchorMax,
        string offsetMin, string offsetMax, float padding, string buttonColor, string textColor, string buttonText,
        int fontSize, string buttonCommand, string parent = "Overlay",
        TextAnchor labelAnchor = TextAnchor.MiddleCenter)
        {
            var panel = container.Add(new CuiPanel
            {
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax,
                    OffsetMin = offsetMin,
                    OffsetMax = offsetMax
                },
                Image = {Color = "0 0 0 0"}
            }, parent);

            container.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = $"{padding} {padding}",
                    AnchorMax = $"{1 - padding} {1 - padding}"
                },
                Button = {Color = buttonColor, Command = $"{buttonCommand}"},
                Text = {Align = labelAnchor, Color = textColor, FontSize = fontSize, Text = buttonText}
            }, panel);
            return panel;
        }

        private static string CreateImagePanel(ref CuiElementContainer container, string anchorMin, string anchorMax,
        string offsetMin, string offsetMax, float padding, string imageData, string parent = "Overlay",
        string panelName = null)
        {
            var panel = container.Add(new CuiPanel
            {
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax,
                    OffsetMin = offsetMin,
                    OffsetMax = offsetMax
                },
                Image = {Color = "0 0 0 0"}
            }, parent, panelName);

            container.Add(new CuiElement
            {
                Parent = panel,
                Components =
                {
                    new CuiRectTransformComponent
                    {
                        AnchorMin = $"{padding} {padding + .004f}",
                        AnchorMax = $"{1 - padding - .004f} {1 - padding - .02f}"
                    },
                    new CuiRawImageComponent {Png = imageData}
                }
            });

            return panel;
        }

        private string CreateLabel(ref CuiElementContainer container, string anchorMin, string anchorMax,
        string offsetMin, string offsetMax, float padding, string backgroundColor, string textColor,
        string labelText, int fontSize, TextAnchor alignment, string parent = "Overlay",
        string labelName = null)
        {
            var panel = container.Add(new CuiPanel
            {
                RectTransform =
                {
                    AnchorMin = anchorMin, AnchorMax = anchorMax,
                    OffsetMin = offsetMin, OffsetMax = offsetMax
                },
                Image = {Color = backgroundColor}
            }, parent, labelName);
            container.Add(new CuiLabel
            {
                Text =
                {
                    Color = textColor,
                    Text = labelText,
                    Align = alignment,
                    FontSize = fontSize
                },
                RectTransform =
                {
                    AnchorMin = $"{padding} {padding}", AnchorMax = $"{1 - padding} {1 - padding}"
                }
            }, panel);
            return panel;
        }

        private static string CreatePanel(ref CuiElementContainer container, string anchorMin, string anchorMax,
        string offsetMin, string offsetMax, string panelColor, string parent = "Overlay",
        string panelName = null)
        {
            return container.Add(new CuiPanel
            {
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax,
                    OffsetMin = offsetMin,
                    OffsetMax = offsetMax
                },
                Image = {Color = panelColor}
            }, parent, panelName);
        }

        private void CreateSymmetryPanel(ref CuiElementContainer container, string anchorMin, string anchorMax,
        string offsetMin, string offsetMax, string backgroundColor, PlayerBuildOptions buildOptions,
        string parent = "Overlay", string buttonName = "")
        {
            var btnHeight = 36;
            var top = 0;
            var symmetryPanel = CreatePanel(ref container, anchorMin, anchorMax, offsetMin, offsetMax, backgroundColor,
            parent);
            var labelOffset = 0f;

            if (buildOptions.SymmetryShape == SymmetryShape.Triangle)
            {
                labelOffset = -25;
            }

            var symImagePanel = CreateImagePanel(ref container, Anchors.TopLeft, Anchors.TopLeft, $"5 {top - 132}",
            $"140 {top}", .02f,
            GetSymmetryImage(buildOptions),
            symmetryPanel);

            CreateLabel(ref container, Anchors.Center, Anchors.Center, $"-100 {labelOffset - 10}",
            $"100 {labelOffset + 10}", .02f,
            "0 0 0 0", "1 1 1 1",
            ToSentenceCase(GetMsg(buildOptions.SymmetryEnabled
            ? buildOptions.SymmetryType.ToString()
            : "SymmetryNotSet")),
            10, TextAnchor.MiddleCenter, symImagePanel);

            CreateButton(ref container, Anchors.TopRight, Anchors.TopRight, $"-95 {top - 136}", "-70 1.5", .04f,
            GetButtonColor(buttonName == "cycle"), "1 1 1 1",
            ">", 8, "symmetry cycle", "SymmetryPanel");

            CreateButton(ref container, Anchors.TopRight, Anchors.TopRight, $"-70 -25", $"-35 0", .2f,
            GetButtonColor(buttonName == "minimize"),
            "1 1 1 1", "_", 8, "symmetry minimize", "SymmetryPanel");

            CreateButton(ref container, Anchors.TopRight, Anchors.TopRight, $"-35 -25", $"0 0", .2f,
            GetButtonColor(buttonName == "close"),
            "1 1 1 1", "X", 8, "symmetry ui", "SymmetryPanel");

            top = -25;

            CreateButton(ref container, Anchors.TopRight, Anchors.TopRight, $"-70 {top - btnHeight}", $"0 -25", .1f,
            GetButtonColor(buttonName == "set"),
            "1 1 1 1", GetMsg(PluginMessages.SetSymmetry), 8, "symmetry set", "SymmetryPanel");
            top -= btnHeight;
            CreateButton(ref container, Anchors.TopRight, Anchors.TopRight, $"-70 {top - btnHeight}", $"0 {top}", .1f,
            GetButtonColor(buttonName == "toggle"),
            "1 1 1 1", GetMsg(PluginMessages.ToggleSymmetry), 8, "symmetry toggle", "SymmetryPanel");
            top -= btnHeight;
            CreateButton(ref container, Anchors.TopRight, Anchors.TopRight, $"-70 {top - btnHeight}", $"0 {top}", .1f,
            GetButtonColor(buttonName == "delete"),
            "1 1 1 1", GetMsg(PluginMessages.DeleteSymmetry), 8, "symmetry delete", "SymmetryPanel");
        }

        private void DemolishByType(SymmetryShape symmetryShape, SymmetryType symmetryType, Vector3 startPoint,
        BaseNetworkable entity, Quaternion buildOptionsStartRotation)
        {
            switch (symmetryShape)
            {
                case SymmetryShape.Triangle:
                ServerMgr.Instance.StartCoroutine(DemolishGeneric(startPoint, entity, 3));
                break;
                case SymmetryShape.Hexagon:
                switch (symmetryType)
                {
                    case SymmetryType.Normal6Sided:
                    ServerMgr.Instance.StartCoroutine(DemolishGeneric(startPoint, entity, 6));
                    return;
                    case SymmetryType.Mirrored2Sided:
                    ServerMgr.Instance.StartCoroutine(DemolishGeneric(startPoint, entity, 2));
                    return;
                    case SymmetryType.Normal3Sided:
                    ServerMgr.Instance.StartCoroutine(DemolishGeneric(startPoint, entity, 3));
                    return;
                    default:
                    ServerMgr.Instance.StartCoroutine(DemolishGeneric(startPoint, entity, 2));
                    return;
                }
                case SymmetryShape.Square:
                case SymmetryShape.Rectangle:
                case SymmetryShape.Octagon:
                switch (symmetryType)
                {
                    case SymmetryType.Normal2Sided:
                    ServerMgr.Instance.StartCoroutine(DemolishGeneric(startPoint, entity, 2));
                    return;
                    case SymmetryType.Normal4Sided:
                    ServerMgr.Instance.StartCoroutine(DemolishGeneric(startPoint, entity, 4));
                    return;
                    case SymmetryType.Mirrored2Sided:
                    ServerMgr.Instance.StartCoroutine(DemolishMirroredGeneric(startPoint, entity, 2,
                    buildOptionsStartRotation));
                    return;
                    case SymmetryType.Mirrored4Sided:
                    ServerMgr.Instance.StartCoroutine(DemolishMirroredGeneric(startPoint, entity, 4,
                    buildOptionsStartRotation));
                    return;
                }

                break;
            }
        }

        private IEnumerator DemolishGeneric(Vector3 startingPoint, BaseNetworkable newBlock, int total)
        {
            if (newBlock == null) yield break;
            var pos = newBlock.transform.position;
            var angles = 360 / total;

            for (var i = 1; i < total; i++)
            {
                var newPos = startingPoint + Quaternion.Euler(0, angles * i, 0) * (pos - startingPoint);
                var entityToDemolish = Physics.OverlapSphere(newPos, 2, _constructionMask)
                .Select(x => x.ToBaseEntity())
                .Where(x => x.ShortPrefabName == newBlock.ShortPrefabName)
                .OrderBy(x => Vector3.Distance(x.transform.position, newPos))
                .FirstOrDefault();
                if (entityToDemolish == null) continue;
                var demolishStorage = entityToDemolish as StorageContainer;
                if (demolishStorage != null && demolishStorage.inventory != null) demolishStorage.inventory.Clear();
                if (!entityToDemolish.IsDestroyed) entityToDemolish.Kill();
                yield return i;
            }
        }

        private IEnumerator DemolishMirroredGeneric(Vector3 startingPoint, BaseNetworkable newBlock, int sides,
        Quaternion startRotation)
        {
            var pos = newBlock.transform.position;
            var localOffset = Quaternion.Inverse(startRotation) * (pos - startingPoint);

            localOffset = Math.Abs(localOffset.x) > Math.Abs(localOffset.z)
            ? localOffset.WithX(-localOffset.x)
            : localOffset.WithZ(-localOffset.z);

            var newPoint = startingPoint + startRotation * localOffset;
            var entityToDemolish = Physics.OverlapSphere(newPoint, 2f, _constructionMask)
            .Select(x => x.ToBaseEntity())
            .OrderBy(x => Vector3.Distance(x.transform.position, newPoint))
            .FirstOrDefault(x => x.ShortPrefabName == newBlock.ShortPrefabName);

            if (entityToDemolish == null) yield break;
            var demolishStorage = entityToDemolish as StorageContainer;
            if (demolishStorage != null && demolishStorage.inventory != null) demolishStorage.inventory.Clear();
            if (!entityToDemolish.IsDestroyed) entityToDemolish.Kill();
        }

        private Vector3 FindTheCenter(BaseNetworkable entity, BasePlayer player)
        {
            if (!entity) return new Vector3();
            // Create a new vector to store the center position
            var centerPosition = Vector3.zero;
            if (entity.ShortPrefabName is "foundation" or "floor")
            {
                centerPosition = entity.transform.position;
                player.SendConsoleCommand("ddraw.sphere", 10f, Color.blue, centerPosition, 0.2f);
                return centerPosition;
            }

            // Get the angle of the triangle's baseline relative to the x-axis
            var transform = entity.transform;
            var baselineAngle = transform.eulerAngles.y;

            // Set the position of the triangle's baseline
            var baselinePosition = transform.position;

            // Calculate the position of the center of the triangle
            centerPosition.x = baselinePosition.x + (0.8660254f * Mathf.Sin(baselineAngle * Mathf.Deg2Rad));
            centerPosition.z = baselinePosition.z + (0.8660254f * Mathf.Cos(baselineAngle * Mathf.Deg2Rad));
            centerPosition.y = baselinePosition.y;
            player.SendConsoleCommand("ddraw.sphere", 10f, Color.blue, centerPosition, 0.2f);
            return centerPosition;
        }


        private string GetButtonColor(bool isSelected)
        {
            return isSelected ? _config.SelectedButtonColor : _config.DefaultButtonColor;
        }

        public BuildingBlock GetNearbyBuildingBlock(Vector3 position)
        {
            var nearbyBuildingBlock = (BuildingBlock) null;
            var list = Facepunch.Pool.Get<List<BuildingBlock>>();
            Vis.Entities(position, 15f, list, LayerMask.GetMask("Construction"));
            var maxDistance = 15f;
            foreach (var buildingBlock in list)
            {
                var distance = Vector3.Distance(buildingBlock.transform.position, position);
                if (distance > maxDistance) continue;
                maxDistance = distance;
                nearbyBuildingBlock = buildingBlock;
            }

            Facepunch.Pool.FreeUnmanaged(ref list);
            return nearbyBuildingBlock;
        }

        private string GetSymmetryImage(PlayerBuildOptions buildOptions)
        {
            var name = buildOptions.SymmetryEnabled
            ? $"{buildOptions.SymmetryShape}_{buildOptions.SymmetryType}.png"
            : $"{buildOptions.SymmetryShape}.png";

            return _storedData.CommonImages.TryGetValue(name, out var image)
            ? image
            : _storedData.CommonImages["Square.png"];
        }

        private bool IsToCloseToRoad(BaseEntity baseEntity)
        {
            var heightMap = TerrainMeta.HeightMap;
            var topologyMap = TerrainMeta.TopologyMap;
            if (heightMap == null)
            {
                return false;
            }

            if (topologyMap == null)
            {
                return false;
            }

            var transform = baseEntity.transform;
            var obb = new OBB(transform.position, Vector3.one, transform.rotation, baseEntity.bounds);
            var num = Mathf.Abs(heightMap.GetHeight(obb.position) - obb.position.y);
            if (num > 9f)
            {
                return false;
            }

            var radius = Mathf.Lerp(3f, 0f, num / 9f);
            var position = obb.position;
            var point = obb.GetPoint(-1f, 0f, -1f);
            var point2 = obb.GetPoint(-1f, 0f, 1f);
            var point3 = obb.GetPoint(1f, 0f, -1f);
            var point4 = obb.GetPoint(1f, 0f, 1f);
            var topology = topologyMap.GetTopology(position, radius);
            var topology2 = topologyMap.GetTopology(point, radius);
            var topology3 = topologyMap.GetTopology(point2, radius);
            var topology4 = topologyMap.GetTopology(point3, radius);
            var topology5 = topologyMap.GetTopology(point4, radius);
            return ((topology | topology2 | topology3 | topology4 | topology5) & 526336) != 0;
        }

// NV_WYXAJQqTTl/CIP7HT+7DTZMFiBc3gyeJo7/AqXBycJU=
        private void MinimizeUi(BasePlayer player)
        {
            var buildOptions = GetBuildOptions(player.userID.Get());
            buildOptions.UiMinimized = true;
            var container = new CuiElementContainer
            {
                new CuiElement
                {
                    Parent = "Overlay",
                    Name = "SymmetryPanel",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = _config.UiAnchorMax,
                            AnchorMax = _config.UiAnchorMax,
                            OffsetMin = "-50 -50",
                            OffsetMax = "0 0"
                        },
                        new CuiImageComponent
                        {
                            Color = _config.MainBackgroundColor
                        }
                    }
                }
            };

            CreateImagePanel(ref container, "0 0", "1 1", "0 0", "0 0", 0.1f, GetSymmetryImage(buildOptions),
            "SymmetryPanel");
            CreateButton(ref container, "0 0", "1 1", "0 0", "0 0", 0.1f, ".2 .2 .2 .9", "1 1 1 1", "+", 12,
            "symmetry ui true", "SymmetryPanel");


            CuiHelper.DestroyUi(player, "SymmetryPanel");
            CuiHelper.AddUi(player, container);
            Interface.CallHook("OnSymmetryUiChanged", player, false, true);
        }

        private void ReplicateByType(SymmetryShape symmetryShape, SymmetryType symmetryType, Vector3 startPoint,
        BaseNetworkable entity, Quaternion buildOptionsStartRotation, Planner plan)
        {
            switch (symmetryShape)
            {
                case SymmetryShape.Triangle:
                    ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 3, plan));
                    break;
                case SymmetryShape.Hexagon:
                    switch (symmetryType)
                    {
                        case SymmetryType.Normal6Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 6, plan));
                        return;
                        case SymmetryType.Mirrored2Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 2, plan));
                        return;
                        case SymmetryType.Normal3Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 3, plan));
                        return;
                        default:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 2, plan));
                        return;
                    }
                case SymmetryShape.Square:
                case SymmetryShape.Rectangle:
                    switch (symmetryType)
                    {
                        case SymmetryType.Normal2Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 2, plan));
                        return;
                        case SymmetryType.Normal4Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 4, plan));
                        return;
                        case SymmetryType.Mirrored2Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateMirroredGeneric(startPoint, entity, 2,
                        buildOptionsStartRotation, plan));
                        return;
                        case SymmetryType.Mirrored4Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateMirroredGeneric(startPoint, entity, 4,
                        buildOptionsStartRotation,plan));
                        return;
                    }

                    break;
                case SymmetryShape.Octagon:
                    switch (symmetryType)
                    {
                        case SymmetryType.Normal2Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 2, plan));
                        return;
                        case SymmetryType.Normal4Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateGeneric(startPoint, entity, 4, plan));
                        return;
                        case SymmetryType.Mirrored2Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateMirroredGeneric(startPoint, entity, 2,
                        buildOptionsStartRotation, plan));
                        return;
                        case SymmetryType.Mirrored4Sided:
                        ServerMgr.Instance.StartCoroutine(ReplicateMirroredGeneric(startPoint, entity, 4,
                        buildOptionsStartRotation, plan));
                        return;
                    }

                    break;
            }
        }

        private IEnumerator ReplicateGeneric(Vector3 startingPoint, BaseNetworkable newBlock, int total, Planner plan)
        {
            var pos = newBlock.transform;
            var playerId = (newBlock as BaseEntity)?.OwnerID ?? 0;
            if (playerId == 0 || total < 1) yield break;

            var player = BasePlayer.Find(playerId.ToString());
            if (player == null) yield break;

            var newBuildBlock = newBlock.GetComponent<BuildingBlock>();
            if (newBuildBlock == null) yield break;

            var cost = newBuildBlock.BuildCost();
            var newDefinition = PrefabAttribute.server.Find<Construction>(newBlock.prefabID);
            var newGrade = (newBlock as BuildingBlock)?.grade ?? BuildingGrade.Enum.Twigs;
            var angles = 360 / total;

            for (var i = 1; i < total; i++)
            {
                CoroutineEx.waitForSeconds(0.1f);
                if (_config.RequireMaterialsToBuild && !CanAfford(player, cost)) yield break;
                var newPos = startingPoint + Quaternion.Euler(0, angles * i, 0) * (pos.position - startingPoint);
                var newRotation = Quaternion.Euler(pos.eulerAngles + new Vector3(0, angles * i, 0));
                var foundation = GameManager.server.CreateEntity(newBlock.PrefabName.ToLower(), newPos, newRotation);
                if (foundation == null) continue;

                if (!CanPlace(foundation, player))
                {
                    foundation.Kill();
                    continue;
                }

                foundation.OwnerID = playerId;
                foundation.skinID = newBuildBlock.skinID;

                var decayEntity = foundation as DecayEntity;
                var nearbyBuildingBlock = GetNearbyBuildingBlock(newBlock.transform.position);
                if (nearbyBuildingBlock != null && decayEntity != null)
                {
                    var buildingId = nearbyBuildingBlock.buildingID == 0
                    ? BuildingManager.server.NewBuildingID()
                    : nearbyBuildingBlock.buildingID;

                    decayEntity.AttachToBuilding(buildingId);
                }

                foundation.Spawn();

                var block = foundation.GetComponent<BuildingBlock>();
                if (block != null)
                {
                    block.blockDefinition = newDefinition;
                    block.ChangeGradeAndSkin(newGrade,foundation.skinID);
                    block.SetHealthToMax();
                    block.SendNetworkUpdate();
                    _waterFoundations.Add(block.net.ID.Value);
                    Interface.Call("OnEntityBuilt", plan, block.gameObject);
                }

                foundation.SendNetworkUpdateImmediate();
                yield return null;

            }
        }

        private IEnumerator ReplicateMirroredGeneric(Vector3 startingPoint, BaseNetworkable newBlock, int total, Quaternion startRotation, Planner plan)
        {
            var pos = newBlock.transform;
            var playerId = (newBlock as BaseEntity)?.OwnerID ?? 0;
            if (playerId == 0 || total < 1) yield break;
            var player = BasePlayer.Find(playerId.ToString());
            if (player == null) yield break;
            var newBuildBlock = newBlock.GetComponent<BuildingBlock>();
            if (newBuildBlock == null) yield break;
            var cost = newBuildBlock.BuildCost();
            if (_config.RequireMaterialsToBuild && !CanAfford(player, cost)) yield break;

            var newPos = pos.position;
            var localOffset = Quaternion.Inverse(startRotation) * (newPos - startingPoint);

            localOffset = Math.Abs(localOffset.x) > Math.Abs(localOffset.z)
            ? localOffset.WithX(-localOffset.x)
            : localOffset.WithZ(-localOffset.z);

            var newPoint = startingPoint + startRotation * localOffset;
            var eulerAngles = pos.eulerAngles;
            var deltaY = startRotation.eulerAngles.y - eulerAngles.y;
            var newRot = Quaternion.Euler(eulerAngles.WithY(startRotation.eulerAngles.y + deltaY + 180));

            var entity = GameManager.server.CreateEntity(newBlock.PrefabName.ToLower(), newPoint, newRot);
            if (entity == null) yield break;

            entity.OwnerID = playerId;
            entity.skinID = newBuildBlock.skinID;

            var decayEntity = entity as DecayEntity;
            var nearbyBuildingBlock = GetNearbyBuildingBlock(newBlock.transform.position);
            if (nearbyBuildingBlock != null && decayEntity != null)
            {
                var buildingId = nearbyBuildingBlock.buildingID == 0
                ? BuildingManager.server.NewBuildingID()
                : nearbyBuildingBlock.buildingID;

                decayEntity?.AttachToBuilding(buildingId);
            }

            entity.Spawn();

            var block = entity.GetComponent<BuildingBlock>();
            if (block != null)
            {
                block.blockDefinition = PrefabAttribute.server.Find<Construction>(newBlock.prefabID);
                block.ChangeGradeAndSkin((newBlock as BuildingBlock)?.grade ?? BuildingGrade.Enum.Twigs,entity.skinID);
                block.SetHealthToMax();
                block.SendNetworkUpdate();
                _waterFoundations.Add(block.net.ID.Value);
                Interface.Call("OnEntityBuilt", plan, block.gameObject);
            }

            if (total != 4) yield break;

            localOffset = Quaternion.Inverse(startRotation) * (newPos - startingPoint);

            localOffset = Math.Abs(localOffset.x) < Math.Abs(localOffset.z)
            ? localOffset.WithX(-localOffset.x)
            : localOffset.WithZ(-localOffset.z);

            newPoint = startingPoint + startRotation * localOffset;
            eulerAngles = pos.eulerAngles;
            deltaY = startRotation.eulerAngles.y - eulerAngles.y;

            newRot = Quaternion.Euler(eulerAngles.WithY(startRotation.eulerAngles.y + deltaY + 180));
            entity.SendNetworkUpdateImmediate();

            entity = GameManager.server.CreateEntity(newBlock.PrefabName, newPoint, newRot);
            if (entity == null) yield break;

            entity.OwnerID = playerId;

            decayEntity = entity as DecayEntity;
            nearbyBuildingBlock = GetNearbyBuildingBlock(newBlock.transform.position);
            if (nearbyBuildingBlock != null && decayEntity != null)
            {
                var buildingId = nearbyBuildingBlock.buildingID == 0
                ? BuildingManager.server.NewBuildingID()
                : nearbyBuildingBlock.buildingID;

                decayEntity?.AttachToBuilding(buildingId);
            }

            entity.Spawn();

            block = entity.GetComponent<BuildingBlock>();
            if (block != null)
            {
                block.blockDefinition = PrefabAttribute.server.Find<Construction>(newBlock.prefabID);
                block.ChangeGradeAndSkin((newBlock as BuildingBlock)?.grade ?? BuildingGrade.Enum.Twigs,entity.skinID);
                block.SetHealthToMax();
                block.SendNetworkUpdate();
                _waterFoundations.Add(block.net.ID.Value);
                Interface.Call("OnEntityBuilt", plan, block.gameObject);
            }
            entity.SendNetworkUpdateImmediate();
            yield return null;

        }

        private void SendMessage(BasePlayer player, string message)
        {
            if (!_config.ShowChatMessages) return;
            player.ChatMessage(message);
        }

        private void SetupSymmetry(BasePlayer player, Vector3? origin = null)
        {
            var buildOptions = GetBuildOptions(player.userID.Get());
            buildOptions.SymmetryEnabled = false;

            var allEntities = Physics.OverlapSphere(origin ?? player.eyes.center, 1.6f, _constructionMask)
            .Select(x => x.ToBaseEntity())
            .Where(x => x.ShortPrefabName.Contains("foundation") || x.ShortPrefabName.Contains("floor")).ToArray();

            _color = Color.blue;

            if (!allEntities.Any())
            {
                player.ChatMessage(GetMsg(PluginMessages.NoProperFoundations, player.userID.Get()));
                return;
            }

            if (allEntities.Any(
            x => x.ShortPrefabName == "foundation.triangle" || x.ShortPrefabName == "floor.triangle"))
            {
                switch (allEntities.Length)
                {
                    case 6:
                    buildOptions.SymmetryShape = SymmetryShape.Hexagon;
                    var x1 = FindTheCenter(allEntities[0], player);
                    x1 += FindTheCenter(allEntities[1], player);
                    x1 += FindTheCenter(allEntities[2], player);
                    x1 += FindTheCenter(allEntities[3], player);
                    x1 += FindTheCenter(allEntities[4], player);
                    x1 += FindTheCenter(allEntities[5], player);

                    x1 /= 6;
                    buildOptions.StartPoint = x1;
                    buildOptions.SymmetryType = SymmetryType.Normal6Sided;
                    break;
                    default:
                    var centerBlock = allEntities.First(x =>
                    x.ShortPrefabName == "foundation.triangle" || x.ShortPrefabName == "floor.triangle");
                    buildOptions.SymmetryShape = SymmetryShape.Triangle;
                    var initialPosition = centerBlock.transform.position;

                    // Set the angle of the triangle's baseline relative to the x-axis
                    var baselineAngle = centerBlock.transform.eulerAngles.y;

                    // Set the position of the triangle's baseline
                    var baselinePosition = initialPosition;

                    // Create a new vector to store the center position
                    var centerPosition = Vector3.zero;

                    // Calculate the position of the center of the triangle
                    centerPosition.x = baselinePosition.x +
                    (0.8667854f * Mathf.Sin(baselineAngle * Mathf.Deg2Rad));
                    centerPosition.z = baselinePosition.z +
                    (0.8667854f * Mathf.Cos(baselineAngle * Mathf.Deg2Rad));
                    centerPosition.y = baselinePosition.y;

                    buildOptions.StartPoint = centerPosition;
                    buildOptions.SymmetryType = SymmetryType.Normal3Sided;
                    break;
                }

                // The square branch below enables symmetry after detection, but this
                // branch never did - triangle and hexagon points were set yet stayed OFF.
                buildOptions.SymmetryEnabled = true;
                buildOptions.StartRotation = allEntities[0].transform.rotation;
                player.ChatMessage(GetMsg(PluginMessages.SymmetrySet, player.userID.Get()));
            }
            else
            {
                switch (allEntities.Length)
                {
                    case 1:
                    buildOptions.SymmetryShape = SymmetryShape.Square;
                    buildOptions.StartPoint = allEntities[0].transform.position;
                    break;
                    case 2:
                    buildOptions.SymmetryShape = SymmetryShape.Rectangle;
                    var x0 = FindTheCenter(allEntities[0], player);
                    x0 += FindTheCenter(allEntities[1], player);
                    x0 /= 2;
                    buildOptions.StartPoint = x0;
                    _color = Color.magenta;
                    break;
                    case 4:
                    buildOptions.SymmetryShape = SymmetryShape.Octagon;
                    var x1 = FindTheCenter(allEntities[0], player);
                    x1 += FindTheCenter(allEntities[1], player);
                    x1 += FindTheCenter(allEntities[2], player);
                    x1 += FindTheCenter(allEntities[3], player);
                    x1 /= 4;
                    buildOptions.StartPoint = x1;
                    break;
                    default:
                    player.ChatMessage(GetMsg(PluginMessages.NoProperFoundations, player.userID.Get()));
                    return;
                }

                buildOptions.SymmetryEnabled = true;
                buildOptions.StartRotation = allEntities[0].transform.rotation;
                buildOptions.SymmetryType = SymmetryType.Normal2Sided;
                player.ChatMessage(GetMsg(PluginMessages.SymmetrySet, player.userID.Get()));
            }


            player.SendConsoleCommand("ddraw.sphere", 10f, _color, buildOptions.StartPoint, 0.5f);
        }

        private void ShowCurrentSymmetryPoint(BasePlayer player)
        {
            var buildOptions = GetBuildOptions(player.userID.Get());
            if (buildOptions.StartPoint == Vector3.zero)
            {
                player.ChatMessage(GetMsg(PluginMessages.SymmetryUnSet, player.userID.Get()));
                return;
            }

            _color = Color.blue;
            switch (buildOptions.SymmetryShape)
            {
                case SymmetryShape.Rectangle:
                {
                    _color = Color.magenta;
                    if (buildOptions.SymmetryType == SymmetryType.Mirrored2Sided)
                    {
                        _color = Color.red;
                    }

                    break;
                }
                case SymmetryShape.Octagon:
                {
                    switch (buildOptions.SymmetryType)
                    {
                        case SymmetryType.Mirrored2Sided:
                        _color = Color.red;
                        break;
                        case SymmetryType.Mirrored4Sided:
                        _color = Color.yellow;
                        break;
                        case SymmetryType.Normal2Sided:
                        _color = Color.cyan;
                        break;
                    }

                    break;
                }
            }

            player.SendConsoleCommand("ddraw.sphere", 10f, _color, buildOptions.StartPoint, 0.5f);
        }

        private void ShowUi(BasePlayer player, string buttonName = "")
        {
            var buildOptions = GetBuildOptions(player.userID.Get());
            buildOptions.UiEnabled = true;
            buildOptions.UiMinimized = false;
            var container = new CuiElementContainer
            {
                new CuiElement
                {
                    Parent = "Overlay",
                    Name = "SymmetryPanel",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = _config.UiAnchorMin,
                            AnchorMax = _config.UiAnchorMax,
                            OffsetMin = _config.OffsetMin,
                            OffsetMax = _config.OffsetMax
                        },
                        new CuiImageComponent
                        {
                            Color = _config.MainBackgroundColor
                        }
                    }
                }
            };

            CreateSymmetryPanel(ref container, "0 0", "1 1", "0 0", "0 0", "0 0 0 0", buildOptions, "SymmetryPanel",
            buttonName);
            CuiHelper.DestroyUi(player, "SymmetryPanel");
            CuiHelper.AddUi(player, container);
        }

        private void StopSymmetry(BasePlayer player)
        {
            var buildOptions = GetBuildOptions(player.userID.Get());
            buildOptions.SymmetryEnabled = false;
        }

        private static string ToSentenceCase(string input)
        {
            return Regex.Replace(input, "((^[a-z]+)|([0-9]+)|([A-Z]{1}[a-z]+)|([A-Z]+(?=([A-Z][a-z])|($)|([0-9]))))",
            "$1 ");
        }

        internal class Anchors
        {
            public static string BottomCenter = ".5 0";
            public static string BottomLeft = "0 0";
            public static string BottomRight = "1 0";
            public static string Center = ".5 .5";
            public static string CenterLeft = "0 .5";
            public static string CenterRight = "1 .5";

            public static string TopCenter = ".5 1";
            public static string TopLeft = "0 1";

            public static string TopRight = "1 1";
        }
        #endregion

        #region 1.SimpleSymmetry.Config.cs
        private static Configuration _config;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) LoadDefaultConfig();
                SaveConfig();
                LoadData();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                PrintWarning("Creating new config file.");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => _config = Configuration.DefaultConfig();
        protected override void SaveConfig() => Config.WriteObject(_config);

        public class Configuration
        {
            public string DefaultButtonColor = "0 0 0 .7";
            public string MainBackgroundColor = "0 0 0 .8";
            public string OffsetMax = "-5 -5";
            public string OffsetMin = "-245 -140";
            public string SelectedButtonColor = "0 .45 1 .7";

            [JsonProperty(PropertyName = "ShowUIByDefault")]
            public bool ShowUiByDefault = false;

            public string UiAnchorMax = "1 1";
            public string UiAnchorMin = "1 1";
            public bool EnableEntKill { get; set; }

            [JsonProperty(PropertyName = "Max Radius For Building")]
            public double MaxRadiusForBuilding { get; set; }

            public bool ShowChatMessages { get; set; } = false;

            public bool SupportRemoverTool { get; set; } = true;

            public bool ToggleUiWithPlanner { get; set; } = true;

            public bool RequireMaterialsToBuild { get; set; } = true;

            public static Configuration DefaultConfig()
            {
                return new Configuration
                {
                    MaxRadiusForBuilding = 40
                };
            }
        }
        #endregion

        #region 2.SimpleSymmetry.Localization.cs
        private string GetMsg(string key, object userId = null)
        {
            return lang.GetMessage(key, this, userId?.ToString());
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [PluginMessages.NoPermission] = "You do not have permission to do that",
                [PluginMessages.MustUse2X2] =
                "Your Symmetry point could not be set.\n You must start with a square or 2x2",
                [PluginMessages.SetSymmetry] = "Set\nSymmetry",
                [PluginMessages.ToggleSymmetry] = "Toggle\nSymmetry",
                [PluginMessages.DeleteSymmetry] = "Delete\nSymmetry",
                [$"{SymmetryType.Mirrored2Sided}"] = SymmetryType.Mirrored2Sided.ToString(),
                [$"{SymmetryType.Mirrored4Sided}"] = SymmetryType.Mirrored4Sided.ToString(),
                [$"{SymmetryType.Normal2Sided}"] = SymmetryType.Normal2Sided.ToString(),
                [$"{SymmetryType.Normal3Sided}"] = SymmetryType.Normal3Sided.ToString(),
                [$"{SymmetryType.Normal4Sided}"] = SymmetryType.Normal4Sided.ToString(),
                [$"{SymmetryType.Normal6Sided}"] = SymmetryType.Normal6Sided.ToString(),
                [$"SymmetryNotSet"] = "Symmetry Not Set",
                [PluginMessages.SymmetryChanged] = "Symmetry type changed to {0}",
                [PluginMessages.SymmetrySet] = "Your Symmetry point has been set.",
                [PluginMessages.SymmetryUnSet] = "Symmetry Point not set.",
                [PluginMessages.NoProperFoundations] =
                "Your Symmetry point could not be set. You must start with:\n1. A single triangle\n2. A single square\n3. Squares in a  2x2\n4. Triangles in a hexagon",
                [PluginMessages.CanNotAfford] = "Can not afford to fully upgrade.",
                [PluginMessages.DisablingSymmetry] = "Disabling Symmetry",
                [PluginMessages.SymmetryPointDeleted] = "Symmetry point deleted",
                [PluginMessages.SymmetryReEnabled] = "Symmetry Re-Enabled",
                [PluginMessages.HelpMenu] = "<color=#28B9DD>/sym ui</color> - Toggles UI on/off \n" +
                "<color=#28B9DD>/sym toggle</color> - Toggles Symmetry on/off \n" +
                "<color=#28B9DD>/sym show</color> - Show the current Symmetry Center\n" +
                "<color=#28B9DD>/sym set</color> - Sets the Symmetry Center\n" +
                "<color=#28B9DD>/sym delete</color> - Deletes the Symmetry Center\n" +
                "<color=#28B9DD>/sym {type}</color> - Sets the Symmetry Type (see below) \n" +
                "<color=#28B9DD>  N2S</color> (Normal 2 sided)\n" +
                "<color=#28B9DD>  N3S</color> (Normal 3 sided)\n" +
                "<color=#28B9DD>  N4S</color> (Normal 4 sided)\n" +
                "<color=#28B9DD>  N6S</color> (Normal 6 sided)\n" +
                "<color=#28B9DD>  M2S</color> (Mirrored 2 sided)\n" +
                "<color=#28B9DD>  M4S</color> (Mirrored 4 sided)\n"
            }, this);
        }

        private static class PluginMessages
        {
            public const string CanNotAfford = "CanNotAfford";
            public const string DeleteSymmetry = "DeleteSymmetry";
            public const string DisablingSymmetry = "DisablingSymmetry";
            public const string HelpMenu = "HelpMenu";
            public const string MustUse2X2 = "MustUse2X2";
            public const string NoPermission = "NoPermission";
            public const string NoProperFoundations = "NoProperFoundations";
            public const string SetSymmetry = "SetSymmetry";
            public const string SymmetryChanged = "SymmetryChanged";
            public const string SymmetryPointDeleted = "SymmetryPointDeleted";
            public const string SymmetryReEnabled = "SymmetryReEnabled";
            public const string SymmetrySet = "SymmetrySet";
            public const string SymmetryUnSet = "SymmetryUnset";
            public const string ToggleSymmetry = "ToggleSymmetry";
        }
        #endregion

        #region 3.SimpleSymmetry.Permissions.cs
        public class PluginPermissions
        {
            public const string SimpleSymmetryUse = "simplesymmetry.use";
        }

        private void LoadPermissions()
        {
            permission.RegisterPermission(PluginPermissions.SimpleSymmetryUse, this);
        }
        #endregion

        #region 4.SimpleSymmetry.Data.cs
        private static readonly Dictionary<ulong, PlayerBuildOptions> ActiveBuildOptions = new();

        private readonly List<AssetInfo> _assets = new()
        {
            new() {FileName = "Triangle.png"},
            new() {FileName = "Triangle_Normal3Sided.png"},
            new() {FileName = "Rectangle.png"},
            new() {FileName = "Rectangle_Normal2Sided.png"},
            new() {FileName = "Rectangle_Mirrored2Sided.png"},
            new() {FileName = "Square.png"},
            new() {FileName = "Square_Normal2Sided.png"},
            new() {FileName = "Square_Normal4Sided.png"},
            new() {FileName = "Square_Mirrored2Sided.png"},
            new() {FileName = "Square_Mirrored4Sided.png"},
            new() {FileName = "Octagon.png"},
            new() {FileName = "Octagon_Normal2Sided.png"},
            new() {FileName = "Octagon_Normal4Sided.png"},
            new() {FileName = "Octagon_Mirrored2Sided.png"},
            new() {FileName = "Octagon_Mirrored4Sided.png"},
            new() {FileName = "Hexagon.png"},
            new() {FileName = "Hexagon_Normal2Sided.png"},
            new() {FileName = "Hexagon_Normal3Sided.png"},
            new() {FileName = "Hexagon_Normal6Sided.png"}
        };

        private StoredData _storedData;

        private void DownloadAssetImages()
        {
            var assetInfo = _assets.FirstOrDefault(x => !_storedData.CommonImages.ContainsKey(x.FileName));
            if (assetInfo == null) return;
            ServerMgr.Instance.StartCoroutine(GetImageFromFilePath(assetInfo));
        }

        private static PlayerBuildOptions GetBuildOptions(ulong playerId)
        {
            if (!ActiveBuildOptions.ContainsKey(playerId))
            {
                ActiveBuildOptions.Add(playerId, new PlayerBuildOptions());
            }

            return ActiveBuildOptions[playerId];
        }

        // API: set the symmetry point around a specific world position (e.g. a hammered
        // block) instead of where the player stands.
        private void SetSymmetryAtPosition(BasePlayer player, Vector3 position)
        {
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, PluginPermissions.SimpleSymmetryUse))
            {
                SendMessage(player, GetMsg(PluginMessages.NoPermission, player.userID.Get()));
                return;
            }

            SetupSymmetry(player, position + new Vector3(0, 1f, 0));
        }

        // API used by RandomGridSpawn's menu to mirror the panel state.
        private Dictionary<string, object> GetSymmetryInfo(BasePlayer player)
        {
            if (player == null) return null;

            var buildOptions = GetBuildOptions(player.userID.Get());
            return new Dictionary<string, object>
            {
                ["set"] = buildOptions.StartPoint != Vector3.zero,
                ["enabled"] = buildOptions.SymmetryEnabled,
                ["shape"] = buildOptions.SymmetryShape.ToString(),
                ["type"] = ToSentenceCase(buildOptions.SymmetryType.ToString()),
                ["image"] = GetSymmetryImage(buildOptions)
            };
        }

        IEnumerator GetImageFromFilePath(AssetInfo assetInfo)
        {
            var url =
            $"file://{Interface.Oxide.DataDirectory}{Path.DirectorySeparatorChar}SimpleSymmetry{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}{assetInfo.FileName}";

            using (var webRequest = UnityWebRequestTexture.GetTexture(url))
            {
                yield return webRequest.SendWebRequest();

                if (webRequest.isHttpError || webRequest.isNetworkError)
                {
                    Debug.LogError($"Image could not be loaded {url}: {webRequest.error}");
                }
                else
                {
                    var texture = DownloadHandlerTexture.GetContent(webRequest);
                    _storedData.CommonImages.Add(assetInfo.FileName,
                    FileStorage.server.Store(texture.EncodeToPNG(), FileStorage.Type.png,
                    CommunityEntity.ServerInstance.net.ID).ToString());
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            DownloadAssetImages();
        }

        private void LoadData()
        {
            try
            {

                _storedData = Interface.GetMod().DataFileSystem.ReadObject<StoredData>("SimpleSymmetry");
            }
            catch (Exception e)
            {
                Puts(e.Message);
                Puts(e.StackTrace);
                _storedData = new StoredData
                {
                    CommonImages = new Dictionary<string, string>()
                };
                SaveData();
            }
        }

        void OnNewSave(string filename)
        {
            _storedData.CommonImages = new Dictionary<string, string>();
            SaveData();
        }
// NV_WYXAJQqTTl/CIP7HT+7DTZMFiBc3gyeJo7/AqXBycJU=

        private void SaveData()
        {
            Interface.GetMod().DataFileSystem.WriteObject("SimpleSymmetry", _storedData);
        }

        public class StoredData
        {
            public Dictionary<string, string> CommonImages = new();
        }
        #endregion

        #region 5.SimpleSymmetry.Hooks.cs
        private void Init()
        {
            LoadData();
            LoadPermissions();
        }

        void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null || player.IsNpc || !_config.ToggleUiWithPlanner ||
            !permission.UserHasPermission(player.UserIDString, "simplesymmetry.use")) return;

            var itemInHand = newItem?.GetHeldEntity()?.ShortPrefabName;
            if (itemInHand == "planner" || itemInHand == "hammer.entity" || itemInHand == "toolgun.entity")
            {
                var buildOptions = GetBuildOptions(player.userID.Get());
                if (buildOptions.UiMinimized)
                {
                    MinimizeUi(player);
                }
                else
                {
                    ShowUi(player);
                }
            }
            else
            {
                CuiHelper.DestroyUi(player, "SymmetryPanel");
            }
        }

        void Unload()
        {
            SaveData();

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null)
                    CuiHelper.DestroyUi(player, "SymmetryPanel");
            }
        }

        object OnEntityKill(BuildingPrivlidge entity)
        {
            if(entity == null) return null;
            if(entity.health>0 && entity.inventory.itemList.Any()) return false;
            return null;

        }

        void OnStructureRotate(BaseCombatEntity entity, BasePlayer player)
        {
            if (entity == null || player == null || entity.OwnerID == 0) return;

            var buildOptions = GetBuildOptions(player.userID.Get());

            if (!buildOptions.SymmetryEnabled) return;

            try
            {
                var center = entity.ShortPrefabName.Contains("triangle")
                    ? entity.transform.position +
                      entity.transform.rotation * new Vector3(0, 0, MathF.Cos(30 * Mathf.Deg2Rad))
                    : entity.transform.position;

                var count = 0;
                var symmetryType = buildOptions.SymmetryType;
                count = symmetryType switch
                {
                    SymmetryType.Normal2Sided => 2,
                    SymmetryType.Normal3Sided => 3,
                    SymmetryType.Normal4Sided => 4,
                    SymmetryType.Normal6Sided => 6,
                    _ => count
                };

                var angleIncrement = 360f / count;

                for (var i = 0; i <= (count - 1); i++)
                {
                        BaseEntity entityToRotate = null;
                        var newRotation = Quaternion.Euler(0, i * angleIncrement, 0);
                        var newPosition = buildOptions.StartPoint +
                                          newRotation * (center - buildOptions.StartPoint);

                        var rayStart = newPosition + Vector3.up * 5f;
                        var rayDirection = Vector3.down;

                        var layers = LayerMask.GetMask("Construction", "Default");

                        if (Physics.Raycast(rayStart, rayDirection, out var hit, 10f, layers))
                        {
                            var hitEntity = hit.GetEntity();
                            if (hitEntity != null && hitEntity.ShortPrefabName == entity.ShortPrefabName && hitEntity.net.ID != entity.net.ID)
                            {
                                entityToRotate = hitEntity;
                            }
                        }

                        if (entityToRotate == null)
                        {
                            var colliders = Physics.OverlapSphere(newPosition,
                                entity.ShortPrefabName.Contains("triangle") ? .8f : .5f,
                                LayerMask.GetMask("Construction", "Default"));

                            var closestDistance = float.MaxValue;

                            foreach (var collider in colliders)
                            {
                                var foundEntity = collider.GetComponentInParent<BaseEntity>() as BaseEntity;
                                if (foundEntity is null ||
                                    foundEntity.ShortPrefabName != entity.ShortPrefabName ||
                                    foundEntity.net.ID == entity.net.ID)
                                {
                                    continue;
                                }

                                var distance = Vector3.Distance(newPosition, foundEntity.transform.position);
                                if (!(distance < closestDistance)) continue;
                                closestDistance = distance;
                                entityToRotate = foundEntity;
                            }
                        }

                        if (entityToRotate == null) continue;

                        var buildingBlock = entityToRotate.GetComponent<BuildingBlock>();

                        if (buildingBlock == null) continue;

                        buildingBlock.transform.rotation = buildingBlock.transform.rotation * Quaternion.Euler(0, 180, 0);
                        NextTick(() =>
                        {
                            buildingBlock.SendNetworkUpdateImmediate();
                            buildingBlock.ResetUpkeepTime();
                        });



                }
            }
            catch (Exception ex)
            {
            }

        }


        void OnEntityBuilt(Planner plan, GameObject go)
        {
            var player = plan?.GetOwnerPlayer();
            if (player == null) return;
            var buildOptions = GetBuildOptions(player.userID.Get());
            if (!buildOptions.SymmetryEnabled) return;
            var entity = go.ToBaseEntity();
            if (entity == null) return;
            if (_waterFoundations.Contains(entity.net.ID.Value))
            {
                _waterFoundations.Remove(entity.net.ID.Value);
                return;
            }

            ReplicateByType(buildOptions.SymmetryShape, buildOptions.SymmetryType, buildOptions.StartPoint, entity,
            buildOptions.StartRotation, plan);
        }

        private void OnNormalRemovedEntity(BasePlayer player, BaseEntity entity)
        {
            if (!_config.SupportRemoverTool) return;
            if (entity == null || player == null) return;
            var buildOptions = GetBuildOptions(player.userID.Get());

            if (buildOptions.SymmetryEnabled)
            {
                DemolishByType(buildOptions.SymmetryShape, buildOptions.SymmetryType, buildOptions.StartPoint, entity,
                buildOptions.StartRotation);
            }
        }

        void OnPlayerConnected(BasePlayer player)
        {
            if (_config.ShowUiByDefault)
            {
                ShowUi(player);
            }
        }

        void OnServerInitialized(bool initial)
        {
            DownloadAssetImages();
        }

        void OnStructureDemolish(BaseCombatEntity entity, BasePlayer player, bool immediate)
        {
            if (entity == null || player == null) return;
            var buildOptions = GetBuildOptions(player.userID.Get());

            if (buildOptions.SymmetryEnabled)
            {
                DemolishByType(buildOptions.SymmetryShape, buildOptions.SymmetryType, buildOptions.StartPoint, entity,
                buildOptions.StartRotation);
            }
        }
        #endregion

        #region 6.SimpleSymmetry.Commands.cs
        [ChatCommand("sym")]
        void ChatCmdSymmetry(BasePlayer player, string command, string[] args)
        {
            var action = "help";
            if (args is {Length: > 0})
            {
                action = args[0];
            }

            if (!permission.UserHasPermission(player.UserIDString, PluginPermissions.SimpleSymmetryUse))
            {
                SendMessage(player,GetMsg(PluginMessages.NoPermission, player.userID.Get()));
                CuiHelper.DestroyUi(player, "SymmetryPanel");
                return;
            }

            var buildOptions = GetBuildOptions(player.userID.Get());

            switch (action)
            {
                case "toggle":
                if (buildOptions.SymmetryEnabled)
                {
                    SendMessage(player,GetMsg(PluginMessages.DisablingSymmetry, player.userID.Get()));
                    StopSymmetry(player);
                    break;
                }

                if (buildOptions.StartPoint != Vector3.zero)
                {
                    SendMessage(player,GetMsg(PluginMessages.SymmetryReEnabled, player.userID.Get()));
                    buildOptions.SymmetryEnabled = true;
                }

                break;
                case "ui":
                if (args is {Length: > 1})
                {
                    buildOptions.UiEnabled = Convert.ToBoolean(args[1]);
                }
                else
                {
                    buildOptions.UiEnabled = !buildOptions.UiEnabled;
                }

                break;
                case "set":
                SetupSymmetry(player);
                break;
                case "show":
                ShowCurrentSymmetryPoint(player);
                break;
                case "minimize":
                MinimizeUi(player);
                return;
                case "M2S":
                AdjustSymmetry(buildOptions, player, SymmetryType.Mirrored2Sided);
                break;
                case "M4S":
                AdjustSymmetry(buildOptions, player, SymmetryType.Mirrored4Sided);
                break;
                case "N2S":
                AdjustSymmetry(buildOptions, player, SymmetryType.Normal2Sided);
                break;
                case "N3S":
                AdjustSymmetry(buildOptions, player, SymmetryType.Normal3Sided);
                break;
                case "N4S":
                AdjustSymmetry(buildOptions, player, SymmetryType.Normal4Sided);
                break;
                case "N6S":
                AdjustSymmetry(buildOptions, player, SymmetryType.Normal6Sided);
                break;
                case "delete":
                buildOptions.StartPoint = Vector3.zero;
                buildOptions.StartRotation = new Quaternion(0, 0, 0, 0);
                buildOptions.SymmetryEnabled = false;
                SendMessage(player,GetMsg(PluginMessages.SymmetryPointDeleted, player.userID.Get()));
                break;
                case "cycle":
                CycleSymmetryType(buildOptions);
                ShowCurrentSymmetryPoint(player);
                SendMessage(player, string.Format(GetMsg(PluginMessages.SymmetryChanged, player.userID.Get()),
                ToSentenceCase(GetMsg(buildOptions.SymmetryType.ToString()))));
                break;
                default:
                SendMessage(player,GetMsg(PluginMessages.HelpMenu, player.userID.Get()));
                return;
            }
// NV_WYXAJQqTTl/CIP7HT+7DTZMFiBc3gyeJo7/AqXBycJU=

            if (buildOptions.UiEnabled)
            {
                ShowUi(player, action);
                timer.Once(.3f, () => { ShowUi(player); });
                Interface.CallHook("OnSymmetryUiChanged", player, true, buildOptions.UiMinimized);
            }
            else
            {
                CuiHelper.DestroyUi(player, "SymmetryPanel");
                Interface.CallHook("OnSymmetryUiChanged", player, false, buildOptions.UiMinimized);
            }
        }

        [ConsoleCommand("symmetry")]
        void ConsoleCmdSymmetry(ConsoleSystem.Arg arg)
        {
            if (arg == null || !arg.HasArgs()) return;
            var player = arg.Player();
            if (player == null) return;

            ChatCmdSymmetry(player, "sym", arg.Args.Select(x=>x.ToString()).ToArray());
        }

        private static void CycleSymmetryType(PlayerBuildOptions buildOptions)
        {
            switch (buildOptions.SymmetryShape)
            {
                case SymmetryShape.Triangle:
                buildOptions.SymmetryType = SymmetryType.Normal3Sided;
                return;
                case SymmetryShape.Hexagon:
                {
                    switch (buildOptions.SymmetryType)
                    {
                        case SymmetryType.Normal2Sided:
                        buildOptions.SymmetryType = SymmetryType.Normal3Sided;
                        return;
                        case SymmetryType.Normal3Sided:
                        buildOptions.SymmetryType = SymmetryType.Normal6Sided;
                        return;
                        default:
                        buildOptions.SymmetryType = SymmetryType.Normal2Sided;
                        return;
                    }
                }
                case SymmetryShape.Rectangle:
                {
                    if (buildOptions.SymmetryType == SymmetryType.Mirrored2Sided)
                    {
                        buildOptions.SymmetryType = SymmetryType.Normal2Sided;
                        return;
                    }

                    buildOptions.SymmetryType = SymmetryType.Mirrored2Sided;
                    return;
                }
                case SymmetryShape.Square:
                case SymmetryShape.Octagon:
                {
                    switch (buildOptions.SymmetryType)
                    {
                        case SymmetryType.Mirrored2Sided:
                        buildOptions.SymmetryType = SymmetryType.Mirrored4Sided;
                        return;
                        case SymmetryType.Mirrored4Sided:
                        buildOptions.SymmetryType = SymmetryType.Normal2Sided;
                        return;
                        case SymmetryType.Normal2Sided:
                        buildOptions.SymmetryType = SymmetryType.Normal4Sided;
                        return;
                    }

                    buildOptions.SymmetryType = SymmetryType.Mirrored2Sided;
                    break;
                }
            }
        }
        #endregion

        #region 7.SimpleSymmetry.Classes.cs
        public enum SymmetryShape
        {
            Rectangle = 2,
            Triangle = 3,
            Square = 4,
            Hexagon = 6,
            Octagon = 8
        }

        public enum SymmetryType
        {
            Normal2Sided = 2,
            Normal3Sided = 3,
            Normal4Sided = 4,
            Normal6Sided = 6,
            Mirrored2Sided = 8,
            Mirrored4Sided = 16
        }

        public class AssetInfo
        {
            public string FileData { get; set; }
            public string FileName { get; set; }
        }

        public class PlayerBuildOptions
        {
            public Vector3 StartPoint { get; set; } = new Vector3();
            public Quaternion StartRotation { get; set; }
            public bool SymmetryEnabled { get; set; } = false;
            public SymmetryShape SymmetryShape { get; set; } = SymmetryShape.Square;
            public SymmetryType SymmetryType { get; set; } = SymmetryType.Mirrored4Sided;
            public bool UiEnabled { get; set; } = false;
            public bool UiMinimized { get; set; } = false;
        }
        #endregion

    }

}
