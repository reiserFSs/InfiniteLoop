using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Logging;
using AscNet.Table.V2.client.draw;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;

namespace AscNet.GameServer.Game
{
    internal class DrawManager
    {
        public static readonly List<DrawSceneTable> drawSceneTables = TableReaderV2.Parse<DrawSceneTable>();
        public static readonly List<DrawPreviewTable> drawPreviewTables = TableReaderV2.Parse<DrawPreviewTable>();
        public static readonly List<CharacterTable> charactersTables = TableReaderV2.Parse<CharacterTable>();
        public static readonly List<CharacterQualityTable> characterQualitiesTables = TableReaderV2.Parse<CharacterQualityTable>();
        private static readonly List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
        private static readonly List<ItemTable> itemTables = TableReaderV2.Parse<ItemTable>();
        private static readonly HashSet<int> drawPreviewIds = drawPreviewTables.Select(x => x.Id).ToHashSet();
        private static readonly HashSet<int> drawWaferShowIds = TableReaderV2.Parse<DrawWaferShowTable>().Select(x => x.Id).ToHashSet();
        private static readonly Logger log = new(typeof(DrawManager), LogLevel.DEBUG, LogLevel.DEBUG);
        private const int MinDrawItemShowQuality = 3;
        private static readonly object stateLock = new();
        private static readonly Dictionary<long, Dictionary<int, DrawProgress>> drawProgressByPlayer = new();
        private static readonly Dictionary<long, Dictionary<int, Dictionary<int, int>>> selectedDrawByPlayerGroup = new();
        private static readonly Dictionary<long, Dictionary<int, int>> switchCountByPlayerGroup = new();
        private static readonly Dictionary<(long PlayerId, int GroupId, int GroupSubType), List<DrawHistoryEntry>> drawHistoryByPlayerGroup = new();

        #region DrawTags
        public const int TagBase = 1;
        public const int TagEvent = 2;
        public const int TagSpecialEvent = 3;
        public const int TagTargetUniframe = 4;
        public const int TagCollab = 5;
        public const int TagEndlessSummerBlue = 6;
        public const int TagCUB = 7;
        #endregion

        #region Groups
        public const int GroupMemberTarget = 1;
        public const int GroupWeaponResearch = 2;
        public const int GroupTargetWeaponResearch = 4;
        public const int GroupDormitoryResearch = 6;
        public const int GroupThemedTargetWeapon = 10;
        public const int GroupThemedEventConstruct = 11;
        public const int GroupArrivalConstruct = 12;
        public const int GroupFateArrivalConstruct = 13;
        public const int GroupArrivalEventConstruct = 14;
        public const int GroupFateThemedConstruct = 15;
        public const int GroupTargetUniframe = 16;
        public const int GroupAnniversary = 17;
        public const int GroupFateAnniversaryLimited = 18;
        public const int GroupCollabTarget = 19;
        public const int GroupFateCollabTarget = 20;
        public const int GroupCollabWeaponTarget = 21;
        public const int GroupCUBTarget = 22;
        public const int GroupWishingTarget = 23;
        public const int GroupFateWishingTarget = 24;
        #endregion

        private sealed record DrawProgress(int TodayCount, int TotalCount);
        private sealed record DrawHistoryEntry(RewardGoods RewardGoods, long DrawTime);

        private sealed record DrawGroupDefinition(
            int Id,
            int Tag,
            int Order,
            int Priority,
            int UseItemId,
            int MaxBottomTimes,
            int Type,
            string Banner,
            Dictionary<int, int> DefaultUseDrawIdDict,
            List<int> OptionalDrawIdList,
            List<int> TagBlackListDrawIds,
            long BannerBeginTime,
            long BannerEndTime,
            int ConditionId,
            int ShowPredictType,
            long StartTime,
            long EndTime);

        private sealed record DrawInfoTemplate(
            int Id,
            int GroupId,
            int DrawType,
            int UseItemId,
            int UseItemCount,
            int BaseTodayCount,
            int BaseTotalCount,
            int BottomTimes,
            int MaxBottomTimes,
            long StartTime,
            long EndTime,
            string Banner,
            Dictionary<int, string> Resources,
            Dictionary<int, int> ResourceIds,
            List<int> PurchaseUiType,
            List<int> PurchaseId,
            int CapacityCheckType,
            int GroupSubType);

