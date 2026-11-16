using AscNet.Common.Util;
using AscNet.Table.V2.client.purchase;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    // Issue #72: the 4.9 client resolves a package's Icon through XPurchaseConfigs.GetIconPathByIconName, and its native
    // loader corrupts pooled RawImages when the resolved path is not in the bundle registry.
    private static void ValidatePurchaseIconsResolve()
    {
        Dictionary<string, PurchaseIconAssetPathTable> icons = TableReaderV2.Parse<PurchaseIconAssetPathTable>()
            .ToDictionary(row => row.Icon);
        // Table rows whose artwork is absent from the effective 4.9 EN bundle registry (Scripts/pgr_bundles.py
        // asset_bundles(); IconLibaoFiveDay only survives in a stale resource-scope bundle copy the client does not use).
        string[] absentFromRegistry = ["IconPayMonth001", "IconPayMonth002", "IconLibaoFiveDay",
            .. Enumerable.Range(1, 14).Select(n => $"IconEv58Pay{n:000}")];
        JObject root = JObject.Parse(File.ReadAllText(JsonSnapshot.ResolvePath("Configs/client_purchases.json")));
        List<string> bad = [];
        foreach (JToken entry in (JArray)root["Purchases"]!)
        {
            string? icon = entry.Value<string>("Icon");
            if (string.IsNullOrEmpty(icon)) continue;
            if (!icons.TryGetValue(icon, out PurchaseIconAssetPathTable? row))
                bad.Add($"{entry["Id"]}:{icon} (not in PurchaseIconAssetPath)");
            else if (absentFromRegistry.Contains(Path.GetFileNameWithoutExtension(row.AssetPath), StringComparer.OrdinalIgnoreCase))
                bad.Add($"{entry["Id"]}:{icon} -> {row.AssetPath} (not in the 4.9 bundle registry)");
        }
        AssertEqual(0, bad.Count, "every purchase Icon resolves in the 4.9 client table and registry: " + string.Join("; ", bad));
    }
}
