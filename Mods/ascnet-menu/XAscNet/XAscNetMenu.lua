-- AscNet Menu screen, built from the client's own Settings screen (UiSet, light panel with the left tab rail, Back/Main
-- buttons, open animation) and its component prefabs, so it looks and behaves like the game's UI in every edition and
-- language. The screen "UiAscNetMenu" is registered at run time with UiSet.prefab as its prefab; its Lua class repurposes the
-- prefab's parts: five tab buttons for the command groups (XUiButtonGroup), the "Other settings" panel's header and
-- scroll view for the command cards, the Save / Return-to-Default button art for actions, UiSetNumber's input field for
-- values, and the game's own dialog (XUiManager.DialogTip) for the confirmation of destructive actions.
-- Running an action sends the existing server chat command (XAscNetMenuCmd); the reply is shown as a game toast and in a
-- small log under the cards.
local Cmd = require("XAscNet/XAscNetMenuCmd")
local Lookup = require("XAscNet/XAscNetMenuLookup")
local spec = require("XAscNet/XAscNetMenuSpec")
local U = CS.UnityEngine
local UI = U.UI
local Menu = {}
local UI_NAME = "UiAscNetMenu"
local PREFAB_SET = "Assets/Product/Ui/Prefab/UiSet.prefab"
local PREFAB_OTHER = "Assets/Product/Ui/ComponentPrefab/Set/PanelOtherPC.prefab"
local PREFAB_NUMBER = "Assets/Product/Ui/Prefab/UiSetNumber.prefab"
local LOG_LINES = 3
-- Hot reload re-runs this chunk: state, the registered class (its methods call state.impl) and the template survive.
local state = rawget(_G, "XAscNetMenuState")
if not state or not state.impl then
    state = {replies = {}, values = {}, res = {}, tpl = {}, impl = {}}
    rawset(_G, "XAscNetMenuState", state)
end
local Ui = state.impl

-- The client language is XLocalizationManager.Language, read through XOverseaManager (xoverseamanager.lua); CN is the
-- only non-overseas value, and anything unknown falls back to English.
local function language()
    local region = rawget(_G, "XOverseaManager")
    if region then
        local ok, lang = pcall(function()
            if region.IsTWRegion() then return "zh-TW" end
            if region.IsKRRegion() then return "ko" end
            if region.IsJPRegion() then return "ja" end
            if region.IsENRegion() then return "en" end
            if region.IsOverSeaRegion() then return "en" end
            return "zh-CN"
        end)
        if ok then return lang end
    end
    return "en"
end

-- Text for a spec key (unknown keys are returned unchanged).
local function tr(key, ...)
    local entry = spec.text[key]
    local text = entry and (entry[state.lang] or entry.en) or key
    if select("#", ...) > 0 then return string.format(text, ...) end
    return text
end

--region Prefab parts
-- An asset child kept as a clone source. The resource stays referenced in state so the asset is not unloaded.
local function asset(url)
    if not state.res[url] then state.res[url] = CS.XResourceManager.Load(url) end
    return state.res[url].Asset.transform
end

local function template(name, url, path)
    if XTool.UObjIsNil(state.tpl[name]) then
        local found = asset(url):Find(path)
        assert(found, "AscNet Menu: game prefab part missing: " .. url .. " / " .. path)
        state.tpl[name] = found.gameObject
    end
    return state.tpl[name]
end

local OTHER_ROWS = "SView /Viewport/PanelContent/"
local function clone(name, parent)
    local go = U.Object.Instantiate(state.tpl[name], parent)
    go:SetActive(true)
    return go
end

local function loadTemplates()
    template("label", PREFAB_OTHER, OTHER_ROWS .. "LoadingSet/Label")            -- 40pt black Text, game font
    template("block", PREFAB_OTHER, OTHER_ROWS .. "LoadingSet/DescriptionType1/Image") -- translucent sliced panel
    template("rule", PREFAB_OTHER, "Biaoti/Image")                                 -- the header's hairline
    -- Settings' own dropdown (the graphics panel's resolution picker): current value on the face, options on tap.
    template("select", XUiConfigs.GetComponentUrl("UiSetPanelGraphicsSetPC"), "Jiemian/Shezhi/Array/TGroupResolution/DrdSort")
    template("input", PREFAB_NUMBER, "SafeAreaContentPane/InputField")
    template("btnBlack", PREFAB_SET, "SafeAreaContentPane/PanelMainSet/BtnRightDown/BtnSave")
