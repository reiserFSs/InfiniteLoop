-- Main-screen second menu ("GM Menu" / "Mod List" next to Official Site / Swap Team). The grid's system entries come
-- from XUiConfigs.GetSystemSubMenuList() (Client/Ui/UiMainSubMenu.tab), filtered by XFunctionManager.CheckInTimeByTimeId:
-- a row without a server-sent, currently open TimeId is hidden (XFunctionManager.lua:742), and AscNet's time ids follow
-- its activity schedule. So entries are appended to the filtered list here instead of added as table rows.
-- Clicking one calls XFunctionManager.SkipInterface(skipId); skip ids must not exist in the shipped SkipFunctional.tab
-- (4.8: 3434 rows, highest 11080106).
-- This module is the only code that wraps those two functions. The registry lives in a global so hot reload neither
-- double-wraps nor loses entries.
local XAscNetSubMenu = {}
local state = rawget(_G, "XAscNetSubMenuState")
if not state then
    state = {entries = {}, order = {}, bySkipId = {}, wrapped = false}
    rawset(_G, "XAscNetSubMenuState", state)
end

local function install()
    if state.wrapped then return true end
    local functions = rawget(_G, "XFunctionManager")
    local configs = rawget(_G, "XUiConfigs")
    if not (functions and type(functions.SkipInterface) == "function"
        and configs and type(configs.GetSystemSubMenuList) == "function") then
        XLog.Error("AscNet second menu: XFunctionManager/XUiConfigs not loaded; no second-menu entries")
        return false
    end
    local skip = functions.SkipInterface
    functions.SkipInterface = function(id, ...)
        local entry = state.bySkipId[id]
        if not entry then return skip(id, ...) end
        local ok, err = xpcall(entry.open, debug.traceback)
        if not ok then XLog.Error("AscNet second menu '" .. entry.key .. "': " .. tostring(err)) end
        return true
    end
    local list = configs.GetSystemSubMenuList
    configs.GetSystemSubMenuList = function(...)
        local entries = list(...)
        for _, key in ipairs(state.order) do table.insert(entries, state.entries[key].row) end
        return entries
    end
    state.wrapped = true
    return true
end

function XAscNetSubMenu.Add(def)
    assert(type(def.key) == "string" and type(def.skipId) == "number" and type(def.open) == "function",
        "XAscNetSubMenu.Add needs key, skipId and open")
    local owner = state.bySkipId[def.skipId]
    assert(not owner or owner.key == def.key, "skip id " .. def.skipId .. " already used by " .. tostring(owner and owner.key))
    local old = state.entries[def.key]
    if old then state.bySkipId[old.skipId] = nil else table.insert(state.order, def.key) end
    -- Every XTableUiMainSubMenu column: consumers rely on the loader's row shape, where list columns are empty tables
    -- (XRedPointConditionSubMenuNewSystem iterates RedPointCondition with ipairs; nil there broke the whole main screen).
    local entry = {
        key = def.key, skipId = def.skipId, open = def.open,
        row = {
            Id = def.skipId, TimeId = 0, Title = def.title, SubTitle = def.subtitle, Desc = def.subtitle, StyleType = 2,
            BtnIcon = def.icon, SkipId = def.skipId, ConditionId = 0, RedPointCondition = {}, RedPointParam = {},
        },
    }
    state.entries[def.key] = entry
    state.bySkipId[def.skipId] = entry
    return install()
end

return XAscNetSubMenu
