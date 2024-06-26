﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ImGuiNET;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using Newtonsoft.Json;
using visland.Helpers;
using visland.IPC;
using static visland.Gathering.GatherRouteDB;
using Action = System.Action;

namespace visland.Gathering;

public class GatherWindow : Window, IDisposable
{
    private readonly UITree _tree = new();
    private readonly List<Action> _postDraw = [];

    public GatherRouteDB RouteDB;
    public GatherRouteExec Exec = new();
    public GatherDebug _debug;

    private int selectedRouteIndex = -1;
    public static bool loop;

    private readonly List<uint> Colours = Svc.Data.GetExcelSheet<UIColor>()!.Select(x => x.UIForeground).ToList();
    private readonly Vector4 greenColor = new Vector4(0x5C, 0xB8, 0x5C, 0xFF) / 0xFF;
    private readonly Vector4 redColor = new Vector4(0xD9, 0x53, 0x4F, 0xFF) / 0xFF;
    private Vector4 yellowColor = new Vector4(0xD9, 0xD9, 0x53, 0xFF) / 0xFF;

    private readonly List<int> Items = Svc.Data.GetExcelSheet<Item>()?.Select(x => (int)x.RowId).ToList()!;
    private readonly ExcelSheet<Item> _items;

    private string searchString = string.Empty;
    private readonly List<Route> FilteredRoutes = [];

    private readonly Dictionary<uint, Mount>? _mounts;
    private string mountSearchString = string.Empty;

    public GatherWindow() : base("采集自动化", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(800, 800);
        SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new GatherDebug(Exec);
        _items = Svc.Data.GetExcelSheet<Item>()!;

        _mounts ??= Svc.Data.GetExcelSheet<Mount>()!
            .Where(x => !string.IsNullOrWhiteSpace(x.Singular.RawString))
            .ToDictionary(x => x.RowId, x => x);
    }

    public void Dispose()
    {
        Exec.Dispose();
    }

    public override void PreOpenCheck()
    {
        Exec.Update();
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem("路线"))
            {
                if (tab)
                {
                    DrawExecution();
                    ImGui.Separator();
                    ImGui.Spacing();

                    var cra = ImGui.GetContentRegionAvail();
                    var sidebar = cra with { X = cra.X * 0.40f };
                    var editor = cra with { X = cra.X * 0.60f };

                    DrawSidebar(sidebar);
                    ImGui.SameLine();
                    DrawEditor(editor);

                    foreach (var a in _postDraw)
                        a();
                    _postDraw.Clear();
                }
            }