end

local function object(name, parent)
    local go = U.GameObject(name)
    local rect = go:AddComponent(typeof(U.RectTransform))
    rect:SetParent(parent, false)
    return go, rect
end

local function layoutElement(go, width, height, flexible)
    local layout = go:GetComponent(typeof(UI.LayoutElement)) or go:AddComponent(typeof(UI.LayoutElement))
    if width then layout.minWidth, layout.preferredWidth = width, width end
    if height then layout.minHeight, layout.preferredHeight = height, height end
    layout.flexibleWidth = flexible or 0
    layout.flexibleHeight = 0
    return layout
end

local function group(go, vertical, spacing, left, top, right, bottom)
    local component = go:AddComponent(vertical and typeof(UI.VerticalLayoutGroup) or typeof(UI.HorizontalLayoutGroup))
    component.spacing = spacing or 0
    component.padding = U.RectOffset(left or 0, right or left or 0, top or 0, bottom or top or 0)
    component.childControlWidth, component.childControlHeight = true, true
    component.childForceExpandWidth, component.childForceExpandHeight = false, false
    return component
end

local function words(parent, value, size, color)
    local go = clone("label", parent)
    local text = go:GetComponent(typeof(UI.Text))
    text.fontSize = size
    text.color = color or text.color
    text.horizontalOverflow = U.HorizontalWrapMode.Wrap
    text.verticalOverflow = U.VerticalWrapMode.Overflow
    text.alignment = U.TextAnchor.MiddleLeft
    text.raycastTarget = false
    text.text = value
    local fitter = go:GetComponent(typeof(UI.ContentSizeFitter))
    if fitter then fitter.enabled = false end
    return text
end

local function button(parent, label, callback)
    local go = clone("btnBlack", parent)
    local component = go:GetComponent(typeof(CS.XUiComponent.XUiButton))
    component:SetNameByGroup(0, label)
    component.CallBack = callback
    layoutElement(go, 364, 101) -- the art's own size: it draws outside a smaller slot
    return component
end
--endregion

