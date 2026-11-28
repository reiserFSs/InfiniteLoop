-- Name search behind the AscNet Menu's id fields: each kind lists {id, name, hint, key} from the client's own localized
-- config getters (names follow the game language in every edition). A list is built on first use after Lookup.Reset()
-- (the menu calls it on open), so typing only scans an already-built list. Spec: a field's "lookup" (or "lookupBy").
local Lookup = {}
local LIMIT = 8
local cache = {}

local function G(name) return rawget(_G, name) end

local function add(list, id, name, hint, extra)
    if id and name and name ~= "" then
        list[#list + 1] = {id = id, name = name, hint = hint, key = (name .. " " .. (extra or "")):lower()}
    end
end

local function plain(source, nameOf, labelKey)
    return function(tr)
        local list = {}
        for id, row in pairs(source()) do add(list, id, nameOf(id, row), tr(labelKey)) end
        return list
    end
end

local function characterName(id)
    local agency = XMVCA.XCharacter
    local ok, name = pcall(agency.GetCharacterFullNameStr, agency, id)
    return ok and name or agency:GetCharacterName(id)
end

local function equipConfigs() return XMVCA.XEquip:GetConfigEquip() end
local function equipKind(tr, config) return tr(config.Site == 0 and "l.weapon" or "l.memory") end

local kinds = {}
kinds["item"] = plain(function() return XItemConfigs.GetItemTemplates() end, function(_, row) return row.Name end, "l.item")
kinds["equip"] = function(tr)
    local list = {}
    for id, config in pairs(equipConfigs()) do
        local suit = config.SuitId > 0 and XMVCA.XEquip:GetSuitName(config.SuitId) or ""
        add(list, id, config.Name, equipKind(tr, config) .. (suit ~= "" and (" " .. suit) or ""), suit)
    end
    return list
end
kinds["equip-suit"] = plain(function() return XMVCA.XEquip:GetConfigEquipSuit() end, function(_, row) return row.Name end, "l.suit")
-- The player's own copies: the id is the record id; the wearer's name is searchable too.
kinds["owned-equip"] = function(tr)
    local list, configs = {}, equipConfigs()
    for id, equip in pairs(XMVCA.XEquip:GetEquipDic()) do
        local config = configs[equip.TemplateId]
        if config then
            local wearer = equip.CharacterId > 0 and characterName(equip.CharacterId) or nil
            add(list, id, config.Name .. " " .. tr("u.lv", equip.Level),
                equipKind(tr, config) .. ", " .. (wearer and tr("u.wornby", wearer) or tr("u.unworn")), wearer)
        end
    end
    return list
end
kinds["character"] = function(tr)
    local list = {}
    for id in pairs(XMVCA.XCharacter:GetCharacterTemplates()) do add(list, id, characterName(id), tr("l.character")) end
    return list
end
kinds["owned-character"] = function(tr)
    local list = {}
    for _, character in ipairs(XMVCA.XCharacter:GetOwnCharacterList()) do
        add(list, character.Id, characterName(character.Id), tr("l.character") .. ", " .. tr("u.lv", character.Level))
    end
    return list
end
kinds["cub"] = plain(function() return XPartnerConfigs.GetPartnerTemplateCfg() end, function(_, row) return row.Name end, "l.cub")
kinds["owned-cub"] = function(tr)
    local list = {}
    for _, cub in pairs(XDataCenter.PartnerManager.GetPartnerOverviewDataList(nil, nil, false)) do
        local carrier = cub.CharacterId > 0 and characterName(cub.CharacterId) or nil
        add(list, cub.Id, cub:GetName(),
            tr("u.lv", cub.Level) .. ", " .. (carrier and tr("u.carriedby", carrier) or tr("u.uncarried")), carrier)
    end
    return list
end
kinds["stage"] = plain(function() return XMVCA.XFuben:GetStageCfgs() end, function(_, row) return row.Name end, "l.stage")
kinds["medal"] = plain(function() return XMedalConfigs.GetMeadalConfigs() end, function(_, row) return row.Name end, "l.medal")
-- Nameplate names are content-map codes; XMedalConfigs resolves them to text.
kinds["nameplate"] = plain(function() return XTableManager.ReadByIntKey("Share/Nameplate/Nameplate.tab", XTable.XTableNameplate, "Id") end,
    function(id) return XMedalConfigs.GetNameplateName(id) end, "l.nameplate")
kinds["emoji"] = plain(function() return XTableManager.ReadByIntKey("Share/Chat/Emoji.tab", XTable.XTableEmoji, "Id") end,
    function(_, row) return row.Name end, "l.emoji")
kinds["title"] = plain(function() return XMedalConfigs.GetScoreTitlesConfigs() end, function(_, row) return row.Name end, "l.title")
kinds["chatboard"] = plain(function() return XChatConfigs.GetChatBoardCfg() end, function(_, row) return row.Name end, "l.chatboard")
kinds["portrait"] = plain(function() return XHeadPortraitConfigs.GetHeadPortraitsCfg() end, function(_, row) return row.Name end, "l.portrait")
kinds["furniture"] = plain(function() return XFurnitureConfigs.GetAllFurnitures() end, function(_, row) return row.Name end, "l.furniture")

function Lookup.Reset() cache = {} end

local function entries(kind, tr)
    if cache[kind] then return cache[kind] end
    local ok, list = pcall(kinds[kind], tr)
    if not ok then
        local log = G("XLog")
        if log then log.Warning("[AscNetMenu] name list '" .. kind .. "' failed: " .. tostring(list)) end
        list = {}
    end
    table.sort(list, function(a, b) return a.id < b.id end)
    cache[kind] = list
    return list
end

-- Up to LIMIT entries: the exact id first, then names containing the query (case-insensitive, by id order).
function Lookup.Search(kind, query, tr)
    local text = tostring(query or ""):match("^%s*(.-)%s*$"):lower()
    local found = {}
    if text == "" or not kinds[kind] then return found end
    local number = tonumber(text:match("^%d+$"))
    for _, entry in ipairs(entries(kind, tr)) do
        if not number and #found >= LIMIT then break end
        if entry.id == number then
            table.insert(found, 1, entry)
        elseif #found < LIMIT and entry.key:find(text, 1, true) then
            found[#found + 1] = entry
        end
    end
    while #found > LIMIT do found[#found] = nil end
    return found
end

return Lookup
