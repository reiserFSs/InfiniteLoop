-- AscNet server-owned purchase catalog icons
-- Keep original artwork when the installed client can resolve it. Package IDs,
-- reward IDs and asset paths are supplied by the catalog and client tables.
-- patch_local_store.py replaces this nil with the textures actually inside
-- the installed purchase image bundles. A folder bundle can be indexed while
-- the texture the icon table names was never shipped.
AscNetPurchaseBundleAssets = nil
local AscNetPurchaseAssetAvailable = {}
local function AscNetHasPurchaseAsset(path)
    if not path or path == "" then return false end
    if AscNetPurchaseAssetAvailable[path] == nil then
        local bundle = CS.XResourceManager.GetBundleUrl(path)
        local available = bundle ~= nil and bundle ~= ""
        if available and AscNetPurchaseBundleAssets then
            local lower = string.lower(path)
            local folder = string.match(lower, "^(.*)/[^/]+$")
            local listed = folder and AscNetPurchaseBundleAssets[folder .. ".ab"]
            if listed then
                available = listed[lower] == true
            end
        end
        AscNetPurchaseAssetAvailable[path] = available
    end
    return AscNetPurchaseAssetAvailable[path]
end

function XPurchaseConfigs.GetIconPathByIconName(iconName, purchaseData)
    local configured = PurchaseIconAssetPathConfig[iconName]
    if not purchaseData then
        return configured
    end
    local asset = configured and configured.AssetPath
    local cover = configured and configured.CoverImgPath
    if not AscNetHasPurchaseAsset(asset) then asset = nil end
    if not AscNetHasPurchaseAsset(cover) then cover = nil end
    local function findRewardIcon(rewards)
        for _, reward in ipairs(rewards or {}) do
            local path = XGoodsCommonManager.GetGoodsIcon(reward.TemplateId)
            if AscNetHasPurchaseAsset(path) then
                return path
            end
        end
    end
    if not asset then
        asset = findRewardIcon(purchaseData.RewardGoodsList)
            or findRewardIcon(purchaseData.DailyRewardGoodsList)
    end
    if not asset and purchaseData.PurchaseSignInInfo then
        for _, rewardId in ipairs(purchaseData.PurchaseSignInInfo.PurchaseSignInRewardInfos or {}) do
            asset = findRewardIcon(XRewardManager.GetRewardList(rewardId))
            if asset then break end
        end
    end
    if asset or cover then
        -- Never mutate the shared client table or reuse another card's image.
        return { AssetPath = asset or cover, CoverImgPath = cover }
    end
    return nil
end
