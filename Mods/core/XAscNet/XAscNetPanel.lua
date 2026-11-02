-- Runtime-built UGUI overlay panel shared by the GM menu and the Mod List. XAscNetPanel.New(opts) returns an
-- independent panel; opts: name (GameObject), title (root page), tag (log prefix), init(panel) (fills the root page on
-- first open, or on every open when rebuild is set), translate(label) and translateRefreshed(text) (optional).
local XAscNetPanel = {}
local U = CS.UnityEngine
local UI = U.UI
local white = U.Color(1, 1, 1, 1)
local font

local function object(name, parent)
    local go = U.GameObject(name)
    local rect = go:AddComponent(typeof(U.RectTransform))
    if parent then rect:SetParent(parent, false) end
    return go, rect
end
local function stretch(rect, left, bottom, right, top)
    rect.anchorMin = U.Vector2(0, 0)
    rect.anchorMax = U.Vector2(1, 1)
    rect.offsetMin = U.Vector2(left or 0, bottom or 0)
    rect.offsetMax = U.Vector2(-(right or 0), -(top or 0))
end
local function image(go, color)
    local component = go:AddComponent(typeof(UI.Image))
    component.color = color
    return component
end
local function row(go, height)
    local layout = go:AddComponent(typeof(UI.LayoutElement))
    layout.preferredHeight = height or 58
    layout.minHeight = height or 58
end