--region Replies
local function renderLog(ui)
    if not ui or XTool.UObjIsNil(ui.ReplyLog) then return end
    local lines = {}
    for i = #state.replies, math.max(1, #state.replies - LOG_LINES + 1), -1 do
        local reply = state.replies[i]
        local text = (reply.text:gsub("%s*\n%s*", "  "))
        local color = reply.error and "#8A1212" or (i == #state.replies and "#000000" or "#333333")
        lines[#lines + 1] = "<color=" .. color .. ">" .. text .. "</color>"
    end
    ui.ReplyLog.text = #lines > 0 and table.concat(lines, "\n") or tr("u.noreply")
end

local function log(text, isError)
    table.insert(state.replies, {text = text, error = isError})
    renderLog(state.ui)
end

-- Server replies and errors also show as the game's own toast.
local function showReply(text, isError)
    log(text, isError)
    local manager = rawget(_G, "XUiManager")
    if manager then
        local show = isError and manager.TipError or manager.TipMsg
        if show then pcall(show, text) end
    end
end
--endregion

--region Commands
local function run(command, action, values)
    local line, err = Cmd.BuildLine(command, action, values, tr)
    if not line then
        showReply(err, true)
        return
    end
    log(tr("u.sending", line))
    local ok, why = Cmd.Send(line, showReply, tr)
    if not ok then showReply(why, true) end
end

local function trigger(command, action, values)
    if not action.confirm then return run(command, action, values) end
    local line, err = Cmd.BuildLine(command, action, values, tr)
    if not line then
        showReply(err, true)
        return
    end
    XUiManager.DialogTip(tr("u.confirm"), tr(action.confirm) .. "\n\n" .. tr("u.line") .. ": /" .. line,
        XUiManager.DialogType.Normal, nil, function() run(command, action, values) end)
end

local function valuesFor(command, action)
    local key = command.cmd .. "." .. action.id
    state.values[key] = state.values[key] or {}
    return state.values[key]
end

-- A labelled cell of an action row: the field's label (two lines high, so the controls line up) over its control.
local cellWidth = 250 -- narrower while a row holds more than three fields
local function cell(parent, field, width)
    local go, rect = object(field.id, parent)
    group(go, true, 4).childForceExpandWidth = true
    layoutElement(go, math.min(width, cellWidth), nil, 0)
    local label = words(rect, tr(field.label) .. (field.optional and (" " .. tr("u.optional")) or ""), 22,
        U.Color(0.2, 0.2, 0.2, 1))
    label.alignment = U.TextAnchor.LowerLeft
    layoutElement(label.gameObject, nil, 56)
    return rect
end

-- Id search: matches (up to 8, name / id / hint) list full width under the action's row, so the row's controls stay put.
-- Picking one writes the plain id into the field; the command sent is unchanged.
local function matchPanel(card)
    local go, rect = object("Matches", card)
    group(go, true, 4, 28, 0, 28, 0).childForceExpandWidth = true
    return rect
end

local function clearMatches(panel)
    for i = panel.childCount - 1, 0, -1 do
        local child = panel:GetChild(i).gameObject
        child:SetActive(false) -- leaves the layout now; Destroy only takes effect at frame end
        U.Object.Destroy(child)
    end
end

local function matchRow(panel, text, onPick)
    local go, rect = object("Match", panel)
    go:AddComponent(typeof(UI.Image)).color = U.Color(0.93, 0.93, 0.93, 1)
    group(go, true, 0, 12, 6, 12, 6).childForceExpandWidth = true
    words(rect, text, 24, U.Color(0.1, 0.1, 0.1, 1)).supportRichText = true
    if onPick then go:AddComponent(typeof(UI.Button)).onClick:AddListener(onPick) end
end

local WORDS = {all = true, max = true, suit = true, weapons = true, memories = true}

local function addInput(parent, field, values, matches)
    local rect = cell(parent, field, 250)
    local go = clone("input", rect)
    layoutElement(go, nil, 72)
    local input = go:GetComponent(typeof(UI.InputField))
    local searchable = field.lookup or field.lookupBy
    local numeric = field.kind == "number" and not field.allow and not searchable
    input.contentType = numeric and UI.InputField.ContentType.IntegerNumber or UI.InputField.ContentType.Standard
    input.characterLimit = 40
    local placeholder = input.placeholder:GetComponent(typeof(UI.Text))
    placeholder.fontSize = 24
    placeholder.text = field.hint and tr(field.hint) or tr(searchable and "u.search" or "u.enter")
    input.text = values[field.id] or ""
    local picking = false
    local function search(value)
        clearMatches(matches)
        local query = value:match("^%s*(.-)%s*$")
        local kind = field.lookup or values[field.lookupBy]
        if not searchable or not kind then return end
        local found = Lookup.Search(kind, query, tr)
        for _, entry in ipairs(found) do
            matchRow(matches, string.format("%s  <color=#555555>#%d  %s</color>", entry.name, entry.id, entry.hint), function()
                picking = true
                input.text = string.format("%d", entry.id)
                picking = false
                clearMatches(matches)
            end)
        end
        local lower = query:lower()
        if query ~= "" and #found == 0 and not (query:match("^%d+$") or lower:match("^c%d+$") or WORDS[lower]) then
            matchRow(matches, tr("u.nomatch"))
        end
    end
    input.onValueChanged:AddListener(function(value)
        values[field.id] = value
        if not picking then search(value) end
    end)
end

-- A choice is Settings' dropdown: the current value on its face, the values ("(none)" first when optional) on tap.
local function addChoice(parent, field, values)
    local rect = cell(parent, field, 250)
    local states = field.optional and {""} or {}
    for _, choice in ipairs(field.choices) do table.insert(states, choice) end
    local go = clone("select", rect)
    layoutElement(go, nil, 82)
    local dropdown = go:GetComponent(typeof(UI.Dropdown))
    dropdown.onValueChanged:RemoveAllListeners()
    dropdown:ClearOptions()
    local selected = 0
    for i, value in ipairs(states) do
        local option = UI.Dropdown.OptionData()
        option.text = value == "" and tr("u.any") or value
        dropdown.options:Add(option)
        if value == values[field.id] then selected = i - 1 end
    end
    values[field.id] = states[selected + 1]
    dropdown.value = selected
    dropdown:RefreshShownValue()
    dropdown.onValueChanged:AddListener(function(index) values[field.id] = states[index + 1] end)
    local face = dropdown.captionText.rectTransform -- keep the value off the frame's edge
    face.offsetMin = U.Vector2(16, face.offsetMin.y)
end

-- One row per action: its name, its fields, and the button that runs it ("Run..." when a confirm dialog follows).
local function addAction(card, command, action)
    local rule = clone("rule", card)
    layoutElement(rule, nil, 2)
    local fields = action.fields or {}
    local title = tr("a." .. command.cmd .. "." .. action.id)
    -- Up to two fields fit beside the name; more fields get the name on a line of its own above them.
    local stacked = #fields > 2
    local outer, outerRect = object(action.id, card)
    local matches = matchPanel(card)
    local rowObject, rect = outer, outerRect
    if stacked then
        group(outer, true, 0, 28, 14)
        words(outerRect, title, 32)
        rowObject, rect = object("Controls", outerRect)
        group(rowObject, false, 24, 0, 6)
    else
        group(outer, false, 24, 28, 14)
    end
    rowObject:GetComponent(typeof(UI.HorizontalLayoutGroup)).childAlignment = U.TextAnchor.MiddleLeft
    if stacked then
        layoutElement(object("Spacer", rect), 0, 0, 1)
    else
        local name = words(rect, title, 32)
        layoutElement(name.gameObject, 220, nil, 1)
    end
    cellWidth = #fields > 3 and 205 or 250
    local values = valuesFor(command, action)
    for _, field in ipairs(fields) do
        if field.kind == "choice" then addChoice(rect, field, values) else addInput(rect, field, values, matches) end
    end
    button(rect, tr(action.confirm and "u.runconfirm" or "u.run"), function() trigger(command, action, values) end)
end

local function addCommand(content, command)
    local go, rect = object(command.cmd, content)
    local back = clone("block", rect)
    back:AddComponent(typeof(UI.LayoutElement)).ignoreLayout = true
    back.transform:SetAsFirstSibling()
    local stretch = back:GetComponent(typeof(U.RectTransform))
    stretch.anchorMin, stretch.anchorMax = U.Vector2(0, 0), U.Vector2(1, 1)
    stretch.offsetMin, stretch.offsetMax = U.Vector2.zero, U.Vector2.zero
    group(go, true, 0).childForceExpandWidth = true
    local head, headRect = object("Head", rect)
    local black = head:AddComponent(typeof(UI.Image))
    black.color = U.Color(0, 0, 0, 1)
    group(head, true, 4, 28, 14).childForceExpandWidth = true
    words(headRect, tr("c." .. command.cmd), 38, U.Color(1, 1, 1, 1))
    words(headRect, tr("c." .. command.cmd .. ".help"), 26, U.Color(0.85, 0.85, 0.85, 1))
    for _, action in ipairs(command.actions) do addAction(rect, command, action) end
    local pad = object("Pad", rect)
    layoutElement(pad, nil, 10)
end
--endregion

--region Screen
-- In the rail's sibling order, so tab i is spec group i.
local TAB_BUTTONS = {"BtnVoice", "BtnGraphics", "BtnFight", "BtnOther", "BtnDownload", "BtnPush"}
local UNUSED = {"BtnFpsGame", "BtnDlcHunt", "BtnSpecialTrain", "BtnInstruction", "BtnGeneralSkill", "BtnAccount",
    "BtnInfoTip"}

function Ui.OnAwake(self)
    XTool.InitUiObject(self)
    state.ui = self
    for _, name in ipairs(UNUSED) do self[name].gameObject:SetActiveEx(false) end
    self.BtnDefault.transform.parent.gameObject:SetActiveEx(false)
    self.PanelAsset.gameObject:SetActiveEx(false)
    self.Transform:Find("SafeAreaContentPane/SubPanels/TxtStageName").gameObject:SetActiveEx(false)
    self.BtnBack.CallBack = function() self:Close() end
    self.BtnMainUi.CallBack = function() XLuaUiManager.RunMain() end
    self.PanelTabToggles:Init((function()
        local tabs = {}
        for i, name in ipairs(TAB_BUTTONS) do
            tabs[i] = self[name]
            self[name].gameObject:SetActiveEx(true)
            self[name]:SetNameByGroup(0, tr("g." .. spec.groups[i].id))
            -- Group names are longer than the Settings tabs': shrink to fit the 270px label instead of wrapping.
            local texts = self[name].gameObject:GetComponentsInChildren(typeof(UI.Text), true)
            for t = 0, texts.Length - 1 do
                texts[t].resizeTextForBestFit, texts[t].resizeTextMinSize, texts[t].resizeTextMaxSize = true, 18, texts[t].fontSize
            end
        end
        return tabs
    end)(), function(index) self:ShowGroup(index) end)
end

function Ui.OnStart(self)
    loadTemplates()
    Lookup.Reset()
    local panel = self.PanelOther:LoadPrefab(PREFAB_OTHER)
    local transform = panel.transform
    self.Title = transform:Find("Biaoti/TextTitle"):GetComponent(typeof(UI.Text))
    self.Title.horizontalOverflow = U.HorizontalWrapMode.Overflow
    self.Scroll = transform:Find("SView "):GetComponent(typeof(UI.ScrollRect))
    self.Content = transform:Find("SView /Viewport/PanelContent")
    for i = self.Content.childCount - 1, 0, -1 do U.Object.Destroy(self.Content:GetChild(i).gameObject) end
    local layout = self.Content:GetComponent(typeof(UI.VerticalLayoutGroup))
    layout.spacing = 36
    layout.padding = U.RectOffset(0, 0, 10, 40)
    layout.childControlWidth, layout.childControlHeight = true, true
    layout.childForceExpandWidth, layout.childForceExpandHeight = true, false
    -- The prefab's fitter sizes to the minimum; nested groups only reach their preferred height when the root does.
    self.Content:GetComponent(typeof(UI.ContentSizeFitter)).verticalFit = UI.ContentSizeFitter.FitMode.PreferredSize
    self.PanelOther.gameObject:SetActiveEx(true)

    -- Reply log: bottom strip of the main panel, where the Settings screen keeps its Save / Default buttons.
    local go = clone("label", self.PanelTabToggles.transform.parent)
    local log = go:GetComponent(typeof(UI.Text))
    log.fontSize = 24
    log.horizontalOverflow, log.verticalOverflow = U.HorizontalWrapMode.Wrap, U.VerticalWrapMode.Truncate
    log.alignment = U.TextAnchor.UpperLeft
    log.supportRichText = true
    log.raycastTarget = false
    local rect = go:GetComponent(typeof(U.RectTransform))
    rect.anchorMin, rect.anchorMax = U.Vector2(0, 0), U.Vector2(0, 0)
    rect.pivot = U.Vector2(0, 0)
    rect.sizeDelta = U.Vector2(1300, 100)
    rect.anchoredPosition = U.Vector2(445, 10)
    local fitter = go:GetComponent(typeof(UI.ContentSizeFitter))
    if fitter then fitter.enabled = false end
    self.ReplyLog = log
    renderLog(self)
    self.PanelTabToggles:SelectIndex(1)
end

function Ui.OnDestroy(self)
    if state.ui == self then state.ui = nil end
    Lookup.Reset()
end

function Ui.ShowGroup(self, index)
    local current = spec.groups[index]
    self.Title.text = tr("g." .. current.id)
    for i = self.Content.childCount - 1, 0, -1 do
        local child = self.Content:GetChild(i).gameObject
        child:SetActive(false)
        U.Object.Destroy(child)
    end
    for _, command in ipairs(current.commands) do addCommand(self.Content, command) end
    UI.LayoutRebuilder.ForceRebuildLayoutImmediate(self.Content)
    self.Content.anchoredPosition = U.Vector2.zero
    self:PlayAnimation("AnimQieHuanEnable")
end

-- One class, registered once; its methods look the implementation up so a hot reload takes effect.
local function register()
    if not XLuaUiManager.GetUiClass(UI_NAME) then
        local class = XLuaUiManager.Register(XLuaUi, UI_NAME)
        for name in pairs({OnAwake = 1, OnStart = 1, OnDestroy = 1, ShowGroup = 1}) do
            class[name] = function(self, ...) return state.impl[name](self, ...) end
        end
    end
    -- The game registers its screens from Ui.tab through this call (XUiManager.RegisterByTab); this one reuses UiSet's prefab.
    if not state.registered then
        CS.XUiManager.Instance:Register(UI_NAME, CS.XUiType.Normal, CS.XUiResType.Bundle, true, PREFAB_SET, "", "", 0,
            false, false, 0, "")
        state.registered = true
    end
end
--endregion

-- The screen is a normal lobby UI like Settings: not on the login screen and not during a fight (the hotkey works anywhere).
local function available()
    local player = rawget(_G, "XPlayer")
    if not (player and player.Id) then return false end
    local ok, fighting = pcall(function() return CS.XFight.IsRunning end)
    return not (ok and fighting)
end

function Menu.Open()
    if not available() then return end
    state.lang = language()
    register()
    XLuaUiManager.Open(UI_NAME)
end

function Menu.Toggle()
    if XLuaUiManager.IsUiShow(UI_NAME) then XLuaUiManager.Close(UI_NAME) else Menu.Open() end
end

return Menu
