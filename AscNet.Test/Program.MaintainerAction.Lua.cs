using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>Runs the ORIGINAL EN Operation Guardians Lua consumers headlessly against server payloads.</summary>
    private static void MaintainerActionOriginalLua(
        JObject login, JObject dailyReset, JObject fightWin, JObject nodeChange,
        List<JObject> nodeEventNodes,
        List<(int From, bool Reverse, int Card, int Target)> moves,
        uint playerId, JArray records, JArray flow)
    {
        string? root = Environment.GetEnvironmentVariable("ASCNET_PGR_EN_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = new[]
            {
                Path.Combine("..", "..", "PGR_Data", "en"),
                "/Volumes/Lucia/PGR-native-research/PGR_Data/en",
                "/PGR-native-research/PGR_Data/en"
            }.FirstOrDefault(path => Directory.Exists(Path.Combine(path, "lua", "matrix")));
        if (root is null || !Directory.Exists(Path.Combine(root, "lua", "matrix")))
            throw new DirectoryNotFoundException("Original EN dump required: set ASCNET_PGR_EN_ROOT to the PGR_Data/en directory and ASCNET_LUA to the Lua executable.");
        root = Path.GetFullPath(root);
        string matrix = Path.Combine(root, "lua", "matrix");

        string Literal(JToken? token) => token switch
        {
            JObject obj => "{" + string.Join(",", obj.Properties().Select(property => $"[{Literal(new JValue(property.Name))}]={Literal(property.Value)}")) + "}",
            JArray array => "{" + string.Join(",", array.Select(Literal)) + "}",
            null => "nil",
            _ when token.Type == JTokenType.Null => "nil",
            _ when token.Type == JTokenType.String => "\"" + token.Value<string>()!.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"",
            _ when token.Type == JTokenType.Boolean => token.Value<bool>() ? "true" : "false",
            _ => token.ToString(Newtonsoft.Json.Formatting.None)
        };

        // lowercase module name -> real file; the EN engine resolves require() case-insensitively
        string modules = "{" + string.Join(",", Directory.EnumerateFiles(Path.Combine(matrix, "xentity", "xmaintaineraction"), "*.lua")
            .Append(Path.Combine(matrix, "xcommon", "Json.lua"))
            .Select(file => $"[{Literal(new JValue(Path.GetRelativePath(matrix, file).Replace('\\', '/')[..^4].ToLowerInvariant()))}]={Literal(new JValue(file))}")) + "}";

        string script = """
            XMain = {IsEditorDebug = false}
            XLog = {Error = function(m) error("XLog.Error: " .. tostring(m), 2) end, Warning = function() end, Debug = function() end}
            XTool = {InitUiObject = function() end, IsTableEmpty = function(t) return t == nil or next(t) == nil end}
            table.insert(package.searchers, 2, function(name)
                local file = MODULES[name:lower()]
                if file then return assert(loadfile(file)), file end
            end)
            local function eq(a, b, msg) if a ~= b then error(msg .. ": expected " .. tostring(b) .. " got " .. tostring(a), 2) end end
            local function num(v, msg) if math.type(v) == nil then error(msg .. ": not a number: " .. tostring(v), 2) end end

            local NOW = os.time()
            local timers, removed, events, tips = {}, {}, {}, {}
            XTime = {GetServerNowTimestamp = function() return NOW end,
                TimestampToGameDateTimeString = function(t) num(t, "record Time") return tostring(t) end}
            XCountDown = {CreateTimer = function(n, s) timers[#timers + 1] = {n, s} end, RemoveTimer = function(n) removed[#removed + 1] = n end}
            XEventId = {EVENT_MAINTAINERACTION_WEEK_UPDATA = "week", EVENT_MAINTAINERACTION_DAY_UPDATA = "day", EVENT_MAINTAINERACTION_NODE_CHANGE = "node"}
            XEventManager = {DispatchEvent = function(e) events[#events + 1] = e end}
            CS = {XTextManager = {GetText = function(k) return k end}, XGame = {ClientConfig = {}}}
            XUiManager = {TipMsg = function(msg, _, cb) tips[#tips + 1] = msg if cb then cb() end end, TipText = function() end, TipCode = function() end}
            XLuaUiManager = {Open = function() end, IsUiShow = function() return false end, RunMain = function() end}
            XScheduleManager = {ScheduleOnce = function() end}
            XScheduleManagerPro = {ScheduleOnce = function() end}
            XFunctionManager = {GetTimeByTimeId = function() return 0, 0 end, GetStartTimeByTimeId = function() return 0 end}
            local saved = {}
            XSaveTool = {GetData = function(k) return saved[k] end, SaveData = function(k, v) saved[k] = v end, RemoveData = function(k) saved[k] = nil end}
            XDataCenter = {FubenManager = {
                GetStageCfg = function(id) num(id, "stage id") return {Name = "stage" .. id, FinishRewardShow = 0} end,
                GetStageLevelControl = function() return nil end}}
            XPlayer = {Id = PLAYER, Name = "player"}
            XRpc = {}
            XTable = setmetatable({}, {__index = function(_, k) return k end})
            local Json = require("XCommon/Json")
            XTableManager = {ReadByIntKey = function(path, _, key)
                local f = assert(io.open(DATA .. path:match("([^/]+)%.tab$") .. ".json", "rb"))
                local rows, out = Json.decode(f:read("a")), {}
                f:close()
                for _, row in ipairs(rows) do out[row[key] or 0] = row end
                return out
            end}

            assert(loadfile(MATRIX .. "/xcommon/XClass.lua"))()
            assert(loadfile(MATRIX .. "/xconfig/XMaintainerActionConfigs.lua"))()
            XMaintainerActionConfigs.Init()
            assert(loadfile(MATRIX .. "/xmanager/XMaintainerActionManager.lua"))()
            local mgr = XMaintainerActionManagerCreator()
            XDataCenter.MaintainerActionManager = mgr
            local NT = XMaintainerActionConfigs.NodeType
            local CLASS = {[NT.UnKnow] = "XUnKnowNodeEntity", [NT.Start] = "XStartNodeEntity", [NT.Fight] = "XFightNodeEntity", [NT.Box] = "XBoxNodeEntity",
                [NT.None] = "XNoneNodeEntity", [NT.Forward] = "XForwardNodeEntity", [NT.FallBack] = "XFallBackNodeEntity", [NT.CardChange] = "XCardChangeNodeEntity",
                [NT.DirectionChange] = "XDirectionChangeNodeEntity", [NT.ActionPoint] = "XExtraActionNodeEntity", [NT.SimulationFight] = "XSimulationFightNodeEntity",
                [NT.Warehouse] = "XWarehouseNodeEntity", [NT.Explore] = "XExploreNodeEntity", [NT.Mentor] = "XMentorNodeEntity"}
            local function count(t) local n = 0 for _ in pairs(t) do n = n + 1 end return n end
            local function checkType(ent, node, what)
                assert(ent and ent.__cname, what .. ": no entity for node " .. tostring(node.NodeId))
                eq(ent.__cname, CLASS[node.NodeType], what .. " class of node " .. node.NodeId .. " type " .. node.NodeType)
                eq(ent:GetType(), node.NodeType, what .. " NodeType")
                eq(ent:GetId(), node.NodeId, what .. " NodeId")
            end
            local function exercise(ent)
                assert(type(ent:GetCfg()) == "table", "GetCfg")
                assert(type(ent:GetName()) == "string", "GetName of event " .. tostring(ent.EventId))
                ent:GetDesc() ent:GetHint() ent:GetEventIcon()
            end

            -- 1. NotifyMaintainerActionData
            XRpc.NotifyMaintainerActionData(LOGIN)
            assert(mgr.GetPlayerDic()[PLAYER], "PlayerDic lacks player")
            assert(mgr.GetPlayerMySelf() == mgr.GetPlayerDic()[PLAYER], "GetPlayerMySelf")
            eq(count(mgr.GetMapNodeList()), #LOGIN.Nodes, "MapNodeList size")
            for _, node in ipairs(LOGIN.Nodes) do
                local ent = mgr.GetMapNodeById(node.NodeId)
                checkType(ent, node, "login")
                exercise(ent)
            end
            local start = mgr.GetMapNodeById(0)
            assert(start and start:GetType() == NT.Start, "start node (type 1, id 0) missing")
            local gd = mgr.GetGameData()
            assert(type(gd:GetCards()) == "table", "Cards")
            for _, g in ipairs({"GetFightWinCount", "GetBoxCount", "GetUsedActionCount", "GetExtraActionCount", "GetWarehouseFinishCount", "GetMentorStatus",
                "GetMaxDailyActionCount", "GetMaxFightWinCount", "GetMaxBoxCount", "GetMaxWarehouseFinishCount"}) do num(gd[g](gd), g) end
            gd:GetHasWarehouseNode() gd:GetHasMentorNode()
            mgr.CheckIsActionPointOver() mgr.CheckIsFightComplete() mgr.CheckIsBoxComplete() mgr.CheckIsWarehouseComplete() mgr.CheckIsAllComplete()
            local timer = timers[#timers]
            assert(timer and timer[1] == mgr.GetResetCountDownName(), "reset timer not created")
            eq(timer[2], LOGIN.ResetTime - NOW, "reset timer seconds")
            assert(mgr.GetMessageTypeList()[XMaintainerActionConfigs.MessageType.WeekUpdate], "WeekUpdate not registered")
            eq(events[#events], "week", "week event")

            -- 2. DailyReset
            num(DAILY.UsedActionCount, "DailyReset.UsedActionCount") num(DAILY.ExtraActionCount, "DailyReset.ExtraActionCount")
            XRpc.NotifyMaintainerActionDailyReset(DAILY)
            eq(gd:GetUsedActionCount(), DAILY.UsedActionCount, "UsedActionCount")
            eq(gd:GetExtraActionCount(), DAILY.ExtraActionCount, "ExtraActionCount")
            assert(mgr.GetMessageTypeList()[XMaintainerActionConfigs.MessageType.DayUpdate], "DayUpdate not registered")
            eq(events[#events], "day", "day event")

            -- 3. FightWin
            num(FIGHT.FightWinCount, "FightWin.FightWinCount")
            mgr.GetPlayerMySelf():UnMarkNodeEvent()
            XRpc.NotifyMaintainerActionFightWin(FIGHT)
            eq(gd:GetFightWinCount(), FIGHT.FightWinCount, "FightWinCount")
            eq(mgr.GetPlayerMySelf():GetIsNodeTriggered(), true, "IsNodeTriggered")

            -- 4. NodeChange
            XRpc.NotifyMaintainerActionNodeChange(CHANGE)
            local changed = mgr.GetMapNodeById(CHANGE.Node.NodeId)
            checkType(changed, CHANGE.Node, "nodeChange")
            exercise(changed)
            eq(events[#events], "node", "node event")

            -- 5. NodeEvent responses
            local function clean(s, what)
                assert(type(s) == "string", what .. " not string")
                assert(not s:find("%%"), what .. " has '%' leftover: " .. s)
                assert(not s:find("nil", 1, true), what .. " has nil: " .. s)
            end
            for _, node in ipairs(EVENTS) do
                local ent = mgr.CreateNode(node)
                ent:UpdateData(node)
                checkType(ent, node, "nodeEvent")
                exercise(ent)
                local t = ent.NodeType
                if t == NT.Forward or t == NT.FallBack then num(ent:GetStep(), "Step") num(ent:GetTargetNodeId(), "TargetNodeId")
                elseif t == NT.CardChange then num(ent:GetOldCard(), "OldCard") num(ent:GetNewCard(), "NewCard")
                elseif t == NT.Start or t == NT.ActionPoint then num(ent:GetExtraActionCount(), "ExtraActionPoint")
                elseif t == NT.Box or t == NT.Warehouse then num(ent:GetRewardId(), "RewardId")
                elseif t == NT.Fight then num(ent:GetStageId(), "StageId") num(ent:GetRewardId(), "RewardId")
                elseif t == NT.SimulationFight then
                    assert(type(ent:GetRewardGoodsList()) == "table", "RewardGoodsList") num(ent:GetStageId(), "SimulationFight StageId")
                elseif t == NT.Mentor then num(ent:GetMentorStatus(), "MentorStatus")
                elseif t == NT.Explore then
                    local inner = ent:GetNode()
                    assert(type(inner) == "table", "Explore Node") num(inner.NodeId, "Explore Node.NodeId") num(inner.NodeType, "Explore Node.NodeType") num(inner.EventId, "Explore Node.EventId")
                    if inner.Value ~= nil then assert(type(inner.Value) == "string" and Json.decode(inner.Value) ~= nil, "Explore Node.Value not decodable") end
                    local revealed = mgr.CreateNode(inner)
                    checkType(revealed, inner, "explore inner")
                    exercise(revealed)
                    revealed:OpenHintTip(function() end)
                end
                local desc = ent:GetDesc()
                if desc ~= nil then clean(desc, "GetDesc of node " .. node.NodeId) end
                clean(ent:GetName(), "GetName of node " .. node.NodeId)
                local before, called = #tips, false
                ent:OpenHintTip(function() called = true end)
                assert(called, "OpenHintTip callback not called")
                if ent:GetHint() then
                    eq(#tips, before + 1, "OpenHintTip recorded")
                    clean(tips[#tips], "hint of node " .. node.NodeId)
                end
            end

            -- 6. Moves via the real XUiPanelIntermediate.CreateRoute
            package.preload["XUi/XUiMaintainerAction/XUiGridMapNote"] = function() return {} end
            package.preload["XUi/XUiMaintainerAction/XUiGridPlayer"] = function() return {} end
            local XUiPanelIntermediate = assert(loadfile(MATRIX .. "/xui/xuimaintaineraction/XUiPanelIntermediate.lua"))()
            local XPlayerEntity = require("XEntity/XMaintainerAction/XMaintainerActionPlayerEntity")
            local FAKE = -1
            for i, m in ipairs(MOVES) do
                local p = XPlayerEntity.New(FAKE)
                p:UpdateData({NodeId = m[1], Reverse = m[2]})
                mgr.GetPlayerDic()[FAKE] = p
                local route = XUiPanelIntermediate.CreateRoute({}, FAKE, m[3], nil, 2)
                mgr.GetPlayerDic()[FAKE] = nil
                assert(route and #route > 0, "move " .. i .. " empty route")
                eq(route[#route], m[4], "move " .. i .. " (from " .. m[1] .. " reverse " .. tostring(m[2]) .. " card " .. m[3] .. ") route target")
            end

            -- 7. Records via the real XUiGridRecord.UpdateGrid
            local XUiGridRecord = assert(loadfile(MATRIX .. "/xui/xuimaintaineraction/XUiGridRecord.lua"))()
            local grid = XUiGridRecord.New({gameObject = 1, transform = 1})
            grid.TextTime, grid.TextRecording = {}, {}
            for i, rec in ipairs(RECORDS) do
                assert(type(rec.EventValues) == "table", "record " .. i .. " EventValues")
                if XMaintainerActionConfigs.IsFightEvent(rec.EventId) then eq(type(rec.EventValues[1]), "number", "record " .. i .. " fight stage id type") end
                grid:UpdateGrid(rec)
                clean(grid.TextRecording.text, "record " .. i)
            end


            -- 8. UI-driven landing flow: real manager + entities + XUiFubenMaintaineraction.UsedCard/CheckEvent against recorded server exchanges
            XCode = {Success = 0}
            local queue = {}
            XNetwork = {Call = function(name, req, cb)
                local item = table.remove(queue, 1)
                assert(item, "client sent unexpected request " .. name)
                eq(name, item.Name, "client request name")
                if item.Card then eq(req.Card, item.Card, "PlayCard request body") end
                cb(item.Response)
            end}
            local uiOpen = {}
            local UiClass = {}
            XLuaUiManager.Register = function() return UiClass end
            XLuaUiManager.SetMask = function() end
            XLuaUiManager.Open = function(name, ...)
                uiOpen[#uiOpen + 1] = name
                if name == "UiFubenMaintaineractionTreasurechest" then
                    local args = {...}
                    local rewardId, list, closeCb = args[1], args[2], args[6]
                    if rewardId then assert(rewardId > 0, "treasure chest reward id") end
                    local fn = args[7] or closeCb
                    if type(fn) == "function" then fn() end
                end
            end
            package.preload["XUi/XUiCommon/XUiPanelAsset"] = function() return {} end
            package.preload["XUi/XUiMaintainerAction/XUiPanelBelow"] = function() return {} end
            package.preload["XUi/XUiMaintainerAction/XUiPanelIntermediate"] = function() return {} end
            XDataCenter.ItemManager = {ItemId = {}}
            assert(loadfile(MATRIX .. "/xui/xuimaintaineraction/XUiFubenMaintaineraction.lua"))()
            local Ui = UiClass
            for si, seg in ipairs(FLOW) do
                XRpc.NotifyMaintainerActionData(seg.Login)
                local ui = setmetatable({CardRouteList = {}}, {__index = Ui})
                ui.UpdatePanel = function() end
                local me = mgr.GetPlayerMySelf()
                ui.BelowPanel = {GetNewCard = function() end, ChangeCard = function(_, old, new, cb) mgr.GetGameData():CardChange(old, new) cb() end}
                local function move(_, id, target, cb) me:MoveTo(target) me:UnMarkNodeEvent() cb() end
                ui.IntermediatePanel = {MovePlayerById = move, ReverseMovePlayerById = move}
                for ki, step in ipairs(seg.Steps) do
                    local where = "board " .. si .. " step " .. ki .. " (" .. step.Kind .. ")"
                    for _, r in ipairs(step.Responses or {}) do queue[#queue + 1] = r end
                    local gd = mgr.GetGameData()
                    if step.Kind == "card" then
                        assert(not mgr.CheckIsActionPointOver(), where .. ": client would refuse the card (action points)")
                        mgr.PlayerMoveRequest(step.Card, 1, function(data)
                            gd:CardChange(step.Card, data.NewCard)
                            gd:UpdateData({UsedActionCount = gd:GetUsedActionCount() + 1})
                            ui:UsedCard(data.NodeId)
                        end)
                    elseif step.Kind == "event" then
                        ui:CheckEvent(me:GetPosNodeId(), false, function() end)
                    elseif step.Kind == "fightwin" then
                        XRpc.NotifyMaintainerActionFightWin(step.Push)
                    elseif step.Kind == "daily" then
                        XRpc.NotifyMaintainerActionDailyReset(step.Push)
                    end
                    eq(#queue, 0, where .. ": server responses the client never consumed")
                    local e = step.Expect
                    eq(me:GetPosNodeId(), e.Node, where .. " client node")
                    eq(me:GetIsNodeTriggered(), e.Triggered, where .. " client IsNodeTriggered")
                    eq(me:GetIsReverse(), e.Reverse, where .. " client Reverse")
                    eq(table.concat(gd:GetCards(), ","), table.concat(e.Cards, ","), where .. " client hand")
                    eq(gd:GetUsedActionCount(), e.Used, where .. " used actions")
                    eq(gd:GetExtraActionCount(), e.Extra, where .. " extra actions")
                    eq(gd:GetFightWinCount(), e.Fight, where .. " fight wins")
                    eq(gd:GetBoxCount(), e.Box, where .. " boxes")
                    eq(gd:GetWarehouseFinishCount(), e.Warehouse, where .. " warehouse count")
                end
            end
            print(string.format("Original EN MaintainerAction consumers passed: %d login nodes, %d event nodes, %d moves, %d records", #LOGIN.Nodes, #EVENTS, #MOVES, #RECORDS))
            """;
        script = "local MATRIX=" + Literal(new JValue(matrix))
            + "\nlocal DATA=" + Literal(new JValue(Path.Combine(root, "bytes", "share", "fuben", "maintaineraction") + "/"))
            + "\nlocal MODULES=" + modules
            + "\nlocal PLAYER=" + playerId
            + "\nlocal LOGIN=" + Literal(login)
            + "\nlocal DAILY=" + Literal(dailyReset)
            + "\nlocal FIGHT=" + Literal(fightWin)
            + "\nlocal CHANGE=" + Literal(nodeChange)
            + "\nlocal EVENTS=" + Literal(new JArray(nodeEventNodes))
            + "\nlocal MOVES=" + Literal(new JArray(moves.Select(m => new JArray(m.From, m.Reverse, m.Card, m.Target))))
            + "\nlocal RECORDS=" + Literal(records)
            + "\nlocal FLOW=" + Literal(flow) + "\n" + script;
        ProcessStartInfo info = new(Environment.GetEnvironmentVariable("ASCNET_LUA") ?? "lua", "-")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch original EN Lua consumer.");
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Original EN MaintainerAction Lua failed ({process.ExitCode}): {stderrTask.Result}");
        Console.Write(stdout);
    }
}