        private static readonly DrawGroupDefinition[] GroupDefinitions =
        [
            new(1, 4, 1, 9000, 50000, 60, 1, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [0] = 101 }, [101, 3000, 3001, 3002, 3003, 3004, 3005, 3006, 3007, 3008, 3010, 3012, 3014, 3016, 3018, 3020, 3022, 3024, 3026, 3028, 3030, 3032, 3034, 3036], [], 1632470400, 0, 0, 0, 0, 0),
            new(2, 4, 1, 1000, 50001, 30, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaboration07.prefab", new() { [0] = 201 }, [201], [], 0, 0, 0, 0, 0, 0),
            new(4, 1, 1, 500, 50003, 30, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1WeaponPower.prefab", new() { [0] = 371, [3] = 378 }, [301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319, 320, 321, 322, 323, 324, 325, 326, 327, 328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 338, 339, 340, 341, 342, 343, 344, 345, 346, 347, 348, 349, 350, 351, 353, 354, 355, 356, 357, 358, 359, 360, 361, 362, 363, 364, 365, 366, 367, 370, 371, 372, 374, 375, 376, 377, 378, 379], [378, 379], 1780653600, 1784242800, 0, 0, 0, 0),
            new(11, 2, 4002, 21000, 50005, 60, 2, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5Power02.prefab", new() { [0] = 0, [5] = 1488 }, [1488, 1498], [1498, 1488], 1780358400, 1784242800, 0, 0, 1780358400, 1784242800),
            new(12, 3, 1002, 14000, 50005, 60, 8, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5.prefab", new() { [0] = 1494 }, [1492, 1493, 1494], [], 1782370800, 1783580340, 0, 1, 1575540000, 1784789940),
            new(13, 3, 1001, 13000, 50005, 100, 8, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5.prefab", new() { [0] = 2487 }, [2486, 2487, 2488], [], 1782370800, 1783580340, 0, 1, 1575540000, 1784789940),
            new(15, 2, 4001, 20000, 50005, 100, 2, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5Power02.prefab", new() { [0] = 0 }, [2482, 2492], [2492, 2482], 1780358400, 1784242800, 0, 0, 1780358400, 1784242800),
            new(16, 4, 1, 100, 50005, 10, 2, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [0] = 4003 }, [4001, 4003, 4005, 4007, 4009, 4011, 4013], [], 1649923200, 0, 8005, 0, 0, 0),
            new(22, 7, 1, 8000, 50009, 20, 8, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1CubPower.prefab", new() { [0] = 7059, [4] = 7065 }, [7002, 7004, 7006, 7008, 7010, 7012, 7014, 7016, 7018, 7020, 7022, 7024, 7026, 7028, 7030, 7032, 7034, 7036, 7038, 7040, 7042, 7044, 7046, 7048, 7052, 7054, 7057, 7059, 7061, 7063, 7064, 7065], [7064, 7065], 1780653600, 1784242800, 8006, 0, 0, 0)
        ];

        private static readonly DrawInfoTemplate[] RetailDrawInfoTemplates =
        [
            new(101, 1, 1, 50000, 250, 0, 71, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new(), new(), [], [], 0, 0),
            new(201, 2, 1, 50001, 250, 0, 13, 17, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaboration07.prefab", new(), new(), [], [], 0, 0),
            new(301, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponShilangqiang.png" }, new() { [1] = 2016001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(302, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHongliankuangshi.png" }, new() { [1] = 2026001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(303, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponYingda.png" }, new() { [1] = 2026002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(304, 4, 2, 50003, 250, 0, 9, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponLingshi.png" }, new() { [1] = 2036001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(305, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponShencizhe.png" }, new() { [1] = 2036002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(306, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponTianlongzhifeng.png" }, new() { [1] = 2036003 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(307, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponLeimier.png" }, new() { [1] = 2046001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(308, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponQimeila.png" }, new() { [1] = 2056001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(309, 4, 2, 50003, 250, 0, 0, 26, 30, 1628064000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponDashenwei.png" }, new() { [1] = 2066001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(310, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponQihei.png" }, new() { [1] = 2066002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(311, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponKuangluanronghepao.png" }, new() { [1] = 2076001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(312, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponZhanhunzhe.png" }, new() { [1] = 2086001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(313, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponLingdudingbiao.png" }, new() { [1] = 2016002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(314, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHejihonglong.png" }, new() { [1] = 2076002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(315, 4, 2, 50003, 250, 0, 0, 26, 30, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponJiyun.png" }, new() { [1] = 2056002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(316, 4, 2, 50003, 250, 0, 10, 26, 30, 1628064000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHongying.png" }, new() { [1] = 2026004 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(317, 4, 2, 50003, 250, 0, 0, 26, 30, 1631260800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponZhenmingzhe.png" }, new() { [1] = 2086002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(318, 4, 2, 50003, 250, 0, 0, 26, 30, 1632470400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponWeizi.png" }, new() { [1] = 2096001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(319, 4, 2, 50003, 250, 0, 0, 26, 30, 1634112000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponLaitening.png" }, new() { [1] = 2046002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(320, 4, 2, 50003, 250, 0, 0, 26, 30, 1634716800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponSaiyin.png" }, new() { [1] = 2016003 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(321, 4, 2, 50003, 250, 0, 0, 26, 30, 1637222400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponShengaiermo.png" }, new() { [1] = 2096002 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(322, 4, 2, 50003, 250, 0, 0, 26, 30, 1640246400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponZhuhua.png" }, new() { [1] = 2026005 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(323, 4, 2, 50003, 250, 0, 0, 26, 30, 1643270400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponTanatuosi.png" }, new() { [1] = 2106001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(324, 4, 2, 50003, 250, 0, 0, 26, 30, 1644220800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponShaliye.png" }, new() { [1] = 2026006 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(325, 4, 2, 50003, 250, 0, 0, 26, 30, 1646294400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponGanggenier.png" }, new() { [1] = 2116001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(326, 4, 2, 50003, 250, 0, 0, 26, 30, 1649923200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponQinghe.png" }, new() { [1] = 2136001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(327, 4, 2, 50003, 250, 0, 0, 26, 30, 1650873600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponBaji.png" }, new() { [1] = 2146001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(328, 4, 2, 50003, 250, 0, 0, 26, 30, 1652947200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponAozima.png" }, new() { [1] = 2176001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(329, 4, 2, 50003, 250, 0, 0, 26, 30, 1660723200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponLinzhongzhuren.png" }, new() { [1] = 2196001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(330, 4, 2, 50003, 250, 0, 0, 26, 30, 1661587200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponTianping.png" }, new() { [1] = 2186001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(331, 4, 2, 50003, 250, 0, 0, 26, 30, 1663401600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponAboluo.png" }, new() { [1] = 2206001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(332, 4, 2, 50003, 250, 0, 0, 26, 30, 1666166400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHulu.png" }, new() { [1] = 2216001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(333, 4, 2, 50003, 250, 0, 10, 26, 30, 1668758400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponBusiniao.png" }, new() { [1] = 2236001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(334, 4, 2, 50003, 250, 0, 0, 26, 30, 1671782400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponDulandeer.png" }, new() { [1] = 2226001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(335, 4, 2, 50003, 250, 0, 0, 26, 30, 1674201600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHesitiya.png" }, new() { [1] = 2246001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(336, 4, 2, 50003, 250, 0, 0, 26, 30, 1677744000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponSalasiteluo.png" }, new() { [1] = 2256001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(337, 4, 2, 50003, 250, 0, 0, 26, 30, 1680940800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponWuyu.png" }, new() { [1] = 2266001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(338, 4, 2, 50003, 250, 0, 0, 26, 30, 1683878400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponJubao.png" }, new() { [1] = 2276001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(339, 4, 2, 50003, 250, 0, 0, 26, 30, 1686902400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponJialateya.png" }, new() { [1] = 2096003 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(340, 4, 2, 50003, 250, 0, 0, 26, 30, 1689321600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponAnJiShanGuang.png" }, new() { [1] = 2286001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(341, 4, 2, 50003, 250, 0, 0, 26, 30, 1692777600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponPuluomixiusi.png" }, new() { [1] = 2296001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(342, 4, 2, 50003, 250, 0, 1, 26, 30, 1695801600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHeKaTe.png" }, new() { [1] = 2306001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(343, 4, 2, 50003, 250, 0, 0, 26, 30, 1699495200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponJingmirenxingzhisheng.png" }, new() { [1] = 2316001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(344, 4, 2, 50003, 250, 0, 0, 26, 30, 1702346400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponSituokesi.png" }, new() { [1] = 2326001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(345, 4, 2, 50003, 250, 0, 0, 26, 30, 1705543200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponXinghaimanyouzhe.png" }, new() { [1] = 2336001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(346, 4, 2, 50003, 250, 0, 66, 26, 30, 1708653600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponYehuang.png" }, new() { [1] = 2346001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(347, 4, 2, 50003, 250, 0, 0, 26, 30, 1712541600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponGujin.png" }, new() { [1] = 2356001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(348, 4, 2, 50003, 250, 0, 0, 26, 30, 1715220000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponManajiaermu.png" }, new() { [1] = 2366001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(349, 4, 2, 50003, 250, 0, 0, 26, 30, 1718330400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponChihongpaoxiao.png" }, new() { [1] = 2376001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(350, 4, 2, 50003, 250, 0, 0, 26, 30, 1721008800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponAstraia.png" }, new() { [1] = 2046003 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(351, 4, 2, 50003, 250, 0, 3, 26, 30, 1724205600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponMotisi.png" }, new() { [1] = 2386001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(353, 4, 2, 50003, 250, 0, 0, 26, 30, 1730340000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponAnyeliesun.png" }, new() { [1] = 2406001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(354, 4, 2, 50003, 250, 0, 1, 26, 30, 1733277600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponMingyaoqueying.png" }, new() { [1] = 2416001 }, [5, 6, 2], [90694, 90695, 90696, 90697, 90700, 90701, 90702, 90705, 90706, 90292], 0, 0),
            new(355, 4, 2, 50003, 250, 0, 0, 26, 30, 1737100800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHaikeruiyin.png" }, new() { [1] = 2426001 }, [5, 2], [90717, 90718, 90723, 90726, 90727, 90741, 90743], 0, 0),
            new(356, 4, 2, 50003, 250, 0, 29, 26, 30, 1736474400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponLvtiaochongsu.png" }, new() { [1] = 2436001 }, [5, 2], [90717, 90718, 90723, 90726, 90727, 90741, 90743], 0, 0),
            new(357, 4, 2, 50003, 250, 0, 0, 26, 30, 1737705600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponSaisitusi.png" }, new() { [1] = 2446001 }, [5, 2], [90717, 90718, 90723, 90726, 90727, 90741, 90743], 0, 0),
            new(358, 4, 2, 50003, 250, 0, 1, 26, 30, 1740016800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponPengyoudiemeng.png" }, new() { [1] = 2456001 }, [5, 2], [90750, 90751, 90756, 90759, 90760, 90772, 90774], 0, 0),
            new(359, 4, 2, 50003, 250, 0, 24, 26, 30, 1740643200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponMingtongxuzhou.png" }, new() { [1] = 2466001 }, [5, 2], [90750, 90751, 90756, 90759, 90760, 90772, 90774], 0, 0),
            new(360, 4, 2, 50003, 250, 0, 10, 26, 30, 1743559200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponFuxiaoyanling.png" }, new() { [1] = 2476001 }, [5, 2], [90778, 90779, 90785, 90786, 90790, 90808, 90810], 0, 0),
            new(361, 4, 2, 50003, 250, 0, 0, 26, 30, 1743840000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponBaZhi.png" }, new() { [1] = 2486001 }, [5, 2], [90778, 90779, 90785, 90786, 90790, 90808, 90810], 0, 0),
            new(362, 4, 2, 50003, 250, 0, 27, 26, 30, 1744185600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponHeersheer.png" }, new() { [1] = 2496001 }, [5, 2], [90778, 90779, 90785, 90786, 90790, 90808, 90810], 0, 0),
            new(363, 4, 2, 50003, 250, 0, 30, 26, 30, 1747188000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponYishiZhongyan.png" }, new() { [1] = 2506001 }, [5, 2], [90815, 90821, 90822, 90825, 90826, 90828, 90830], 0, 0),
            new(364, 4, 2, 50003, 250, 0, 0, 26, 30, 1747814400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponFanXiao.png" }, new() { [1] = 2516001 }, [5, 2], [90815, 90821, 90822, 90825, 90826, 90828, 90830], 0, 0),
            new(365, 4, 2, 50003, 250, 0, 10, 26, 30, 1750816800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponYaoLan.png" }, new() { [1] = 2526001 }, [5, 2], [90852, 90861, 90862, 90865, 90866, 90856, 90858], 0, 0),
            new(366, 4, 2, 50003, 250, 0, 0, 26, 30, 1751097600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponJietawei.png" }, new() { [1] = 2536001 }, [5, 2], [90852, 90861, 90862, 90865, 90866, 90856, 90858], 0, 0),
            new(367, 4, 2, 50003, 250, 0, 30, 26, 30, 1751443200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawWeaponJinjieguixu.png" }, new() { [1] = 2546001 }, [5, 2], [90852, 90861, 90862, 90865, 90866, 90856, 90858], 0, 0),
            new(370, 4, 2, 50003, 250, 0, 20, 26, 30, 1758247200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new(), new() { [1] = 2576001 }, [5, 2], [90918, 90925, 90921, 90922, 90926], 0, 0),
            new(371, 4, 2, 50003, 250, 0, 0, 26, 30, 1758870000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new(), new() { [1] = 2586001 }, [5, 2], [90918, 90925, 90921, 90922, 90926], 0, 0),
            new(372, 4, 2, 50003, 250, 0, 21, 26, 30, 1760079600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new(), new() { [1] = 2596001 }, [5, 2], [90918, 90925, 90921, 90922, 90926], 0, 0),
            new(374, 4, 2, 50003, 250, 0, 0, 26, 30, 1766466000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new(), new() { [1] = 2616001 }, [5, 6], [1972, 1973, 1978, 1979, 1983, 1984], 0, 0),
            new(375, 4, 2, 50003, 250, 0, 0, 26, 30, 1770076800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new(), new() { [1] = 2626001 }, [5, 6], [1996, 1997, 2002, 2003, 2007, 2008], 0, 0),
            new(376, 4, 2, 50003, 250, 0, 0, 26, 30, 1773705600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new(), new() { [1] = 2636001 }, [5, 6], [202, 2017, 2018, 2023, 204, 2028, 2029], 0, 0),
            new(377, 4, 2, 50003, 250, 0, 0, 26, 30, 1776729600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Weapon.prefab", new(), new() { [1] = 2646001 }, [5, 6], [202, 2036, 2042, 2037, 2043, 204, 2047, 2048], 0, 0),
            new(378, 4, 2, 50003, 250, 0, 30, 26, 30, 1780358400, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1WeaponPower.prefab", new(), new() { [1] = 2656001 }, [5, 6], [202, 2060, 2061, 2066, 204, 2071, 2072], 0, 3),
            new(379, 4, 2, 50003, 250, 0, 3, 26, 30, 1780653600, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1WeaponPower.prefab", new(), new() { [1] = 2606001 }, [5, 6], [202, 2060, 2061, 2066, 204, 2071, 2072], 0, 3),
            new(1488, 11, 3, 50005, 250, 0, 133, 47, 60, 1780358400, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5Power02.prefab", new(), new() { [1] = 1021007 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 5),
            new(1492, 12, 3, 50005, 250, 0, 0, 47, 60, 1782370800, 1783580340, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5.prefab", new(), new() { [1] = 1291003 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 0),
            new(1493, 12, 3, 50005, 250, 0, 0, 47, 60, 1782370800, 1783580340, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5.prefab", new(), new() { [1] = 1381003 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 0),
            new(1494, 12, 3, 50005, 250, 0, 0, 47, 60, 1782370800, 1783580340, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5.prefab", new(), new() { [1] = 1171004 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 0),
            new(1498, 11, 3, 50005, 250, 0, 0, 47, 60, 1780653600, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5Power01.prefab", new(), new() { [1] = 1031005 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 1),
            new(2482, 15, 3, 50005, 250, 0, 0, 99, 100, 1780358400, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5Power02.prefab", new(), new() { [1] = 1021007 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 6),
            new(2486, 13, 3, 50005, 250, 0, 0, 100, 100, 1782370800, 1783580340, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5.prefab", new(), new() { [1] = 1291003 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 0),
            new(2487, 13, 3, 50005, 250, 0, 0, 100, 100, 1782370800, 1783580340, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5.prefab", new(), new() { [1] = 1381003 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 0),
            new(2488, 13, 3, 50005, 250, 0, 0, 100, 100, 1782370800, 1783580340, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5.prefab", new(), new() { [1] = 1171004 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 0),
            new(2492, 15, 3, 50005, 250, 0, 0, 99, 100, 1780653600, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5Power01.prefab", new(), new() { [1] = 1031005 }, [5, 6, 2], [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072], 0, 2),
            new(3000, 1, 1, 50000, 250, 0, 0, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterLee.png" }, new() { [1] = 1011002 }, [], [], 0, 0),
            new(3001, 1, 1, 50000, 250, 0, 15, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterLucia.png" }, new() { [1] = 1021002 }, [], [], 0, 0),
            new(3002, 1, 1, 50000, 250, 0, 20, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterLiv.png" }, new() { [1] = 1031002 }, [], [], 0, 0),
            new(3003, 1, 1, 50000, 250, 0, 0, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterBianca.png" }, new() { [1] = 1041002 }, [], [], 0, 0),
            new(3004, 1, 1, 50000, 250, 0, 0, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterKamui.png" }, new() { [1] = 1061002 }, [], [], 0, 0),
            new(3005, 1, 1, 50000, 250, 0, 0, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterKarenina.png" }, new() { [1] = 1071002 }, [], [], 0, 0),
            new(3006, 1, 1, 50000, 250, 0, 0, 60, 60, 0, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterWatanabe.png" }, new() { [1] = 1081002 }, [], [], 0, 0),
            new(3007, 1, 1, 50000, 250, 0, 0, 60, 60, 1632470400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterWatanabe2.png" }, new() { [1] = 1081003 }, [], [], 0, 0),
            new(3008, 1, 1, 50000, 250, 0, 0, 60, 60, 1633680000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterAyla.png" }, new() { [1] = 1091002 }, [], [], 0, 0),
            new(3010, 1, 1, 50000, 250, 0, 0, 60, 60, 1637049600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterSophia.png" }, new() { [1] = 1111002 }, [], [], 0, 0),
            new(3012, 1, 1, 50000, 250, 0, 0, 60, 60, 1639641600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterChrome.png" }, new() { [1] = 1121002 }, [], [], 0, 0),
            new(3014, 1, 1, 50000, 250, 0, 38, 60, 60, 1646035200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterVera.png" }, new() { [1] = 1131002 }, [], [], 0, 0),
            new(3016, 1, 1, 50000, 250, 0, 0, 60, 60, 1652688000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterChangyu.png" }, new() { [1] = 1161002 }, [], [], 0, 0),
            new(3018, 1, 1, 50000, 250, 0, 0, 60, 60, 1663142400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterWanshi.png" }, new() { [1] = 1211002 }, [], [], 0, 0),
            new(3020, 1, 1, 50000, 250, 0, 0, 60, 60, 1668585600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterNo21.png" }, new() { [1] = 1221002 }, [], [], 0, 0),
            new(3022, 1, 1, 50000, 250, 0, 0, 60, 60, 1702022400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterBangbinata.png" }, new() { [1] = 1231002 }, [], [], 0, 0),
            new(3024, 1, 1, 50000, 250, 0, 0, 60, 60, 1714982400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterHanying.png" }, new() { [1] = 1241002 }, [], [], 0, 0),
            new(3026, 1, 1, 50000, 250, 0, 0, 60, 60, 1720857600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNameNoctis.png" }, new() { [1] = 1251002 }, [], [], 0, 0),
            new(3028, 1, 1, 50000, 250, 0, 12, 60, 60, 1739952000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterDollbear.png" }, new() { [1] = 1291002 }, [], [], 0, 0),
            new(3030, 1, 1, 50000, 250, 0, 0, 60, 60, 1739952000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterBridget.png" }, new() { [1] = 1301002 }, [], [], 0, 0),
            new(3032, 1, 1, 50000, 250, 0, 0, 60, 60, 1747188000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterBaZhi.png" }, new() { [1] = 1311002 }, [], [], 0, 0),
            new(3034, 1, 1, 50000, 250, 0, 0, 60, 60, 1754445600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterJietawei.png" }, new() { [1] = 1341002 }, [], [], 0, 0),
            new(3036, 1, 1, 50000, 250, 0, 0, 60, 60, 1762981200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationBenchmarkRole.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/DrawCharacterJietawei.png" }, new() { [1] = 1371002 }, [], [], 0, 0),
            new(4001, 16, 1, 50005, 250, 0, 0, 10, 10, 1649923200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNameKamu1.png" }, new() { [1] = 1511003 }, [6], [90289, 90291], 0, 0),
            new(4003, 16, 1, 50005, 250, 0, 0, 10, 10, 1652688000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNameQu1.png" }, new() { [1] = 1521003 }, [6], [90289, 90291], 0, 0),
            new(4005, 16, 1, 50005, 250, 0, 0, 10, 10, 1663142400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNameSailinna1.png" }, new() { [1] = 1531003 }, [6], [90289, 90291], 0, 0),
            new(4007, 16, 1, 50005, 250, 0, 0, 10, 10, 1674086400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNameLuolan1.png" }, new() { [1] = 1541003 }, [6], [90289, 90291], 0, 0),
            new(4009, 16, 1, 50005, 250, 0, 0, 10, 10, 1683360000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNamePulao1.png" }, new() { [1] = 1551003 }, [6], [90289, 90291], 0, 0),
            new(4011, 16, 1, 50005, 250, 0, 0, 10, 10, 1689148800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNameHakama1.png" }, new() { [1] = 1561003 }, [6], [90289, 90291], 0, 0),
            new(4013, 16, 1, 50005, 250, 0, 0, 10, 10, 1695196800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Frame.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationNameNuoan1.png" }, new() { [1] = 1571003 }, [6], [90289, 90291], 0, 0),
            new(7002, 22, 3, 50009, 250, 0, 0, 20, 20, 1666058400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationTingHong.png" }, new() { [1] = 16030000 }, [], [], 1, 0),
            new(7004, 22, 3, 50009, 250, 0, 0, 20, 20, 1668585600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationShuangShi.png" }, new() { [1] = 16040000 }, [], [], 1, 0),
            new(7006, 22, 3, 50009, 250, 0, 0, 20, 20, 1674086400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationXiaoHuang.png" }, new() { [1] = 16060000 }, [], [], 1, 0),
            new(7008, 22, 3, 50009, 250, 0, 0, 20, 20, 1680163200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationCang.png" }, new() { [1] = 16080000 }, [], [], 1, 0),
            new(7010, 22, 3, 50009, 250, 0, 0, 20, 20, 1686297600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationXiaoYue.png" }, new() { [1] = 16100000 }, [], [], 1, 0),
            new(7012, 22, 3, 50009, 250, 0, 0, 20, 20, 1691740800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationMingYue.png" }, new() { [1] = 16110000 }, [], [], 1, 0),
            new(7014, 22, 3, 50009, 250, 0, 0, 20, 20, 1698220800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationFeiLin.png" }, new() { [1] = 16120000 }, [], [], 1, 0),
            new(7016, 22, 3, 50009, 250, 0, 0, 20, 20, 1704873600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationCheJu.png" }, new() { [1] = 16130000 }, [], [], 1, 0),
            new(7018, 22, 3, 50009, 250, 0, 0, 20, 20, 1708070400, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationHongJi.png" }, new() { [1] = 16140000 }, [], [], 1, 0),
            new(7020, 22, 3, 50009, 250, 0, 20, 20, 20, 1711180800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationCheTing.png" }, new() { [1] = 16150000 }, [], [], 1, 0),
            new(7022, 22, 3, 50009, 250, 0, 5, 20, 20, 1717747200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationMingYa.png" }, new() { [1] = 16160000 }, [], [], 1, 0),
            new(7024, 22, 3, 50009, 250, 0, 0, 20, 20, 1723536000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationZhouYi.png" }, new() { [1] = 16170000 }, [], [], 1, 0),
            new(7026, 22, 3, 50009, 250, 0, 0, 20, 20, 1726732800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationQianZe.png" }, new() { [1] = 16180000 }, [], [], 1, 0),
            new(7028, 22, 3, 50009, 250, 0, 0, 20, 20, 1732867200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationYeFu.png" }, new() { [1] = 16190000 }, [], [], 1, 0),
            new(7030, 22, 3, 50009, 250, 0, 20, 20, 20, 1735804800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationHuiYu.png" }, new() { [1] = 16200000 }, [], [], 1, 0),
            new(7032, 22, 3, 50009, 250, 0, 10, 20, 20, 1739952000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationSuE.png" }, new() { [1] = 16210000 }, [], [], 1, 0),
            new(7034, 22, 3, 50009, 250, 0, 0, 20, 20, 1743559200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationMeiWu.png" }, new() { [1] = 16220000 }, [], [], 1, 0),
            new(7036, 22, 3, 50009, 250, 0, 0, 20, 20, 1743559200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationRuiXue.png" }, new() { [1] = 16230000 }, [], [], 1, 0),
            new(7038, 22, 3, 50009, 250, 0, 0, 20, 20, 1747188000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationYiKong.png" }, new() { [1] = 16240000 }, [], [], 1, 0),
            new(7040, 22, 3, 50009, 250, 0, 0, 20, 20, 1747188000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationAnji.png" }, new() { [1] = 16250000 }, [], [], 1, 0),
            new(7042, 22, 3, 50009, 250, 0, 0, 20, 20, 1750816800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationZhuling.png" }, new() { [1] = 16260000 }, [], [], 1, 0),
            new(7044, 22, 3, 50009, 250, 0, 0, 20, 20, 1750816800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationBili.png" }, new() { [1] = 16270000 }, [], [], 1, 0),
            new(7046, 22, 3, 50009, 250, 0, 0, 20, 20, 1754445600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationXuege.png" }, new() { [1] = 16280000 }, [], [], 1, 0),
            new(7048, 22, 3, 50009, 250, 0, 0, 20, 20, 1754445600, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new() { [3] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationXiaolong.png" }, new() { [1] = 16290000 }, [], [], 1, 0),
            new(7052, 22, 3, 50009, 250, 0, 0, 20, 20, 1762981200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new(), new() { [1] = 16320000 }, [], [], 1, 0),
            new(7054, 22, 3, 50009, 250, 0, 0, 20, 20, 1762981200, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new(), new() { [1] = 16330000 }, [], [], 1, 0),
            new(7057, 22, 3, 50009, 250, 0, 0, 20, 20, 1770094800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new(), new() { [1] = 16350000 }, [], [], 1, 0),
            new(7059, 22, 3, 50009, 250, 0, 0, 20, 20, 1773702000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new(), new() { [1] = 16360000 }, [], [], 1, 0),
            new(7061, 22, 3, 50009, 250, 0, 0, 20, 20, 1776726000, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new(), new() { [1] = 16370000 }, [], [], 1, 0),
            new(7063, 22, 3, 50009, 250, 0, 0, 20, 20, 1780354800, 0, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1Cub.prefab", new(), new() { [1] = 16380000 }, [], [], 1, 0),
            new(7064, 22, 3, 50009, 250, 0, 20, 20, 20, 1780358400, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1CubPower.prefab", new() { [4] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationV1/UiDrawCollaborationV1CUB3.png" }, new() { [1] = 16390000 }, [5, 6], [2061, 2067, 2072], 1, 4),
            new(7065, 22, 3, 50009, 250, 0, 0, 20, 20, 1780653600, 1784242800, "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1CubPower.prefab", new() { [4] = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationV1/UiDrawCollaborationV1CUB3.png" }, new() { [1] = 16340000 }, [5, 6], [2061, 2067, 2072], 1, 4)
        ];

        private static readonly Dictionary<int, DrawInfoTemplate> RetailDrawInfoById = RetailDrawInfoTemplates.ToDictionary(x => x.Id);
        private static readonly Dictionary<int, List<DrawInfoTemplate>> RetailDrawInfosByGroup = RetailDrawInfoTemplates
            .GroupBy(x => x.GroupId)
            .ToDictionary(x => x.Key, x => x.OrderBy(info => info.Id).ToList());

        public static List<DrawGroupInfo> GetDrawGroupInfos(long playerId = 0)
        {
            List<DrawGroupInfo> groups = new();
            foreach (DrawGroupDefinition definition in GroupDefinitions)
            {
                if (!RetailDrawInfosByGroup.TryGetValue(definition.Id, out List<DrawInfoTemplate>? infos) || infos.Count == 0)
                    continue;

                Dictionary<int, int> useDrawIdDict = GetUseDrawIdDict(playerId, definition);
                DrawInfo selectedInfo = BuildDrawInfo(GetSelectedTemplate(definition.Id, useDrawIdDict) ?? infos[0], playerId);
                groups.Add(new DrawGroupInfo
                {
                    Id = definition.Id,
                    Tag = definition.Tag,
                    Type = definition.Type,
                    Order = definition.Order,
                    Priority = definition.Priority,
                    UseItemId = definition.UseItemId,
                    BottomTimes = selectedInfo.BottomTimes,
                    MaxBottomTimes = definition.MaxBottomTimes,
                    SwitchDrawIdCount = GetSwitchDrawIdCount(playerId, definition.Id),
                    UseDrawIdDict = useDrawIdDict,
                    OptionalDrawIdList = [.. definition.OptionalDrawIdList],
                    TagBlackListDrawIds = [.. definition.TagBlackListDrawIds],
                    Banner = definition.Banner,
                    StartTime = definition.StartTime,
                    EndTime = definition.EndTime,
                    BannerBeginTime = definition.BannerBeginTime,
                    BannerEndTime = definition.BannerEndTime,
                    ConditionId = definition.ConditionId,
                    ShowPredictType = definition.ShowPredictType,
                });
            }

            return groups;
        }

        public static List<(int DrawGroupId, int Priority)> GetDrawHistoryGroups()
        {
            return GroupDefinitions
                .Where(definition => RetailDrawInfosByGroup.TryGetValue(definition.Id, out List<DrawInfoTemplate>? infos) && infos.Count > 0)
                .Select(definition => (definition.Id, definition.Priority))
                .ToList();
        }

        public static (int BottomTimes, int MaxBottomTimes) GetDrawHistoryStatus(long playerId, int groupId, int groupSubType)
        {
            if (!RetailDrawInfosByGroup.TryGetValue(groupId, out List<DrawInfoTemplate>? infos) || infos.Count == 0)
                return (0, 0);

            DrawInfoTemplate template = infos.FirstOrDefault(info => info.GroupSubType == groupSubType)
                ?? GetSelectedTemplate(groupId, GetUseDrawIdDict(playerId, GroupDefinitions.First(definition => definition.Id == groupId)))
                ?? infos[0];
            DrawInfo drawInfo = BuildDrawInfo(template, playerId);
            return (drawInfo.BottomTimes, drawInfo.MaxBottomTimes);
        }

        public static void RecordDrawHistory(long playerId, int drawId, IEnumerable<RewardGoods> rewards)
        {
            if (!RetailDrawInfoById.TryGetValue(drawId, out DrawInfoTemplate? template))
                return;

            long drawTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            (long PlayerId, int GroupId, int GroupSubType) key = (playerId, template.GroupId, template.GroupSubType);

            lock (stateLock)
            {
                if (!drawHistoryByPlayerGroup.TryGetValue(key, out List<DrawHistoryEntry>? history))
                {
                    history = [];
                    drawHistoryByPlayerGroup[key] = history;
                }

                history.AddRange(rewards.Select(reward => new DrawHistoryEntry(CloneRewardGoods(reward), drawTime)));
                if (history.Count > 100)
                    history.RemoveRange(0, history.Count - 100);
            }
        }

        public static List<(RewardGoods RewardGoods, long DrawTime)> GetDrawHistory(long playerId, int groupId, int groupSubType)
        {
            lock (stateLock)
            {
                return drawHistoryByPlayerGroup.TryGetValue((playerId, groupId, groupSubType), out List<DrawHistoryEntry>? history)
                    ? history.Select(entry => (CloneRewardGoods(entry.RewardGoods), entry.DrawTime)).ToList()
                    : [];
            }
        }

        private static RewardGoods CloneRewardGoods(RewardGoods reward)
        {
            return new RewardGoods
            {
                RewardType = reward.RewardType,
                TemplateId = reward.TemplateId,
                Count = reward.Count,
                Level = reward.Level,
                Quality = reward.Quality,
                Grade = reward.Grade,
                Breakthrough = reward.Breakthrough,
                ConvertFrom = reward.ConvertFrom,
                ShowQuality = reward.ShowQuality,
                Id = reward.Id,
                IsGift = reward.IsGift,
                RewardMulti = reward.RewardMulti
            };
        }

        public static List<DrawAdjustActivityInfo> GetDrawAdjustActivityInfos()
        {
            return
            [
                new DrawAdjustActivityInfo
                {
                    TargetTimes = 1,
                    TargetId = 1241003,
                    ActivityStatus = 0,
                    ActivityId = 3,
                    StartTime = 1763006400,
                    EndTime = 0,
                    AdjustTimes = 1,
                    DrawGroupId = GroupMemberTarget,
                    TargetTemplateIds = [],
                    SourceTemplateIds = [],
                    EffectTargetTemplateIds = [1011003, 1031003, 1061003, 1071003, 1051003, 1021003, 1041003, 1021004, 1141003, 1171003, 1121003, 1131003, 1031004, 1531004, 1051004, 1071004, 1041004, 1011004, 1091003, 1021005, 1221003, 1261003, 1271003, 1081004, 1521004, 1171004, 1241003, 1211003, 1021006, 1051005, 1321003, 1331003, 1531005, 1131004, 1041005, 1381003, 1291003, 1141004, 1391003]
                }
            ];
        }

        public static List<DrawInfo> GetDrawInfosByGroup(int groupId, long playerId = 0)
        {
            return RetailDrawInfosByGroup.TryGetValue(groupId, out List<DrawInfoTemplate>? infos)
                ? infos.Select(info => BuildDrawInfo(info, playerId)).ToList()
                : [];
        }

        public static DrawInfo? GetDrawInfoById(int drawId, long playerId = 0)
        {
            return RetailDrawInfoById.TryGetValue(drawId, out DrawInfoTemplate? template)
                ? BuildDrawInfo(template, playerId)
                : null;
        }

        public static int SetUseDrawId(long playerId, int drawId)
        {
            if (!RetailDrawInfoById.TryGetValue(drawId, out DrawInfoTemplate? template))
                return 0;

            int selectionSlot = GetSelectionSlot(template);
            lock (stateLock)
            {
                if (!selectedDrawByPlayerGroup.TryGetValue(playerId, out Dictionary<int, Dictionary<int, int>>? selectedByGroup))
                {
                    selectedByGroup = new();
                    selectedDrawByPlayerGroup[playerId] = selectedByGroup;
                }

                if (!selectedByGroup.TryGetValue(template.GroupId, out Dictionary<int, int>? selectedSlots))
                {
                    DrawGroupDefinition definition = GroupDefinitions.First(x => x.Id == template.GroupId);
                    selectedSlots = new(definition.DefaultUseDrawIdDict);
                    selectedByGroup[template.GroupId] = selectedSlots;
                }

                selectedSlots[selectionSlot] = drawId;

                if (!switchCountByPlayerGroup.TryGetValue(playerId, out Dictionary<int, int>? switchCountByGroup))
                {
                    switchCountByGroup = new();
                    switchCountByPlayerGroup[playerId] = switchCountByGroup;
                }
                switchCountByGroup[template.GroupId] = switchCountByGroup.GetValueOrDefault(template.GroupId) + 1;
                return switchCountByGroup[template.GroupId];
            }
        }

        public static DrawInfo? ApplyDrawProgress(long playerId, int drawId, int count)
        {
            if (!RetailDrawInfoById.ContainsKey(drawId))
                return null;

            lock (stateLock)
            {
                if (!drawProgressByPlayer.TryGetValue(playerId, out Dictionary<int, DrawProgress>? playerProgress))
                {
                    playerProgress = new();
                    drawProgressByPlayer[playerId] = playerProgress;
                }

                DrawProgress current = playerProgress.GetValueOrDefault(drawId, new DrawProgress(0, 0));
                playerProgress[drawId] = current with
                {
                    TodayCount = current.TodayCount + count,
                    TotalCount = current.TotalCount + count
                };
            }

            return GetDrawInfoById(drawId, playerId);
        }

        public static int GetGroupByDrawId(int drawId)
        {
            return RetailDrawInfoById.TryGetValue(drawId, out DrawInfoTemplate? template) ? template.GroupId : 0;
        }

        public static List<RewardGoods> DrawDraw(long playerId, int drawId, int pullOffset = 0)
        {
            List<RewardGoods> rewards = new();
            if (!RetailDrawInfoById.TryGetValue(drawId, out DrawInfoTemplate? template))
            {
                log.Error($"Invalid draw id {drawId}");
                return rewards;
            }

            DrawProgress progress = GetDrawProgress(playerId, drawId);
            int bottomTimesBeforePull = GetBottomTimes(template.MaxBottomTimes, template.BottomTimes, progress.TotalCount + pullOffset);
            bool forceRare = template.MaxBottomTimes > 0 && bottomTimesBeforePull == 1;

            RewardGoods? reward = DrawRetailReward(template, forceRare);
            if (reward is not null)
                rewards.Add(reward);

            return rewards;
        }

        private static RewardGoods? DrawRetailReward(DrawInfoTemplate template, bool forceRare)
        {
            return template.GroupId switch
            {
                GroupWeaponResearch or GroupTargetWeaponResearch => DrawEquipReward(template, forceRare),
                GroupFateArrivalConstruct or GroupFateThemedConstruct or GroupFateAnniversaryLimited or GroupFateCollabTarget or GroupFateWishingTarget => DrawLegacyCharacterReward(template, forceRare),
                GroupCUBTarget => DrawFallbackItemReward(),
                _ => DrawCharacterReward(template, forceRare)
            };
        }

        private static DrawInfo BuildDrawInfo(DrawInfoTemplate template, long playerId)
        {
            DrawProgress progress = GetDrawProgress(playerId, template.Id);
            return new DrawInfo
            {
                Id = template.Id,
                GroupId = template.GroupId,
                DrawType = template.DrawType,
                UseItemId = template.UseItemId,
                UseItemCount = template.UseItemCount,
                TodayCount = template.BaseTodayCount + progress.TodayCount,
                TotalCount = template.BaseTotalCount + progress.TotalCount,
                BottomTimes = GetBottomTimes(template.MaxBottomTimes, template.BottomTimes, progress.TotalCount),
                MaxBottomTimes = template.MaxBottomTimes,
                StartTime = template.StartTime,
                EndTime = template.EndTime,
                Banner = template.Banner,
                Resources = new(template.Resources),
                ResourceIds = new(template.ResourceIds),
                BtnDrawCount = [1, 10],
                PurchaseUiType = [.. template.PurchaseUiType],
                PurchaseId = [.. template.PurchaseId],
                CapacityCheckType = template.CapacityCheckType,
                UpGoodsId = 0,
                IsShowShop = template.Id is 374 or 376,
                GroupSubType = template.GroupSubType,
                ShowPriority = 0
            };
        }

        private static DrawProgress GetDrawProgress(long playerId, int drawId)
        {
            lock (stateLock)
            {
                if (drawProgressByPlayer.TryGetValue(playerId, out Dictionary<int, DrawProgress>? playerProgress)
                    && playerProgress.TryGetValue(drawId, out DrawProgress? progress)
                    && progress is not null)
                    return progress;
            }

            return new DrawProgress(0, 0);
        }

        private static int GetBottomTimes(int maxBottomTimes, int templateBottomTimes, int progressCount)
        {
            if (maxBottomTimes <= 0)
                return 0;

            int consumed = maxBottomTimes - templateBottomTimes;
            consumed = (consumed + progressCount) % maxBottomTimes;
            return consumed == 0 ? maxBottomTimes : maxBottomTimes - consumed;
        }

        private static Dictionary<int, int> GetUseDrawIdDict(long playerId, DrawGroupDefinition definition)
        {
            lock (stateLock)
            {
                if (selectedDrawByPlayerGroup.TryGetValue(playerId, out Dictionary<int, Dictionary<int, int>>? selectedByGroup)
                    && selectedByGroup.TryGetValue(definition.Id, out Dictionary<int, int>? selectedSlots))
                    return new(selectedSlots);
            }

            return new(definition.DefaultUseDrawIdDict);
        }

        private static DrawInfoTemplate? GetSelectedTemplate(int groupId, Dictionary<int, int> useDrawIdDict)
        {
            foreach (int drawId in useDrawIdDict.OrderByDescending(x => x.Key).Select(x => x.Value))
            {
                if (drawId > 0 && RetailDrawInfoById.TryGetValue(drawId, out DrawInfoTemplate? template))
                    return template;
            }

            return RetailDrawInfosByGroup.TryGetValue(groupId, out List<DrawInfoTemplate>? infos) ? infos.FirstOrDefault() : null;
        }

        private static int GetSelectionSlot(DrawInfoTemplate template)
        {
            DrawGroupDefinition? definition = GroupDefinitions.FirstOrDefault(x => x.Id == template.GroupId);
            if (definition is null)
                return 0;

            foreach ((int slot, int drawId) in definition.DefaultUseDrawIdDict)
            {
                if (drawId == template.Id)
                    return slot;
            }

            if (definition.TagBlackListDrawIds.Contains(template.Id))
                return template.GroupId switch
                {
                    GroupTargetWeaponResearch => 3,
                    GroupCUBTarget => 4,
                    GroupThemedEventConstruct or GroupFateThemedConstruct => 5,
                    _ => 0
                };

            return 0;
        }

        private static int GetSwitchDrawIdCount(long playerId, int groupId)
        {
            lock (stateLock)
            {
                return switchCountByPlayerGroup.TryGetValue(playerId, out Dictionary<int, int>? switchCountByGroup)
                    ? switchCountByGroup.GetValueOrDefault(groupId)
                    : 0;
            }
        }
        private static RewardGoods? DrawCharacterReward(DrawInfoTemplate template, bool forceRare)
        {
            if (forceRare && TryCreateTargetCharacterReward(template, out RewardGoods? targetReward))
                return targetReward;

            int roll = Random.Shared.Next(9860);
            if (roll < 50 && TryCreateTargetCharacterReward(template, out targetReward))
                return targetReward;

            if (roll < 1445)
                return DrawCharacterShardReward(template);

            if (roll < 3656)
                return DrawCharacterShardReward(template);

            if (roll < 6495)
                return DrawMemoryReward();

            if (roll < 7937)
                return DrawOverclockMaterialReward();

            if (roll < 8418)
                return DrawExpMaterialReward();

            return DrawCogBoxReward();
        }

        private static RewardGoods? DrawLegacyCharacterReward(DrawInfoTemplate template, bool forceRare)
        {
            if (forceRare && TryCreateTargetCharacterReward(template, out RewardGoods? targetReward))
                return targetReward;

            double roll = Random.Shared.NextDouble();
            if (roll < 0.015 && TryCreateTargetCharacterReward(template, out targetReward))
                return targetReward;

            if (roll < 0.25)
                return DrawCharacterShardReward(template);

            if (roll < 0.58)
                return DrawMemoryReward();

            return DrawFallbackItemReward();
        }

        private static RewardGoods? DrawEquipReward(DrawInfoTemplate template, bool forceRare)
        {
            if (forceRare && TryCreateTargetEquipReward(template, out RewardGoods? targetReward))
                return targetReward;

            int roll = Random.Shared.Next(10000);
            if (roll < 400 && TryCreateTargetEquipReward(template, out targetReward))
                return targetReward;

            if (roll < 450)
                return DrawRandomWeaponReward(quality: 6, excludeEquipId: template.ResourceIds.GetValueOrDefault(1));

            if (roll < 600)
                return DrawPreviewEquipReward(template) ?? DrawRandomWeaponReward(quality: 5);

            if (roll < 750)
                return DrawRandomWeaponReward(quality: 5, excludeEquipId: template.ResourceIds.GetValueOrDefault(1));

            if (roll < 4090)
                return DrawRandomWeaponReward(quality: 4);

            if (roll < 6880)
                return DrawRandomWeaponReward(quality: 3);
            if (roll < 7815)
                return DrawCogBoxReward();

            if (roll < 8750)
                return DrawOverclockMaterialReward();

            return DrawExpMaterialReward();
        }

        private static bool TryCreateTargetCharacterReward(DrawInfoTemplate template, out RewardGoods? reward)
        {
            reward = null;
            int characterId = template.ResourceIds.GetValueOrDefault(1);
            if (characterId <= 0 || !IsCharacterId(characterId))
                return false;

            reward = CreateRewardGoods(RewardType.Character, characterId, 1, level: 1);
            return true;
        }

        private static bool TryCreateTargetEquipReward(DrawInfoTemplate template, out RewardGoods? reward)
        {
            reward = null;
            int equipId = template.ResourceIds.GetValueOrDefault(1);
            if (equipId <= 0 || !IsEquipId(equipId))
                return false;

            reward = CreateRewardGoods(RewardType.Equip, equipId, 1, level: 1);
            return true;
        }

        private static RewardGoods? DrawCharacterShardReward(DrawInfoTemplate template)
        {
            List<int> shardIds = drawPreviewTables
                .FirstOrDefault(x => x.Id == template.Id)
                ?.GoodsId
                .Select(characterId => charactersTables.FirstOrDefault(character => character.Id == characterId)?.ItemId ?? 0)
                .Where(Inventory.IsValidClientItemId)
                .Distinct()
                .ToList() ?? [];

            if (shardIds.Count == 0)
            {
                int characterId = template.ResourceIds.GetValueOrDefault(1);
                CharacterTable? targetCharacter = charactersTables.FirstOrDefault(x => x.Id == characterId);
                if (targetCharacter is not null && Inventory.IsValidClientItemId(targetCharacter.ItemId))
                    shardIds.Add(targetCharacter.ItemId);
            }

            int shardId = PickRandomId(shardIds);
            if (shardId <= 0)
                return DrawFallbackItemReward();

            int count = Random.Shared.Next(100) switch
            {
                < 10 => 18,
                < 35 => 6,
                _ => 2
            };
            return CreateRewardGoods(RewardType.Item, shardId, count);
        }

        private static RewardGoods? DrawMemoryReward()
        {
            List<EquipTable> memories = equipTables
                .Where(equip => equip.Type == 0
                    && equip.Quality == 4
                    && Character.IsOwnableEquipTemplate(equip)
                    && drawWaferShowIds.Contains(equip.Id))
                .ToList();
            if (memories.Count == 0)
                return DrawFallbackItemReward();

            EquipTable memory = memories[Random.Shared.Next(memories.Count)];
            return CreateRewardGoods(RewardType.Equip, memory.Id, 1, level: 1);
        }

        private static RewardGoods? DrawPreviewEquipReward(DrawInfoTemplate template)
        {
            DrawPreviewTable? preview = drawPreviewTables.FirstOrDefault(x => x.Id == template.Id);
            List<int> previewEquipIds = preview?.GoodsId
                .Where(IsEquipId)
                .ToList() ?? [];
            if (previewEquipIds.Count == 0)
                return null;

            int equipId = previewEquipIds[Random.Shared.Next(previewEquipIds.Count)];
            return CreateRewardGoods(RewardType.Equip, equipId, 1, level: 1);
        }

        private static RewardGoods? DrawRandomWeaponReward(int quality, int excludeEquipId = 0)
        {
            List<EquipTable> weapons = equipTables
                .Where(equip => equip.Type > 0
                    && equip.Quality == quality
                    && equip.Id != excludeEquipId
                    && Character.IsOwnableEquipTemplate(equip))
                .ToList();
            if (weapons.Count == 0)
                return DrawFallbackItemReward();

            EquipTable weapon = weapons[Random.Shared.Next(weapons.Count)];
            return CreateRewardGoods(RewardType.Equip, weapon.Id, 1, level: 1);
        }


        private static RewardGoods? DrawOverclockMaterialReward()
        {
            return DrawItemRewardByIds([40110, 40111, 40112, 40113, 40114, 60001, 60002], fallbackCount: 1);
        }

        private static RewardGoods? DrawExpMaterialReward()
        {
            return DrawItemRewardByIds([30011, 30012, 30013, 30014, 31101, 31102, 31103, 31104, 31201, 31202, 31203, 31204], fallbackCount: 3);
        }

        private static RewardGoods? DrawCogBoxReward()
        {
            return DrawItemReward(item => item.Name.StartsWith("Cog Pack") && item.Quality >= MinDrawItemShowQuality, fallbackCount: 1);
        }

        private static RewardGoods? DrawFallbackItemReward()
        {
            return DrawOverclockMaterialReward() ?? DrawCogBoxReward();
        }

        private static RewardGoods? DrawItemRewardByIds(int[] ids, int fallbackCount)
        {
            HashSet<int> allowedIds = [.. ids];
            return DrawItemReward(item => allowedIds.Contains(item.Id) && item.Quality >= MinDrawItemShowQuality, fallbackCount);
        }

        private static RewardGoods? DrawItemReward(Func<ItemTable, bool> predicate, int fallbackCount)
        {
            List<ItemTable> pool = itemTables
                .Where(predicate)
                .ToList();
            if (pool.Count == 0)
                return null;

            ItemTable item = pool[Random.Shared.Next(pool.Count)];
            int count = item.Id switch
            {
                90014 or 90015 => 1,
                40110 or 40111 or 40112 or 40113 or 40114 or 60001 or 60002 => 1,
                _ => fallbackCount
            };
            return CreateRewardGoods(RewardType.Item, item.Id, count);
        }

        private static int PickRandomId(List<int> ids)
        {
            return ids.Count == 0 ? 0 : ids[Random.Shared.Next(ids.Count)];
        }

        private static int GetFirstQuality(int characterId)
        {
            return characterQualitiesTables
                .Where(x => x.CharacterId == characterId)
                .OrderBy(x => x.Quality)
                .FirstOrDefault()?.Quality ?? 0;
        }

        private static RewardGoods CreateRewardGoods(RewardType type, int templateId, int count, int level = 0, int quality = 0)
        {
            return new RewardGoods
            {
                RewardType = (int)type,
                TemplateId = templateId,
                Count = count,
                Level = level,
                Quality = quality,
                IsGift = false,
                RewardMulti = 0
            };
        }


        private static bool IsCharacterId(int templateId)
        {
            return charactersTables.Any(x => x.Id == templateId);
        }

        private static bool IsEquipId(int templateId)
        {
            return equipTables.Any(x => x.Id == templateId && Character.IsOwnableEquipTemplate(x));
        }
    }
}
