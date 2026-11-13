// 显示和隐藏（按类别一键全显 / 全隐）
//
// 移植自 caxa3dtracker\ShowHideTool.cs（2026-11-13），只保留「隐藏所有类型 / 显示所有类型」两条，
// 元素级显隐（HideSelected / ShowAll / ShowSelected）不移植。
//
// ★ 通道定案（caxa3dtracker 实测）★
//   走「文档级类别标志」`IZSceneDocProperty.SetShowFlag(eSHOWOption)`，而不是遍历树逐元素
//   `IZElement.SetDisplayFlag`：注释/约束/标注/坐标系/基准面等类别不在 eZElementType(0~51) 里，
//   逐元素遍历覆盖不全；类别标志才是 CAXA 右键「显示和隐藏」菜单的真正语义，
//   且 `IsShowFlagOn` 可回读验证，不靠盲判据。
//
// ★ 判据 ★ 每类「原态 → 设定 → 回读」三态写 trace，只有回读一致才计生效；
//          未生效的类别原样点名，不宣称全部成功。
using System;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal static class ShowHideTool
    {
        private const string Tag = "showhide";

        /// <summary>
        /// 截图菜单「显示和隐藏」的 10 个类别，逐一映射到 eSHOWOption 全局标志。
        /// 顺序与 caxa3dtracker 一致，便于两工程 trace 对照。
        /// </summary>
        private static readonly (string Label, int Flag)[] CategoryFlags = new (string, int)[]
        {
            ("注释",     26),  // Z_SHOW_ANNOTATIONS
            ("阵列",     25),  // Z_SHOW_PATTERNS
            ("位置尺寸",  9),  // Z_SHOW_DIMENSIONS
            ("三维曲线", 33),  // Z_SHOW_3D_WIRE
            ("附着点",   11),  // Z_SHOW_ATTACHMENT_PTS
            ("约束",     20),  // Z_SHOW_RELATIONS
            ("智能标注",  7),  // Z_SHOW_SMARTDIMENSIONS
            ("坐标系",   27),  // Z_SHOW_WORLD_AXES
            ("二维草图", 34),  // Z_SHOW_2D_SKETCH
            ("基准面",   32),  // Z_SHOW_DATUMPLANE
        };

        /// <summary>隐藏所有类型：把 10 个类别标志全部置为「不显示」。</summary>
        public static string HideAllTypes(IZBaseApp app)
        {
            return SetAllCategoryFlags(app, false);
        }

        /// <summary>显示所有类型：把 10 个类别标志全部置为「显示」。</summary>
        public static string ShowAllTypes(IZBaseApp app)
        {
            return SetAllCategoryFlags(app, true);
        }

        private static string SetAllCategoryFlags(IZBaseApp app, bool show)
        {
            string verb = show ? "显示" : "隐藏";
            Action<string> log = m => Diagnostics.Log(m);
            log(Tag + ": ==== " + verb + "所有类型（IZSceneDocProperty.SetShowFlag × " + CategoryFlags.Length + "）====");

            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null) { log(Tag + ": " + err); return err; }

            IZSceneDocProperty prop = null;
            try { prop = scene as IZSceneDocProperty; }   // 默认接口已实现，QI 即得
            catch (Exception ex) { log(Tag + ": QI(IZSceneDocProperty) FAIL " + ProbeCommon.Hr(ex)); }
            if (prop == null) return "场景文档不支持 IZSceneDocProperty（第三态：类别显示开关读不到）";

            int ok = 0, fail = 0;
            foreach (var c in CategoryFlags)
            {
                eSHOWOption flag = (eSHOWOption)c.Flag;
                bool before = false, readBefore = false;
                try { prop.IsShowFlagOn(flag, out before); readBefore = true; }
                catch (Exception ex) { log(Tag + ": IsShowFlagOn(" + c.Label + ") FAIL " + ProbeCommon.Hr(ex)); }

                try
                {
                    prop.SetShowFlag(flag, show);
                    bool after = false;
                    prop.IsShowFlagOn(flag, out after);
                    bool consistent = (after == show);
                    log(string.Format("{0}: [{1}] 原={2} → 设{3} → 回读={4}{5}",
                        Tag, c.Label,
                        readBefore ? (before ? "显" : "隐") : "<读不到>",
                        show ? "显" : "隐",
                        after ? "显" : "隐",
                        consistent ? "" : "（⚠ 未生效）"));
                    if (consistent) ok++; else fail++;
                }
                catch (Exception ex)
                {
                    fail++;
                    log(Tag + ": [" + c.Label + "] SetShowFlag FAIL " + ProbeCommon.Hr(ex));
                }
            }

            try { scene.Update(); }
            catch (Exception ex) { log(Tag + ": scene.Update FAIL " + ProbeCommon.Hr(ex)); }

            string result = string.Format("{0}所有类型：生效 {1} / 未生效 {2}（共 {3} 类）",
                verb, ok, fail, CategoryFlags.Length);
            log(Tag + ": " + result);
            if (fail > 0) result += "（未生效类别见 trace，不宣称全部成功）";
            return result;
        }
    }
}
