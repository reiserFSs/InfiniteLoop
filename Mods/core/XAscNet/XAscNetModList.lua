-- Main-screen second-menu "Mod List": lists the installed client mods from the generated catalog and hot-reloads a
-- mod's Lua modules. Loaded at boot by core's XLoginManager hook: top level only registers the entry.
local XAscNetSubMenu = require("XAscNet/XAscNetSubMenu")
local XAscNetPanel = require("XAscNet/XAscNetPanel")
local ModList = {}
-- Hot reload re-runs this chunk: keep one panel (and window) alive across it.
local state = rawget(_G, "XAscNetModListState")
if not state then
    state = {}
    rawset(_G, "XAscNetModListState", state)
end

local function plural(count, noun)
    return count .. " " .. noun .. (count == 1 and "" or "s")
end

function ModList.Reload(panel, mod)
    local reloader = rawget(_G, "XHotReload")
    if not (reloader and reloader.Reload) then
        panel.SetStatus("XHotReload is not loaded (needs remote-config Debug)", true)
        return
    end
    local reloaded, skipped, errors = 0, 0, {}
    for _, module in ipairs(mod.modules) do
        if package.loaded[module] == nil then
            skipped = skipped + 1
        else
            local ok, err = pcall(reloader.Reload, module)
            if ok then reloaded = reloaded + 1 else table.insert(errors, module .. ": " .. tostring(err)) end
        end
    end
    local message = "Reloaded " .. plural(reloaded, "module") .. " of " .. mod.name
    if skipped > 0 then message = message .. " (" .. skipped .. " not loaded yet)" end
    if #errors > 0 then message = message .. "\n" .. table.concat(errors, "\n") end
    panel.SetStatus(message, #errors > 0)
    XUiManager.TipMsg(message)
end

function ModList.Build(panel)
    local ok, catalog = pcall(require, "XAscNet/XAscNetModCatalog")
    if not (ok and type(catalog) == "table") then
        panel:AddText("The mod catalog (XAscNet/XAscNetModCatalog) is missing: reinstall the client mods.\n"
            .. (ok and "" or tostring(catalog)), nil, 160)
        return
    end
    if #catalog == 0 then
        panel:AddText("No client mods are installed.")
        return
    end
    for _, mod in ipairs(catalog) do
        local modules = mod.modules or {}
        local head = mod.name .. (mod.core and "  [core]" or "") .. "  -  " .. plural(#modules, "module")
            .. ", " .. plural(#(mod.tables or {}), "table")
        panel:AddText(head .. "\n" .. tostring(mod.description or "") .. "\n" .. table.concat(modules, ", "), nil, 150, 21)
        -- Reloading a module under XManager/ makes XHotReload re-run that manager's Init(); core's boot hook lives in
        -- the game's XLoginManager, which must not be re-initialised mid-session.
        if #modules > 0 and not mod.core then
            panel:AddButton("Reload " .. mod.name, function() ModList.Reload(panel, {name = mod.name, modules = modules}) end)
        end
    end
end

function ModList.Open()
    if not state.panel then
        state.panel = XAscNetPanel.New{
            name = "AscNetModList", title = "Mod List", tag = "[Mod list]", rebuild = true,
            init = function(panel) return ModList.Build(panel) end,
        }
    end
    state.panel.Open()
end

local ok, err = pcall(XAscNetSubMenu.Add, {
    key = "mod-list", skipId = 990002, title = "Mod List", subtitle = "AscNet",
    icon = "Assets/Product/Texture/Atlas/UiMain/UiMainIconLogoCartoon.png",
    open = function() ModList.Open() end,
})
if not ok then XLog.Error("AscNet Mod List entry failed: " .. tostring(err)) end
return ModList
