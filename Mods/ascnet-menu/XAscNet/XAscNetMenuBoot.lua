-- AscNet Menu boot: required by core's XAscNetModList at game start (every installed XAscNet/*Boot module is). Registers
-- the main screen's second-menu entry and the Ctrl+Shift+M hotkey; the menu itself loads on first use. Never throws.
local XAscNetSubMenu = require("XAscNet/XAscNetSubMenu")
local U = CS.UnityEngine
local Boot = {}
-- Hot reload re-runs this chunk: one hotkey object, whose update function is replaced.
local state = rawget(_G, "XAscNetMenuBootState")
if not state then
    state = {}
    rawset(_G, "XAscNetMenuBootState", state)
end

local function menu() return require("XAscNet/XAscNetMenu") end

local function poll()
    local input, key = U.Input, U.KeyCode
    if input.GetKeyDown(key.M) and (input.GetKey(key.LeftControl) or input.GetKey(key.RightControl))
        and (input.GetKey(key.LeftShift) or input.GetKey(key.RightShift)) then menu().Toggle() end
end

local function guarded()
    local ok, err = xpcall(poll, debug.traceback)
    if not ok then XLog.Error("AscNet Menu hotkey: " .. tostring(err)) end
end

local function install()
    local ok, err = pcall(XAscNetSubMenu.Add, {
        key = "ascnet-menu", skipId = 990003, title = "AscNet Menu", subtitle = "AscNet",
        icon = "Assets/Product/Texture/Atlas/UiMain/UiMainIconLogoCartoon.png", open = function() menu().Open() end,
    })
    if not ok then XLog.Error("AscNet Menu: no second-menu button: " .. tostring(err)) end
    -- Same bridge the game's XSceneAnimPlayer uses; independent of XScheduleManager, which boot clears.
    if not (state.behaviour and pcall(function() return state.behaviour.enabled end)) then
        local go = U.GameObject("AscNetMenuHotkey")
        U.Object.DontDestroyOnLoad(go)
        state.behaviour = go:AddComponent(typeof(CS.XLuaBehaviour))
    end
    state.behaviour.LuaLateUpdate = guarded
end

local ok, err = pcall(install)
if not ok then XLog.Error("AscNet Menu boot failed: " .. tostring(err)) end
return Boot