            using (var tab = ImRaii.TabItem("Debug"))
            {
                if (tab)
                    _debug.Draw();
            }
        }
    }

    private void DrawExecution()
    {
        ImGui.Text("状态: ");
        ImGui.SameLine();

        if (Exec.CurrentRoute != null)
            Utils.FlashText($"{(Exec.Paused ? "暂停中" : Exec.Waiting ? "等待中" : "运行中")}",
                new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                Exec.Paused ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), 2);
        ImGui.SameLine();

        if (Exec.CurrentRoute == null || Exec.CurrentWaypoint >= Exec.CurrentRoute.Waypoints.Count)
        {
            ImGui.Text("当前无运行中路线");
            return;
        }

        if (Exec.CurrentRoute != null) // Finish() call could've reset it
        {
            ImGui.SameLine();
            ImGui.Text($"{Exec.CurrentRoute.Name}: 步骤 #{Exec.CurrentWaypoint + 1}");

            if (Exec.Waiting)
            {
                ImGui.SameLine();
                ImGui.Text($"等待 {Exec.WaitUntil - Environment.TickCount64}ms");
            }
        }
    }

    private void DrawSidebar(Vector2 size)
    {
        using (ImRaii.Child("Sidebar", size, false))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                RouteDB.Routes.Add(new Route { Name = "未命名路线" });
                RouteDB.NotifyModified();
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("创建");
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileImport))
                try
                {
                    var import = JsonConvert.DeserializeObject<Route>(ImGui.GetClipboardText());
                    RouteDB.Routes.Add(new() { Name = import!.Name, Group = import.Group, Waypoints = import.Waypoints });
                    RouteDB.NotifyModified();
                }
                catch (JsonReaderException ex)
                {
                    Service.ChatGui.PrintError($"导入路线失败: {ex.Message}");
                }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("从剪贴板导入路线 (\uE052 Base64)");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    try
                    {

                        var import = JsonConvert.DeserializeObject<Route>(Utils.FromCompressedBase64(ImGui.GetClipboardText()));
                        RouteDB.Routes.Add(new() { Name = import!.Name, Group = import.Group, Waypoints = import.Waypoints });
                        RouteDB.NotifyModified();
                    }
                    catch (JsonReaderException ex)
                    {
                        Service.ChatGui.PrintError($"导入路线失败: {ex.Message}");
                    }
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog)) ImGui.OpenPopup("Advanced Options");
            DrawRouteSettingsPopup();
            ImGui.SameLine();
            if (ImGui.Checkbox("发生错误时停止路线", ref RouteDB.DisableOnErrors))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker("因物品达到上限而无法采集时, 停止路线运行");

            ImGuiEx.TextV("搜索: ");
            ImGui.SameLine();
            ImGuiEx.SetNextItemFullWidth();
            if (ImGui.InputText("###RouteSearch", ref searchString, 500))
            {
                FilteredRoutes.Clear();
                if (searchString.Length > 0)
                    foreach (var route in RouteDB.Routes)
                    {
                        if (route.Name.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase) ||
                            route.Group.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase))
                            FilteredRoutes.Add(route);
                    }
            }

            ImGui.Separator();

            using (ImRaii.Child("routes"))
            {
                List<string> groups = [];
                for (var g = 0; g < (FilteredRoutes.Count > 0 ? FilteredRoutes.Count : RouteDB.Routes.Count); g++)
                {
                    var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
                    if (string.IsNullOrEmpty(routeSource[g].Group))
                    {
                        routeSource[g].Group = "None";
                    }
                    if (!groups.Contains(routeSource[g].Group))
                    {
                        groups.Add(routeSource[g].Group);
                    }
                }
                groups = [.. groups.OrderBy(i => i == "None").ThenBy(i => i)]; //Sort with None at the End
                foreach (var group in groups)
                {
                    foreach (var wn in _tree.Node($"{group}###{groups.IndexOf(group)}"))
                    {
                        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
                        for (var i = 0; i < routeSource.Count; i++)
                        {
                            var route = routeSource[i];
                            var routeGroup = string.IsNullOrEmpty(route.Group) ? "None" : route.Group;
                            if (routeGroup == group)
                            {
                                var selectedRoute = ImGui.Selectable($"{route.Name} (共 {route.Waypoints.Count} 步)###{i}", i == selectedRouteIndex);
                                if (selectedRoute)
                                    selectedRouteIndex = i;
                            }
                        }
                    }
                }
            }
        }
    }

    private void DrawRouteSettingsPopup()
    {
        using var popup = ImRaii.Popup("Advanced Options");
        if (popup.Success)
        {
            if (ImGui.SliderFloat("默认步骤半径", ref RouteDB.DefaultWaypointRadius, 0, 100))
                RouteDB.NotifyModified();
            if (ImGui.SliderFloat("默认交互半径", ref RouteDB.DefaultInteractionRadius, 0, 100))
                RouteDB.NotifyModified();
            if (ImGui.BeginCombo("默认坐骑", RouteDB.SelectedMount <= 0 ? "随机坐骑" : _mounts![(uint)RouteDB.SelectedMount].Singular.RawString))
            {
                ImGui.InputText("###MountSearchInput", ref mountSearchString, 100);

                ImGui.Separator();

                if (ImGui.Selectable("随机坐骑", RouteDB.SelectedMount <= 0))
                {
                    RouteDB.SelectedMount = 0;
                    RouteDB.NotifyModified();
                }

                ImGui.Separator();

                foreach (var mount in _mounts!)
                {
                    var mountName = mount.Value.Singular.RawString;
                    if (!string.IsNullOrWhiteSpace(mountSearchString) && !mountName.Contains(mountSearchString, StringComparison.OrdinalIgnoreCase)) continue;

                    if (ImGui.Selectable(mount.Value.Singular.RawString, RouteDB.SelectedMount == mount.Key))
                    {
                        RouteDB.SelectedMount = (int)mount.Key;
                        RouteDB.NotifyModified();
                    }
                }

                ImGui.EndCombo();
            }

            if (ImGui.Checkbox("开始时自动切换至采集模式", ref RouteDB.GatherModeOnStart))
                RouteDB.NotifyModified();
        }
    }

    private void DrawEditor(Vector2 size)
    {
        if (selectedRouteIndex == -1) return;

        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
        if (routeSource.Count == 0) return;
        var route = selectedRouteIndex >= routeSource.Count ? routeSource.Last() : routeSource[selectedRouteIndex];

        using (ImRaii.Child("Editor", size))
        {
            using (ImRaii.Disabled(Exec.CurrentRoute != null))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Play))
                    Exec.Start(route, 0, true, loop, route.Waypoints[0].Pathfind);
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("运行");
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, loop ? greenColor : redColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, loop ? greenColor : redColor);
            if (ImGuiComponents.IconButton(FontAwesomeIcon.SyncAlt))
                loop ^= true;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("循环");
            ImGui.SameLine();

            if (Exec.CurrentRoute != null)
            {
                if (ImGuiEx.IconButton(Exec.Paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause))
                    Exec.Paused = !Exec.Paused;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Exec.Paused ? "继续" : "暂停");
                ImGui.SameLine();

                if (ImGuiComponents.IconButton(FontAwesomeIcon.Stop))
                    Exec.Finish();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止");
                ImGui.SameLine();
            }

            var canDelete = !ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(canDelete))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                {
                    if (Exec.CurrentRoute == route)
                        Exec.Finish();
                    RouteDB.Routes.Remove(route);
                    RouteDB.NotifyModified();
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("删除 (按住 CTRL)");
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileExport))
                ImGui.SetClipboardText(JsonConvert.SerializeObject(route));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("导出路线 (\uE052 Base64)");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.SetClipboardText(Utils.ToCompressedBase64(route));
            }

            var name = route.Name;
            
            var group = route.Group;
            var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly : Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
            ImGuiEx.TextV("名称: ");
            ImGui.SameLine();
            if (ImGui.InputText("##name", ref name, 256))
            {
                route.Name = name;
                RouteDB.NotifyModified();
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                Exec.Finish();
                var player = Service.ClientState.LocalPlayer;
                if (player != null)
                {
                    route.Waypoints.Add(new Waypoint
                    {
                        Position = player.Position, Radius = RouteDB.DefaultWaypointRadius,
                        ZoneID = Service.ClientState.TerritoryType, Movement = movementType
                    });
                    RouteDB.NotifyModified();
                }
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("+步骤: 移动至当前位置");
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    route.Waypoints.Add(new Waypoint
                    {
                        Position = target.Position, Radius = RouteDB.DefaultInteractionRadius,
                        ZoneID = Service.ClientState.TerritoryType, Movement = movementType,
                        InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower()
                    });
                    RouteDB.NotifyModified();
                    Exec.Start(route, route.Waypoints.Count - 1, false, false);
                }
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("+步骤: 与目标交互");

            ImGuiEx.TextV("Group: ");
            ImGui.SameLine();
            if (ImGui.InputText("##group", ref group, 256))
            {
                route.Group = group;
                RouteDB.NotifyModified();
            }

            using (ImRaii.Child("waypoints"))
            {
                for (var i = 0; i < route.Waypoints.Count; ++i)
                {
                    var wp = route.Waypoints[i];
                    foreach (var wn in _tree.Node(
                                 $"#{i + 1}: [X: {wp.Position.X:f0}, Y: {wp.Position.Y:f0}, Z: {wp.Position.Z:f0}] ({wp.Movement}){(wp.InteractWithOID != 0 ? $" @ {wp.InteractWithName} ({wp.InteractWithOID:X})" : "")}###{i}",
                                 contextMenu: () => ContextMenuWaypoint(route, i))) DrawWaypoint(wp);
                }
            }
        }
    }

    private void DrawWaypoint(Waypoint wp)
    {
        if (ImGuiEx.IconButton(FontAwesomeIcon.MapMarker) && Service.ClientState.LocalPlayer is var player &&
            player != null)
        {
            wp.Position = player.Position;
            wp.ZoneID = Service.ClientState.TerritoryType;
            RouteDB.NotifyModified();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("设置为当前位置");
        ImGui.SameLine();
        if (ImGui.InputFloat3("位置", ref wp.Position))
            RouteDB.NotifyModified();
        if (UICombo.ExcelSheetCombo("##Territory", out TerritoryType? territory, _ => $"{wp.ZoneID}",
                x => x.PlaceName.Value!.Name, x => Coordinates.HasAetheryteInZone(x.RowId)))
        {
            wp.ZoneID = (int)territory.RowId;
            RouteDB.NotifyModified();
        }

        if (ImGui.InputFloat("半径 (y)", ref wp.Radius))
            RouteDB.NotifyModified();
        if (UICombo.Enum("移动模式", ref wp.Movement))
            RouteDB.NotifyModified();
        ImGui.SameLine();
        using (var noNav = ImRaii.Disabled(!Utils.HasPlugin(NavmeshIPC.Name)))
        {
            if (ImGui.Checkbox("寻路?", ref wp.Pathfind))
                RouteDB.NotifyModified();
        }

        if (!Utils.HasPlugin(NavmeshIPC.Name))
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"此功能需要安装并启用 {NavmeshIPC.Name}");

        if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
        {
            if (wp.InteractWithOID == default)
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    wp.InteractWithName = target.Name.ToString().ToLower();
                    wp.InteractWithOID = target.DataId;
                    RouteDB.NotifyModified();
                }
            }
            else
            {
                wp.InteractWithOID = default;
            }
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("为步骤添加/移除目标");
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.CommentDots))
        {
            wp.showInteractions ^= true;
            RouteDB.NotifyModified();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("切换至交互");
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Clock))
        {
            wp.showWaits ^= true;
            RouteDB.NotifyModified();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("切换至等待");

        if (wp.showInteractions)
        {
            if (UICombo.Enum("交互方式", ref wp.Interaction))
                RouteDB.NotifyModified();
            switch (wp.Interaction)
            {
                case InteractionType.None: break;
                case InteractionType.Standard: break;
                case InteractionType.Emote:
                    if (UICombo.ExcelSheetCombo("##Emote", out Emote? emote, _ => $"{wp.EmoteID}",
                            x => $"[{x.RowId}] {x.Name}", x => !x.Name.RawString.IsNullOrEmpty()))
                    {
                        wp.EmoteID = (int)emote.RowId;
                        RouteDB.NotifyModified();
                    }

                    break;
                case InteractionType.UseItem:
                    ImGui.PushItemWidth(100);
                    if (ImGui.DragInt($"物品 {_items.GetRow((uint)wp.ItemID)?.Name}###{nameof(InteractionType.UseItem)}",
                            ref wp.ItemID, 1, Items.First(), Items.Last()))
                        RouteDB.NotifyModified();
                    break;
                case InteractionType.UseAction:
                    if (UICombo.ExcelSheetCombo("##Action", out Lumina.Excel.GeneratedSheets.Action? action,
                            _ => $"{wp.ActionID}", x => $"[{x.RowId}] {x.Name}",
                            x => x.ClassJobCategory.Row > 0 && x.ActionCategory.Row <= 4 && x.RowId > 8))
                    {
                        wp.ActionID = (int)action.RowId;
                        RouteDB.NotifyModified();
                    }

                    break;
                //case InteractionType.PickupQuest:
                //    if (UICombo.ExcelSheetCombo("##PickupQuest", ref wp.QuestID, UICombo.questComboOptions))
                //        RouteDB.NotifyModified();
                //    break;
                //case InteractionType.TurninQuest:
                //    if (UICombo.ExcelSheetCombo("##TurninQuest", ref wp.QuestID, UICombo.questComboOptions))
                //        RouteDB.NotifyModified();
                //    break;
                case InteractionType.Grind:
                    using (var noVbm = ImRaii.Disabled(!Utils.HasPlugin(BossModIPC.Name)))
                    {
                        if (UICombo.ExcelSheetCombo("##Mob", out BNpcName? mob, _ => $"{wp.EmoteID}",
                                x => $"[{x.RowId}] {x.Singular}", x => !x.Singular.RawString.IsNullOrEmpty()))
                        {
                            wp.MobID = (int)mob.RowId;
                            RouteDB.NotifyModified();
                        }
                    }

                    if (!Utils.HasPlugin(BossModIPC.Name))
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip($"此功能需要安装并启用 {BossModIPC.Name}.");

                    if (wp.MobID != default)
                    {
                        if (UICombo.Enum("Grind Condition", ref wp.StopCondition))
                            RouteDB.NotifyModified();
                        switch (wp.StopCondition)
                        {
                            case GrindStopConditions.None: break;
                            case GrindStopConditions.Kills:
                                ImGui.PushItemWidth(100);
                                if (Utils.EditNumberField("Kill", 25, ref wp.KillCount, " times"))
                                    RouteDB.NotifyModified();
                                break;
                            case GrindStopConditions.QuestSequence:
                                if (UICombo.ExcelSheetCombo("##QuestSequence", out Quest? qs, _ => $"{wp.QuestID}",
                                        x => $"[{x.RowId}] {x.Name}", x => x.Id.RawString.Length > 0))
                                {
                                    wp.QuestID = (int)qs.RowId;
                                    RouteDB.NotifyModified();
                                }

                                ImGui.SameLine();
                                if (Utils.EditNumberField("Sequence = ", 25, ref wp.QuestSeq))
                                    RouteDB.NotifyModified();
                                break;
                            case GrindStopConditions.QuestComplete:
                                if (UICombo.ExcelSheetCombo("##QuestComplete", out Quest? qc, _ => $"{wp.QuestID}",
                                        x => $"[{x.RowId}] {x.Name}", x => x.Id.RawString.Length > 0))
                                {
                                    wp.QuestID = (int)qc.RowId;
                                    RouteDB.NotifyModified();
                                }

                                break;
                        }
                    }

                    break;
                case InteractionType.EquipRecommendedGear: break;
                case InteractionType.StartRoute:
                    if (UICombo.String("路线名称", RouteDB.Routes.Select(r => r.Name).ToArray(), ref wp.RouteName))
                        RouteDB.NotifyModified();
                    break;
            }
        }

        if (wp.showWaits)
        {
            if (ImGui.SliderInt("等待 (ms)", ref wp.WaitTimeMs, 0, 60000))
                RouteDB.NotifyModified();
            if (UICombo.Enum("等待条件满足", ref wp.WaitForCondition))
                RouteDB.NotifyModified();
        }
    }

    private void ContextMenuWaypoint(Route r, int i)
    {
        if (ImGui.MenuItem("仅执行此步")) Exec.Start(r, i, false, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem("从此步开始执行路线一次")) Exec.Start(r, i, true, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem("从此步开始循环执行路线")) Exec.Start(r, i, true, true, r.Waypoints[i].Pathfind);

        var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly :
            Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
        var target = Service.TargetManager.Target;

        if (ImGui.MenuItem($"切换至 {(r.Waypoints[i].InteractWithOID != default ? "移动步骤" : "交互步骤")}"))
            _postDraw.Add(() =>
            {
                r.Waypoints[i].InteractWithOID =
                    r.Waypoints[i].InteractWithOID != default ? default : target?.DataId ?? default;
                RouteDB.NotifyModified();
            });

        if (ImGui.MenuItem("在上方插入"))
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ClientState.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i,
                            new Waypoint
                            {
                                Position = Service.ClientState.LocalPlayer.Position,
                                Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType,
                                Movement = movementType
                            });
                        RouteDB.NotifyModified();
                    }
                }
            });
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i,
                            new Waypoint
                            {
                                Position = target.Position, Radius = RouteDB.DefaultInteractionRadius,
                                ZoneID = Service.ClientState.TerritoryType, Movement = movementType,
                                InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower()
                            });
                        RouteDB.NotifyModified();
                    }
                }
            });

        if (ImGui.MenuItem("在下方插入"))
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ClientState.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i + 1,
                            new Waypoint
                            {
                                Position = Service.ClientState.LocalPlayer.Position,
                                Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType,
                                Movement = movementType
                            });
                        RouteDB.NotifyModified();
                    }
                }
            });
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i + 1,
                            new Waypoint
                            {
                                Position = target.Position, Radius = RouteDB.DefaultInteractionRadius,
                                ZoneID = Service.ClientState.TerritoryType, Movement = movementType,
                                InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower()
                            });
                        RouteDB.NotifyModified();
                    }
                }
            });

        if (ImGui.MenuItem("上移"))
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i - 1, wp);
                    RouteDB.NotifyModified();
                }
            });

        if (ImGui.MenuItem("下移"))
            _postDraw.Add(() =>
            {
                if (i + 1 < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i + 1, wp);
                    RouteDB.NotifyModified();
                }
            });

        if (ImGui.MenuItem("删除"))
            _postDraw.Add(() =>
            {
                if (i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    r.Waypoints.RemoveAt(i);
                    RouteDB.NotifyModified();
                }
            });
    }
}