function XAscNetPanel.New(opts)
    local Panel = {}
    local root, body, title, status, current, top, building, scroll, initialized
    local stack = {}
    local function translate(value)
        value = tostring(value or "")
        return opts.translate and opts.translate(value) or value
    end
    function Panel.SetStatus(message, isError)
        if not status then return end
        status.text = tostring(message)
        status.color = isError and U.Color(1, 0.25, 0.25, 1) or U.Color(0.4, 0.95, 0.65, 1)
    end
    local function report(message)
        Panel.SetStatus(message, true)
        local logger = rawget(_G, "XLog")
        if logger and logger.Error then pcall(logger.Error, opts.tag .. " " .. tostring(message)) end
    end
    local function safe(callback, ...)
        if not callback then return true end
        local args = table.pack(...)
        local ok, result = xpcall(function() return callback(table.unpack(args, 1, args.n)) end, debug.traceback)
        if not ok then report(result) end
        return ok, result
    end
    Panel.Safe = safe
    local function listen(event, callback)
        event:AddListener(function(...) safe(callback, ...) end)
    end
    local function text(parent, name, value, size)
        local go, rect = object(name, parent)
        stretch(rect, 12, 4, 12, 4)
        local component = go:AddComponent(typeof(UI.Text))
        component.font = font
        component.fontSize = size or 23
        component.color = white
        component.alignment = U.TextAnchor.MiddleLeft
        component.horizontalOverflow = U.HorizontalWrapMode.Wrap
        component.verticalOverflow = U.VerticalWrapMode.Truncate
        component.raycastTarget = false
        component.text = translate(value)
        return component, rect
    end
    local function button(parent, name, callback)
        local go, rect = object(name, parent)
        local background = image(go, U.Color(0.16, 0.20, 0.27, 1))
        local component = go:AddComponent(typeof(UI.Button))
        component.targetGraphic = background
        text(rect, "TxtName", name)
        listen(component.onClick, callback)
        return component, rect
    end
    local function page(name, parent)
        local go, rect = object(name, body)
        stretch(rect)
        local layout = go:AddComponent(typeof(UI.VerticalLayoutGroup))
        layout.spacing = 7
        layout.childControlWidth = true
        layout.childControlHeight = true
        layout.childForceExpandWidth = true
        layout.childForceExpandHeight = false
        local fitter = go:AddComponent(typeof(UI.ContentSizeFitter))
        fitter.verticalFit = UI.ContentSizeFitter.FitMode.PreferredSize
        rect.anchorMin = U.Vector2(0, 1)
        rect.anchorMax = U.Vector2(1, 1)
        rect.pivot = U.Vector2(0.5, 1)
        rect.anchoredPosition = U.Vector2(0, 0)
        local result = {go = go, rect = rect, name = name, parent = parent, refreshers = {}}
        go:SetActive(false)
        return result
    end
    local function clear(level)
        for i = level.rect.childCount - 1, 0, -1 do
            local child = level.rect:GetChild(i)
            child.gameObject:SetActive(false)
            U.Object.Destroy(child.gameObject)
        end
        level.refreshers = {}
    end
    local function refresh(level)
        for _, entry in ipairs(level.refreshers) do
            safe(entry.callback, entry.text)
            if opts.translateRefreshed then entry.text.text = opts.translateRefreshed(entry.text.text) end
        end
    end
    local function show(level)
        if current then current.go:SetActive(false) end
        current = level
        level.go:SetActive(true)
        scroll.content = level.rect
        scroll.verticalNormalizedPosition = 1
        local names, cursor = {}, level
        while cursor do table.insert(names, 1, cursor.name); cursor = cursor.parent end
        title.text = table.concat(names, " / ")
        refresh(level)
    end
    local function create()
        local texts = U.Resources.FindObjectsOfTypeAll(typeof(UI.Text))
        for i = 0, texts.Length - 1 do
            if texts[i].font then font = texts[i].font; break end
        end
        assert(font, "No game UI font available yet; open the menu after the login screen appears")
        local go, rect = object(opts.name)
        root = go
        U.Object.DontDestroyOnLoad(root)
        local canvas = go:AddComponent(typeof(U.Canvas))
        canvas.renderMode = U.RenderMode.ScreenSpaceOverlay
        canvas.sortingOrder = 32760
        local scaler = go:AddComponent(typeof(UI.CanvasScaler))
        scaler.uiScaleMode = UI.CanvasScaler.ScaleMode.ScaleWithScreenSize
        scaler.referenceResolution = U.Vector2(1920, 1080)
        scaler.matchWidthOrHeight = 0.5
        go:AddComponent(typeof(UI.GraphicRaycaster))
        local window, windowRect = object("Window", rect)
        windowRect.anchorMin = U.Vector2(0.5, 0.5)
        windowRect.anchorMax = U.Vector2(0.5, 0.5)
        windowRect.sizeDelta = U.Vector2(1180, 920)
        image(window, U.Color(0.035, 0.045, 0.065, 0.98))
        local titleRect
        title, titleRect = text(windowRect, "Path", opts.title, 25)
        titleRect.anchorMin = U.Vector2(0, 1); titleRect.anchorMax = U.Vector2(1, 1)
        titleRect.offsetMin = U.Vector2(20, -64); titleRect.offsetMax = U.Vector2(-240, -8)
        local _, backRect = button(windowRect, "Back", function()
            if #stack > 0 then show(table.remove(stack)) end
        end)
        backRect.anchorMin = U.Vector2(1, 1); backRect.anchorMax = U.Vector2(1, 1)
        backRect.offsetMin = U.Vector2(-230, -58); backRect.offsetMax = U.Vector2(-125, -8)
        local _, closeRect = button(windowRect, "Close", function() Panel.Close() end)
        closeRect.anchorMin = U.Vector2(1, 1); closeRect.anchorMax = U.Vector2(1, 1)
        closeRect.offsetMin = U.Vector2(-115, -58); closeRect.offsetMax = U.Vector2(-10, -8)
        local viewport, viewportRect = object("Viewport", windowRect)
        stretch(viewportRect, 16, 100, 16, 76)
        image(viewport, U.Color(0.06, 0.07, 0.09, 1))
        viewport:AddComponent(typeof(UI.RectMask2D))
        body = viewportRect
        scroll = viewport:AddComponent(typeof(UI.ScrollRect))
        scroll.viewport = viewportRect
        scroll.horizontal = false
        scroll.movementType = UI.ScrollRect.MovementType.Clamped
        local statusRect
        status, statusRect = text(windowRect, "Error", "", 18)
        statusRect.anchorMin = U.Vector2(0, 0); statusRect.anchorMax = U.Vector2(1, 0)
        statusRect.offsetMin = U.Vector2(16, 8); statusRect.offsetMax = U.Vector2(-16, 92)
        -- Same bridge the game's XSceneAnimPlayer uses; it only ticks while the panel is active.
        local behaviour = go:AddComponent(typeof(CS.XLuaBehaviour))
        behaviour.LuaLateUpdate = function()
            if U.Input.GetKeyDown(U.KeyCode.Escape) then Panel.Close() end
        end
        top = page(opts.title)
        current = top
        building = current
    end
    -- Menus are only defined while the panel builds a page. A definition outside a build comes from the game's
    -- XHotReload re-running a reloaded manager's Init(): build it into an inactive scratch page (callers such as
    -- the shipped GM Init use the returned controls), then rebuild from opts.init on the next open.
    local discard
    local function host()
        if building then return building end
        initialized = false
        if not discard then
            local go, rect = object("AscNetDiscardedMenu")
            go:SetActive(false)
            discard = {go = go, rect = rect, refreshers = {}}
        end
        return discard
    end
    -- The shipped optional column span is irrelevant to this single-column layout.
    function Panel:AddButton(name, callback, span)
        local parent = host()
        local component = button(parent.rect, translate(name), callback)
        row(component.gameObject)
        return component
    end
    function Panel:AddToggle(name, callback)
        local parent = host()
        local go, rect = object(translate(name), parent.rect)
        row(go)
        local background = image(go, U.Color(0.13, 0.17, 0.23, 1))
        local component = go:AddComponent(typeof(UI.Toggle))
        component.targetGraphic = background
        local check, checkRect = object("Check", rect)
        checkRect.anchorMin = U.Vector2(0, 0.5); checkRect.anchorMax = U.Vector2(0, 0.5)
        checkRect.sizeDelta = U.Vector2(24, 24); checkRect.anchoredPosition = U.Vector2(25, 0)
        component.graphic = image(check, U.Color(0.2, 0.95, 0.65, 1))
        local _, labelRect = text(rect, "TxtName", name)
        labelRect.offsetMin = U.Vector2(50, 4)
        listen(component.onValueChanged, callback)
        return component
    end
    function Panel:AddInput(name, callback, numeric)
        local parent = host()
        local go, rect = object(translate(name), parent.rect)
        row(go, 78)
        image(go, U.Color(0.1, 0.13, 0.18, 1))
        local label, labelRect = text(rect, "Label", name, 19)
        labelRect.anchorMin = U.Vector2(0, 0.5)
        local value, valueRect = text(rect, "Value", "", 23)
        valueRect.anchorMax = U.Vector2(1, 0.5)
        local placeholder = text(rect, "Placeholder", "Enter value", 20)
        stretch(placeholder.rectTransform, 12, 4, 12, 4)
        placeholder.rectTransform.anchorMax = U.Vector2(1, 0.5)
        placeholder.color = U.Color(0.55, 0.6, 0.65, 1)
        local component = go:AddComponent(typeof(UI.InputField))
        component.textComponent = value
        component.placeholder = placeholder
        component.targetGraphic = go:GetComponent(typeof(UI.Image))
        if numeric then component.contentType = UI.InputField.ContentType.IntegerNumber end
        -- GM inputs are ids, IPs and module names: surrounding whitespace is never meaningful and breaks lookups.
        listen(component.onEndEdit, function(text, ...)
            if type(text) == "string" then text = text:match("^%s*(.-)%s*$") end
            return callback(text, ...)
        end)
        return component
    end
    function Panel:AddText(name, refresher, height, size)
        local parent = host()
        local component = text(parent.rect, "Text", name, size)
        row(component.gameObject, height or 80)
        if refresher then table.insert(parent.refreshers, {text = component, callback = refresher}) end
        return component
    end
    function Panel:AddSubMenu(name, builder, rebuildOnOpen)
        local parent = host()
        local level
        return self:AddButton(name, function()
            if not level then level = page(translate(name), parent) end
            if not level.built or rebuildOnOpen then
                clear(level)
                building = level
                local ok = safe(builder)
                building = nil
                level.built = ok
            end
            table.insert(stack, parent)
            show(level)
        end)
    end
    function Panel.IsOpen()
        return root ~= nil and root.activeSelf
    end
    function Panel.Close()
        if root then root:SetActive(false) end
    end
    function Panel.Open()
        if Panel.IsOpen() then return end
        if not root then
            if not safe(create) then
                if root then U.Object.Destroy(root) end
                root, current, top, building, status = nil, nil, nil, nil, nil
                return
            end
        end
        root:SetActive(true)
        Panel.SetStatus("")
        if not initialized or opts.rebuild then
            initialized = true
            if discard then U.Object.Destroy(discard.go); discard = nil end
            stack = {}
            if current ~= top then current.go:SetActive(false); current = top end
            clear(top)
            building = top
            safe(opts.init, Panel)
            building = nil
        end
        show(current)
    end
    function Panel.Toggle()
        if Panel.IsOpen() then Panel.Close() else Panel.Open() end
    end
    return Panel
end

return XAscNetPanel
