-- Pure-Lua replacement for the stripped XDebugManager GM panel; the UGUI builder lives in core's XAscNetPanel.
local XAscNetPanel = require("XAscNet/XAscNetPanel")
local XAscNetSubMenu = require("XAscNet/XAscNetSubMenu")
local labels = require("XAscNet/XAscNetGmLabels")
local U = CS.UnityEngine
local watching = false
local init
local function translate(value)
    return labels[value] or value
end
local Panel
Panel = XAscNetPanel.New{
    name = "AscNetGmMenu", title = "GM", tag = "[GM menu]", translate = translate,
    translateRefreshed = function(value)
        for _, prefix in ipairs({"当前服务器", "已启动时间", "用户名", "客户端版本", "原版本", "现版本", "未登录"}) do
            value = value:gsub(prefix, function() return translate(prefix) end)
        end
        return value
    end,
    init = function()
        Panel.Safe(init)
        Panel.Safe(Panel.AddAscNetMenu)
    end,
}
-- AscNet tools next to the game's own menus. Hot reload uses the game's XHotReload (XHotReload.lua:110), which
-- needs remote-config Debug and re-reads the module through the external loader; UI modules apply on next open.
function Panel.AddAscNetMenu()
    local module = "XUi/XUiLogin/XUiLogin"
    Panel:AddSubMenu("AscNet", function()
        Panel:AddInput("Lua module (e.g. XUi/XUiLogin/XUiLogin)", function(value) module = value end)
        Panel:AddButton("Hot reload module", function()
            local reloader = rawget(_G, "XHotReload")
            if not (reloader and reloader.Reload) then error("XHotReload is not loaded (needs remote-config Debug)") end
            if not package.loaded[module] then error("not loaded yet, nothing to reload: " .. tostring(module)) end
            reloader.Reload(module)
            XUiManager.TipMsg("Reloaded " .. module)
        end)
    end)
end
function Panel.StartWatcher(callback)
    if watching then return end
    init = callback
    local ok, err = pcall(XAscNetSubMenu.Add, {
        key = "gm-menu", skipId = 990001, title = "GM Menu", subtitle = "AscNet",
        icon = "Assets/Product/Texture/Atlas/UiMain/UiMainIconLogoStrategy.png", open = Panel.Open,
    })
    if not ok then XLog.Error("AscNet GM menu: no second-menu button: " .. tostring(err)) end
    local scheduler = rawget(_G, "XScheduleManager")
    -- Runs while the game is still requiring its managers: never throw here, or the boot require fails.
    if not (scheduler and scheduler.ScheduleForever) then
        XLog.Error("AscNet GM menu: XScheduleManager is not loaded; Ctrl+Shift+D is unavailable")
        return
    end
    watching = true
    local function poll()
        local input, key = U.Input, U.KeyCode
        if input.GetKeyDown(key.D) and (input.GetKey(key.LeftControl) or input.GetKey(key.RightControl))
            and (input.GetKey(key.LeftShift) or input.GetKey(key.RightShift)) then Panel.Toggle() end
    end
    local function arm()
        scheduler.ScheduleForever(function() Panel.Safe(poll) end, 0)
    end
    -- Boot and every return to login clear timers, including this early registration.
    local private = require("XCommon/XScheduleManager")
    local clear = private.__Clear__
    private.__Clear__ = function(...)
        clear(...)
        Panel.Safe(arm)
    end
    arm()
end
return Panel
