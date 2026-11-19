-- Builds a chat command line from a spec action and sends it the way the chat box does, then catches the server's reply.
-- Send: XDataCenter.ChatManager.SendChat (xchatmanager.lua:1045, the call XUiChatServeMain:OnBtnSendClick ends in) with a
-- World/Normal message whose Content is "/<command> <args>"; AscNet.GameServer ChatModule.SendChatRequestHandler runs it
-- as a command (same validation, permissions and replies as typing it) and pushes the result as a NotifyChatMessage from
-- sender 88001 ("System - Lucia") on the same channel. XRpc has no readable handlers (xrpc.lua), so the reply is caught by
-- wrapping XDataCenter.ChatManager.NotifyChatMessage, which XRpc.NotifyChatMessage calls; the message still reaches the chat.
local Cmd = {}
local FEEDBACK_SENDER = 88001 -- ChatModule.BuildCommandFeedback
local TIMEOUT_SECONDS = 8
-- Hot reload re-runs this chunk: the pending request and the wrapper survive in a global.
local state = rawget(_G, "XAscNetMenuCmdState")
if not state then
    state = {}
    rawset(_G, "XAscNetMenuCmdState", state)
end

local function trim(value)
    return (tostring(value or ""):match("^%s*(.-)%s*$"))
end

-- Validates one field value; returns the text to send ("" when an optional field is empty) or nil, error.
local function fieldValue(field, raw, tr)
    local value = trim(raw)
    local label = tr(field.label)
    if value == "" then
        if field.optional then return "" end
        return nil, tr("u.needvalue", label)
    end
    if field.kind == "number" then
        if not value:match("^%d+$") then
            for _, word in ipairs(field.allow or {}) do
                if value:lower() == word then return word end
            end
            return nil, field.allow and tr("u.allowed", label, table.concat(field.allow, "/")) or tr("u.number", label)
        end
    elseif field.kind == "choice" then
        for _, choice in ipairs(field.choices) do
            if value == choice then return value end
        end
        return nil, tr("u.needvalue", label)
    elseif not field.multiword and value:find("%s") then
        return nil, tr("u.nospace", label)
    end
    return (value:gsub("%s+", " "))
end

-- command: spec command; values: field id -> raw text; tr(key, ...) translates. Returns the line without "/", or nil, error.
function Cmd.BuildLine(command, action, values, tr)
    local fields = {}
    for _, field in ipairs(action.fields or {}) do fields[field.id] = field end
    local parts, gap = {}, nil
    for _, token in ipairs(action.line) do
        local id = token:match("^{(%w+)}$")
        local value, err = token, nil
        if id then value, err = fieldValue(fields[id], values[id], tr) end
        if not value then return nil, err end
        if value == "" then
            gap = gap or fields[id]
        elseif gap then
            -- Positional arguments: a later value cannot be sent while an earlier optional one is empty.
            return nil, tr("u.gap", tr(gap.label))
        else
            table.insert(parts, value)
        end
    end
    if #parts == 0 then return command.cmd end
    return command.cmd .. " " .. table.concat(parts, " ")
end

local function install(chat)
    if chat.NotifyChatMessage == state.wrapper then return end
    local inner = chat.NotifyChatMessage
    state.wrapper = function(message, ...)
        local pending = state.pending
        if pending and type(message) == "table" and message.SenderId == FEEDBACK_SENDER then
            state.pending = nil
            pending.callback(tostring(message.Content or ""), false)
        end
        return inner(message, ...)
    end
    chat.NotifyChatMessage = state.wrapper
end

-- callback(text, isError) runs once: with the server's reply, or with a timeout error. Returns false, error when the
-- chat service is not available (not logged in).
function Cmd.Send(line, callback, tr)
    local data = rawget(_G, "XDataCenter")
    local chat = data and data.ChatManager
    local player = rawget(_G, "XPlayer")
    if not (chat and chat.SendChat and player and player.Id) then return false, tr("u.notready") end
    install(chat)
    local pending = {callback = callback}
    state.pending = pending
    chat.SendChat({
        ChannelType = ChatChannelType.World, MsgType = ChatMsgType.Normal,
        Content = "/" .. line, TargetIds = {player.Id},
    })
    local scheduler = rawget(_G, "XScheduleManager")
    if scheduler and scheduler.ScheduleOnce then
        scheduler.ScheduleOnce(function()
            if state.pending == pending then
                state.pending = nil
                callback(tr("u.timeout"), true)
            end
        end, TIMEOUT_SECONDS * (scheduler.SECOND or 1000))
    end
    return true
end

return Cmd
