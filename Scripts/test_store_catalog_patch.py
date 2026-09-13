"""Behavioral Lua tests; set PGR_RESEARCH_LUA to the exported matrix Lua directory
to additionally validate the real client call sites and timer transitions.
Requires lupa, UnityPy and msgpack.
"""
import os
from pathlib import Path
import unittest

from lupa.lua53 import LuaRuntime
from Scripts.patch_local_store import CATALOG_CALLERS, patch_catalog_lua, patch_recharge_lua


class PurchaseIconTests(unittest.TestCase):
    def setUp(self):
        self.lua = LuaRuntime(unpack_returned_tuples=True)
        self.lua.execute('''
            XPurchaseConfigs = {}
            TestIcons = {
                current = {AssetPath = "art", CoverImgPath = "cover"},
                missing = {AssetPath = "old-art", CoverImgPath = "old-cover"},
                coverOnly = {AssetPath = "old-art", CoverImgPath = "cover"}
            }
            Available = {art=true, cover=true, reward=true, daily=true, signin=true}
            GoodsIcons = {[1]="missing-reward", [2]="reward", [3]="daily", [4]="signin"}
            CS = {XResourceManager = {GetBundleUrl = function(path)
                return Available[path] and "bundle" or nil
            end}}
            XGoodsCommonManager = {GetGoodsIcon = function(id) return GoodsIcons[id] end}
            XRewardManager = {GetRewardList = function(id) return {{TemplateId=4}} end}
        ''')
        source = Path(__file__).with_name('store_catalog_icons.lua').read_text(encoding='utf-8')
        self.lua.execute('local PurchaseIconAssetPathConfig = TestIcons\n' + source)

    def test_valid_original_and_cover_preserved(self):
        self.lua.execute('''
            local result = XPurchaseConfigs.GetIconPathByIconName("current", {RewardGoodsList={{TemplateId=2}}})
            assert(result.AssetPath == "art" and result.CoverImgPath == "cover")
        ''')

    def test_missing_art_uses_first_available_reward_without_mutating_table(self):
        self.lua.execute('''
            local result = XPurchaseConfigs.GetIconPathByIconName("missing", {
                RewardGoodsList={{TemplateId=1}, {TemplateId=2}}})
            assert(result.AssetPath == "reward" and result.CoverImgPath == nil)
            assert(TestIcons.missing.AssetPath == "old-art")
            assert(XPurchaseConfigs.GetIconPathByIconName("missing").AssetPath == "old-art")
        ''')

    def test_rewards_are_specific_to_each_package(self):
        self.lua.execute('''
            local first = XPurchaseConfigs.GetIconPathByIconName("missing", {RewardGoodsList={{TemplateId=2}}})
            local second = XPurchaseConfigs.GetIconPathByIconName("missing", {RewardGoodsList={{TemplateId=3}}})
            assert(first.AssetPath == "reward" and second.AssetPath == "daily")
        ''')

    def test_daily_and_signin_only_packages(self):
        self.lua.execute('''
            local daily = XPurchaseConfigs.GetIconPathByIconName("missing", {DailyRewardGoodsList={{TemplateId=3}}})
            assert(daily.AssetPath == "daily")
            local signin = XPurchaseConfigs.GetIconPathByIconName("missing", {
                PurchaseSignInInfo={PurchaseSignInRewardInfos={10}}})
            assert(signin.AssetPath == "signin")
        ''')

    def test_available_cover_survives_missing_base(self):
        self.lua.execute('''
            local result = XPurchaseConfigs.GetIconPathByIconName("coverOnly", {RewardGoodsList={{TemplateId=2}}})
            assert(result.AssetPath == "reward" and result.CoverImgPath == "cover")
        ''')

    def test_no_valid_resource_does_not_invent_an_icon(self):
        self.lua.execute('assert(XPurchaseConfigs.GetIconPathByIconName("missing", {RewardGoodsList={}}) == nil)')


class PatchTests(unittest.TestCase):
    def test_existing_recharge_patch_is_preserved(self):
        original = '            DoPay(productKey, res.GameOrder, template.GoodsId)'
        patched = patch_recharge_lua(original)
        self.assertEqual(patched, patch_recharge_lua(patched))

    def test_changed_resolver_is_rejected(self):
        with self.assertRaises(RuntimeError):
            patch_catalog_lua('XPurchaseConfigs.lua', 'function changed() end')

    def source_dir(self):
        path = os.environ.get('PGR_RESEARCH_LUA')
        if not path:
            self.skipTest('Set PGR_RESEARCH_LUA for installed client source verification')
        return Path(path)

    def test_real_client_scripts_compile_and_all_call_sites_patch(self):
        source_dir = self.source_dir()
        lua = LuaRuntime(unpack_returned_tuples=True)
        compile_lua = lua.eval('function(source) assert(load(source)) end')
        for name in ['XPurchaseConfigs.lua', *CATALOG_CALLERS]:
            with self.subTest(name=name):
                original = (source_dir / name).read_text(encoding='utf-8')
                for script in [original, original.replace('\n', '\r\n')]:
                    patched = patch_catalog_lua(name, script)
                    self.assertNotEqual(script, patched)
                    compile_lua(patched)

    def test_real_timer_expiry_removes_owned_overlay(self):
        source_dir = self.source_dir()
        for name in ['XUiPurchaseLBListItem.lua', 'XUiPurchaseCoatingLBListItem.lua']:
            with self.subTest(name=name):
                lua = LuaRuntime(unpack_returned_tuples=True)
                lua.execute('''
                    XClass = function() return {} end
                    CS = {XTextManager={GetText=function(key) return key end}}
                    XUiHelper = {GetTime=function(t) return tostring(t) end, TimeFormatType={PURCHASELB=1}}
                    XOverseaManager = {IsKRRegion=function() return false end}
                    function Active(value)
                        return {gameObject={active=value, SetActive=function(self, flag) self.active=flag end}}
                    end
                    Card = {RemainTime=2, UpdateTimerType=1,
                        Parent={RemoveTimerFun=function() end}, ItemData={Id=1, BuyTimes=0, BuyLimitTimes=1},
                        ImgSellout=Active(false), ImgHave=Active(true), TxtUnShelveTime={}, TxtSetOut={},
                        ActiveImgTimeBg=function() end}
                ''')
                lua.globals().CardClass = lua.execute(patch_catalog_lua(name, (source_dir / name).read_text(encoding='utf-8')))
                lua.execute('''
                    CardClass.UpdateTimer(Card, false, 1)
                    assert(Card.ImgHave.gameObject.active and not Card.ImgSellout.gameObject.active)
                    CardClass.UpdateTimer(Card, false, 1)
                    assert(not Card.ImgHave.gameObject.active and Card.ImgSellout.gameObject.active)
                ''')


if __name__ == '__main__':
    unittest.main()
