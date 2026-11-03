using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 「改变链接文件」探针（v6）：把图纸环境那张列出全部外链 ics 的对话框
    /// （标题「改变链接文件」，提示「双击单元格进行编辑」，两列 文件名 · 在文件夹，
    /// 按钮 确定(O) / 取消(C) / 替换(R)）自动化成**每行四步 + 一次提交**：
    ///   ① 试探：双击「文件名」只为**读**这一行现在的值和它的目录，读完由通用代点取消「打开」
    ///   ② 「在文件夹」列 → 目标文件夹（外链件＝外链 ics 自己所在目录；虚拟件＝设计环境文档所在目录）
    ///   ③ 「文件名」列   → 零件名.ics（走「打开」时填的是**全路径**，所以两列一次到位）
    ///   ④ 再读一次「打开」的查找范围，作为「在文件夹」真改上了没的**唯一证据**
    ///   ⑤ 「确定(O)」提交，CAXA 接着问「请更新3D文件位置」由 PopupAutoYes 点「是」
    /// 不需要遍历视图：对话框一行就是一个链接文件，同一 ics 被多视图引用只占一行。
    ///
    /// ★ 为什么必须先试探再动手（v5 的隐患）★
    ///   v5 一上来就改「在文件夹」，而这一列**每一行都会走到**（网格投双击不区分是谁的链接）。
    ///   测试图纸只有本件的链接所以看不出来；一张链接了五个件的装配图跑 v5，五行的目录会被
    ///   全刷成我们的目录，「文件名」再按 SameFile 判断，四行也会被填成同一个 ics —— 整张图的链接
    ///   全指向一个件。v6 用试探结果认行：文件名对不上号的行，两列都不碰。
    ///
    /// ★ 「浏览文件夹」的「确定」为什么点不动（实机 11-02 09:11）★
    ///   09:10 / 09:11 两轮：双击 (280,29) 「浏览文件夹」**弹出来了**，「文件夹(F)」也写进去了，
    ///   但 `「确定」点不动（HWND命中=False MSAA命中=True）` ×8 —— HWND 没命中的原因是
    ///   我们拿 "确定(o)" 当指纹去子树里找，而这张框的按钮文字就是「确定」（无助记符），
    ///   MSAA 那条命中了却按不动。v5 改成：先按「确定」找真 Button 走 BM_CLICK，
    ///   再兜一层 `WM_COMMAND / IDOK(1) + BN_CLICKED` 直接给对话框发命令；
    ///   点完**必须等这张框真的消失**才走下一步 —— 09:11 就是让它盖在网格上继续投双击，
    ///   结果全打在栈顶的框上，CRX 记 `count 11->11 +0 -0`。09:49/09:58/10:02 三轮
    ///   `BM_CLICK「确定」id=1 class=Button ok=True` 已证实这条通道通了。
    ///
    /// ★ 「替换(R)」这条退路已删（实机 09:10:13 / 09:11:08，用户 4.PNG 佐证）★
    ///   BM_CLICK 它 ok=True，但弹出来的是**查找替换**框（查找内容=旧 ics 名），
    ///   紧接着通用代点在「CAXA 3D 实体设计2027」上点了「确定」—— 等于凭空替用户做一次全文替换。
    ///   它不是"把选中的行换成别的文件"那个按钮，不能用。
    ///
    /// ★ 只碰该碰的那一行 ★
    ///   「打开」框里带着**这一行现在的值**（文件名编辑框只有裸名，目录看「查找范围」下拉框），
    ///   先读出来判身份：名字去掉扩展名和尾部的数字后与目标同名才算本件（改名试错留下的
    ///   `…内牙接头444444` 就是这种）；对不上号或读不到现值 ⇒ 本行不动，让通用代点把「打开」取消掉。
    ///
    /// ★ 状态机跑在 PopupAutoYes 的后台线程上（实机 11-02 08:00 定案）★
    ///   对话框是**模态**的，它的嵌套消息泵接管 CAXA 主线程，主线程里任何 WaitUntil 都醒不过来。
    ///   ⇒ 主线程只投命令、等 CRX 报 `commandEnded | CX_DFT_ChangeLinks`（=对话框关掉的那一刻）。
    ///
    /// ★ 单元格通道：往网格 HWND 投合成双击（实机 09:10 已证实可用）★
    ///   MSAA 侧 `OBJID_CLIENT childCount=5` 只有四个按钮 + 一句提示，网格下面只有滚动条
    ///   ⇒ 一个单元格都问不到；但 `MFCGridCtrlEx`（id=7158）是有 HWND 的子窗口，
    ///   PostMessage 的 WM_LBUTTONDBLCLK 它**吃**：(280,29)→「浏览文件夹」、(80,29)→「打开」
    ///   都真实弹出来了。行是否存在由"有没有弹出子框"反推，行到头就停。
    ///
    /// ★ 每一步只发起一次，超时即跳过并记录；一行都没改到就**绝不点确定** ★
    ///   后台线程每 120 ms 回来问一次，任何"没反应就再点一次"的写法都会变成无限连点。
    ///   改过（文件名或文件夹都算）才点「确定(O)」；一行没改但每行本来都对 ⇒ 点「取消(C)」干净收尾；
    ///   其余（有对不上号的行、读不到现值）⇒ HandBack 交还，把对话框留在原地并护住手工用的子框。
    /// </summary>
    [ComVisible(false)]
    internal static class ChangeLinksProbe
    {
        // ------------------------------------------------------------ 对外：主线程

        /// <summary>探针是否在跑（只有跑的时候才接管「浏览文件夹」，平时留给通用代点）。</summary>
        public static bool IsArmed { get { return _armed; } }

        /// <summary>
        /// 逐行日志的去向（11-02 用户口径：不再是一段独立「探针 log」，直接并进主程序日志流）。
        /// MainForm 注入的是**异步**投递（BeginInvoke）：本状态机跑在代点线程上，而这段时间主线程
        /// 常在 CAXA 的嵌套模态泵里；同步 Invoke 一旦碰上不泵消息的原生阻塞，代点线程会跟着卡死，
        /// 对话框没人填、主线程白等 600 秒。BeginInvoke 不阻塞调用方，最多丢一行。
        /// </summary>
        public static Action<string> Log;

        /// <summary>主线程投命令前调用，把目标路径交给后台线程。</summary>
        public static void Begin(string targetIcs, string targetFolder)
        {
            lock (_sync)
            {
                _ics = targetIcs;
                _folder = targetFolder ?? "";
                _fileBase = Path.GetFileName(targetIcs);
                _report.Clear();
                _rows.Clear();
                _dumped = false;
                _row = 0;
                _rowsDone = 0;
                _rowsSkipped = 0;
                _already = 0;
                _foreign = 0;
                _folderFixed = 0;
                _viaGrid = false;
                _handedBack = false;
                _stage = StagePlan;
                _stageSince = Environment.TickCount;
                _stepStarted = false;
                PopupAutoYes.FileDialogTarget = null;   // 上一轮残留会把无关「打开」框填成我们的路径
                _msaaBlind = false;
                _dirBlindFor = IntPtr.Zero;
                _dirChannelDead = false;
                _armed = true;
            }
        }

        /// <summary>主线程等完 commandEnded 后调用：解除武装并取回反馈行。</summary>
        public static List<string> End()
        {
            lock (_sync)
            {
                _armed = false;
                _stage = StageDone;
                _handedBack = false;
                Log = null;          // 解除武装同时断掉实时转发，避免残留 sink 把后来的行写进已关闭的窗体
                PopupAutoYes.FileDialogTarget = null;
                var copy = new List<string>(_report);
                _report.Clear();     // 交回即清空：RunLongFlow 的 finally 还会再 End() 一次，别把同一段话播两遍
                return copy;
            }
        }

        // ------------------------------------------------------------ 对外：PopupAutoYes

        public static bool IsMine(string title)
        {
            return !string.IsNullOrEmpty(title) &&
                   title.IndexOf(TitleFrag, StringComparison.Ordinal) >= 0;
        }

        /// <summary>「浏览文件夹」是改「在文件夹」列弹出来的，必须先填路径再点确定。</summary>
        public static bool IsFolderBrowse(string title)
        {
            return !string.IsNullOrEmpty(title) &&
                   title.IndexOf(FolderTitle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 交还用户期间为 true：探针不再动手，但 PopupAutoYes 必须把「打开」「浏览文件夹」
        /// 从通用代点手里护下来 —— 它在 FileDialogTarget 为空时会给文件框点「取消」，
        /// 那正好把用户手工改链接要用的框关掉（实机 08:00 的 `取消无关文件框「打开」`×3 就是这条）。
        /// </summary>
        public static bool HandedBack { get { return _handedBack; } }

        /// <summary>标准文件框标题（用户手工改「文件名」列时弹的就是它）。</summary>
        public static bool IsFileDialog(string title)
        {
            return !string.IsNullOrEmpty(title) && UiWin.Normalize(title) == "打开";
        }

        /// <summary>对话框句柄（0 = 不在）。图纸环境的框可能在 CAXADraft 进程里。</summary>
        public static IntPtr Dialog()
        {
            return UiWin.FindTopByTitle(UiWin.Normalize(TitleFrag));
        }

        /// <summary>「改变链接文件」对话框被扫到（通用代点已让位，不会替我们把「确定」点掉）。</summary>
        public static void Tick(IntPtr dlg)
        {
            if (!_armed) return;
            try { lock (_sync) { if (_armed) Step(dlg); } }
            catch (Exception ex) { Say("更新链接内部异常：" + ex.Message); _stage = StageDone; _armed = false; }
        }

        /// <summary>「浏览文件夹」被扫到：往「文件夹(F)」编辑框填目标目录再点「确定」。</summary>
        public static void TickFolderBrowse(IntPtr dlg)
        {
            if (!_armed) return;
            try
            {
                lock (_sync)
                {
                    if (!_armed || _stage != StageFolder) return;
                    if (_folderHandled) return;         // 一次实例只处理一次
                    _folderHandled = true;
                    _folderSince = Environment.TickCount;
                    FillFolderEdit(dlg);
                    if (AcceptFolder(dlg))
                    {
                        PopupAutoYes.CountExternalClick();
                        _folderFixed++;
                        Say("第 " + (_row + 1) + " 行「在文件夹」已填 " + _folder + " 并点「确定」");
                    }
                    else Say("警告：「浏览文件夹」的「确定」按不动，文件夹已填在框里，请手工点一下");
                }
            }
            catch (Exception ex) { Say("浏览文件夹处理异常：" + ex.Message); }
        }

        // ------------------------------------------------------------ 常量与指纹

        private const string TitleFrag = "改变链接文件";
        private const string FolderTitle = "浏览文件夹";
        private const string OpenTitle = "打开";
        private const string AcceptFrag = "确定(o)";
        private const string AcceptPlain = "确定";
        private const string CancelFrag = "取消(c)";

        private static readonly string[] AcceptNeedles = { "确定(o)", "确定" };
        private static readonly string[] FolderNeedles = { "确定" };
        private static readonly string[] CancelNeedles = { "取消(c)", "取消" };

        private const int StagePlan = 1, StagePeek = 2, StageFolder = 3, StageFile = 4,
                          StageCommit = 5, StageDone = 6;

        // 试探（StagePeek）读完后对本行的判定
        private const int PeekNone = -1, PeekNotOurs = 0, PeekAlready = 1,
                          PeekFolderOnly = 2, PeekChange = 3;

        // 「在文件夹」这一列的三态：实机 v6 两轮都读不到「打开」的查找范围，
        // 把"读不到"当"不对"会让本来正确的行白白重填一遍文件夹 + 白走一次验证性「打开」。
        private const int DirUnknown = -1, DirNo = 0, DirYes = 1;

        /// <summary>行数上限：超出说明命中口径有问题，别再转圈。</summary>
        private const int MaxRows = 8;

        // 网格客户区几何：量自用户截图 改变链接文件\1.PNG（该图 543x306 与取证窗口矩形逐像素对齐），
        // 网格窗口相对对话框 (23,85)、客户区 498x153（实机 09:11 读到 494x149）。
        // 表头占 3~19，第 0 行 21~37（中心 29），行高 18 —— 09:11 实机每行都命中，标定成立。
        private const int RowZeroCenter = 29;
        private const int RowStep = 18;
        private const int XFile = 80;
        private const int XFolder = 280;

        /// <summary>
        /// 等子框（「打开」/「浏览文件夹」）出现的上限。实机双击后 195~230 ms 必现，后台线程 120 ms 一跳；
        /// 原来写 6000 是没数据时的保守值，代价全落在"不存在的最后一行"上：10:36 那轮 8.8 秒的
        /// 对话框阶段里 5.9 秒是白等的（67%）。3 秒仍是实测的 13 倍余量。
        /// </summary>
        private const int SubDialogMs = 3000;

        // ------------------------------------------------------------ 状态

        private static readonly object _sync = new object();
        private static volatile bool _armed;
        private static int _stage, _stageSince, _row, _rowsDone, _rowsSkipped;
        private static int _already, _foreign, _folderFixed;   // 跳过原因分开计数
        private static bool _stepStarted, _dumped, _viaGrid, _handedBack;
        private static bool _folderHandled;
        private static int _folderSince;
        private static bool _read;                 // 本行的「打开」现值已读过
        private static bool _reject;               // 本行不该动，等「打开」被取消
        private static int _peek;                  // 本行试探后的判定（Peek* 常量）
        private static int _peekDir;               // 试探读到的目录三态（Dir* 常量）
        private static bool _didFolder;            // 本行真的填过「浏览文件夹」并等它关闭
        private static bool _verifyOnly;           // 只验文件夹、不改文件名
        private static bool _msaaBlind;            // MSAA 侧一个单元格都读不到（本版实测如此），别再走全树
        private static IntPtr _dirBlindFor;        // 这张「打开」已用无障碍扫过、仍读不到查找范围
        private static bool _dirChannelDead;       // 四条通道全空过一次：本版的「查找范围」就是取不到，别再为验证开框
        private static string _dirHow = "";        // 目录是从哪条通道读到的，只写进日志不参与判定
        private static int _fileClicks;
        private static string _ics = "", _folder = "", _fileBase = "";
        private static readonly List<string> _report = new List<string>();
        private static readonly List<string> _rows = new List<string>();

        private static void Step(IntPtr dlg)
        {
            switch (_stage)
            {
                case StagePlan:
                    if (!_dumped)
                    {
                        _dumped = true;
                        UiWin.DumpWindow(dlg, "改变链接文件对话框（后台线程取证）");
                        ReadRows(dlg);
                        Say("目标：在文件夹=" + _folder + " 文件名=" + _fileBase);
                        if (_rows.Count == 0)
                        {
                            _viaGrid = true;
                            IntPtr g = Grid(dlg);
                            RECT gr;
                            string geo = g != IntPtr.Zero && GetClientRect(g, out gr)
                                ? gr.Right + "x" + gr.Bottom : "未知";
                            if (g == IntPtr.Zero)
                            {
                                Say("MSAA 读不到行、又找不到 MFCGridCtrlEx 网格子窗口 —— 本工具不动，" +
                                    "对话框留在原地交给你");
                                HandBack(dlg);
                                return;
                            }
                            Say("MSAA 读不到单元格（网格只暴露滚动条），走网格坐标通道；" +
                                "网格客户区=" + geo + "（量测基准 498x153，行心 y=29+18n）");
                        }
                    }
                    BeginRow();
                    break;

                case StagePeek:
                    // 动手前先**试探**：双击「文件名」只为读这一行现在的值（现值 + 查找范围），
                    // 读完由通用代点把「打开」取消（此时 FileDialogTarget 为空）。
                    // 不先认行就直接改「在文件夹」，会把这张图纸里别的件的链接也拉进我们的目录。
                    if (!_stepStarted)
                    {
                        _stepStarted = true;
                        _peek = PeekNone;
                        EditCell(dlg, true, "文件名");
                        break;
                    }
                    if (_peek == PeekNone)
                    {
                        IntPtr ofd = OpenDialog();
                        if (ofd == IntPtr.Zero)
                        {
                            // 双击投下去到不了子框 = 这一行（以及它后面所有行）不存在
                            if (Expired(SubDialogMs)) EndOfRows(dlg);
                            break;
                        }
                        Judge(OpenBoxText(ofd), OpenBoxDir(ofd));
                        break;
                    }
                    if (OpenDialog() != IntPtr.Zero)
                    {
                        if (Expired(8000))
                        {
                            Say("第 " + (_row + 1) + " 行试探用的「打开」还堵着，本工具收手，交还给你");
                            HandBack(dlg);
                        }
                        break;
                    }
                    switch (_peek)
                    {
                        case PeekNotOurs:
                            _rowsSkipped++; _foreign++; NextRow(dlg); break;
                        case PeekAlready:
                            _rowsSkipped++; _already++; NextRow(dlg); break;
                        case PeekChange:
                            Advance(_peekDir == DirYes ? StageFile : StageFolder);
                            break;
                        default:                        // PeekFolderOnly
                            Advance(StageFolder); break;
                    }
                    break;

                case StageFolder:
                    if (!_stepStarted)
                    {
                        _stepStarted = true;
                        _folderHandled = false;
                        EditCell(dlg, false, "在文件夹");
                        break;
                    }
                    if (!_folderHandled)
                    {
                        // 到不了「浏览文件夹」⇒ 这一列点不开（或行已到头），
                        // 交给「文件名」的全路径一次补齐两列。
                        if (Expired(SubDialogMs))
                        {
                            Say("第 " + (_row + 1) + " 行「在文件夹」没弹出「浏览文件夹」，" +
                                "改由「文件名」的全路径一次补齐两列");
                            Advance(StageFile);
                        }
                        break;
                    }
                    // 填完必须等这张框真的消失，否则后面的双击全打在它身上（09:11 的教训）
                    if (FolderDialog() == IntPtr.Zero) { _didFolder = true; Advance(StageFile); break; }
                    if (Environment.TickCount - _folderSince > 5000)
                    {
                        Say("「浏览文件夹」还开着（文件夹已填好）—— 本工具不往上叠操作，" +
                            "请手工点它的「确定」，或点「取消」后由「文件名」的全路径补齐");
                        HandBack(dlg);
                    }
                    break;

                case StageFile:
                    if (!_stepStarted)
                    {
                        _stepStarted = true;
                        _read = false;
                        _reject = false;
                        _verifyOnly = _peek == PeekFolderOnly;   // 文件名已经对，只借「打开」验文件夹
                        if (_verifyOnly && _dirChannelDead)
                        {
                            // 这趟「打开」唯一的用途是读「查找范围」当证据，而该通道本轮已证明取不到值：
                            // 文件夹早在「浏览文件夹」里填好并点过「确定」了，再开一张框只能多编一句假警告。
                            Say("第 " + (_row + 1) + " 行「在文件夹」已补；目录列读不到、无法验证 —— " +
                                "跳过只为验证而开的「打开」，直接下一行");
                            NextRow(dlg);
                            break;
                        }
                        _fileClicks = PopupAutoYes.ClickCount;
                        EditCell(dlg, true, "文件名");
                        break;
                    }
                    if (!_read)
                    {
                        IntPtr ofd = OpenDialog();
                        if (ofd == IntPtr.Zero)
                        {
                            // 双击投下去 6 秒没「打开」= 这一行（以及它后面所有行）不存在
                            if (Expired(6000)) EndOfRows(dlg);
                            break;
                        }
                        _read = true;
                        string cur = OpenBoxText(ofd);
                        string dir = OpenBoxDir(ofd);      // 「查找范围」= 「在文件夹」列改完的真实结果
                        Say("第 " + (_row + 1) + " 行" + (_didFolder ? "补文件夹后" : "文件名这一步") +
                            "现值=" + (cur.Length == 0 ? "（读不到）" : cur) +
                            "　所在文件夹=" + DescribeDir(dir));
                        if (_didFolder)
                        {
                            // 三态各说各话：读不到时不能再喊「那一列没改上」——
                            // 实机 10:36/10:38 那句警告全是这么来的假警报。
                            int ds = DirState(dir);
                            Say(ds == DirYes
                                ? "    「在文件夹」已改到 " + _folder + "（证据：这张「打开」的查找范围就是它）"
                                : ds == DirNo
                                    ? "    警告：填过「文件夹(F)」也点了「确定」，「打开」的查找范围却还是 " +
                                      dir + " —— 那一列没改上"
                                    : "    「在文件夹」填了也点了「确定」，但「打开」的查找范围读不到 —— " +
                                      "这一列改没改上无法从界面确认；文件名走的是全路径，链接本身按全路径生效");
                        }
                        if (_verifyOnly)
                        {
                            _reject = true;
                            break;
                        }
                        PopupAutoYes.FileDialogTarget = _ics;   // 下一轮由通用代点填 + 点「打开」
                        break;
                    }
                    if (_reject)
                    {
                        if (OpenDialog() == IntPtr.Zero) { NextRow(dlg); break; }
                        if (Expired(8000))
                        {
                            Say("第 " + (_row + 1) + " 行的「打开」还堵着，本工具收手，交还给你");
                            HandBack(dlg);
                        }
                        break;
                    }
                    if (PopupAutoYes.ClickCount > _fileClicks)
                    {
                        PopupAutoYes.FileDialogTarget = null;   // 用完就清，别去填别人的「打开」
                        _rowsDone++;
                        Say("第 " + (_row + 1) + " 行「文件名」已填 " + _ics + " 并点「打开」");
                        NextRow(dlg);
                        break;
                    }
                    if (Expired(12000))
                    {
                        Say("第 " + (_row + 1) + " 行的「打开」填了却没点掉，本工具收手");
                        HandBack(dlg);
                    }
                    break;

                case StageCommit:
                    if (!_stepStarted)
                    {
                        _stepStarted = true;
                        PopupAutoYes.FileDialogTarget = null;
                        if (_rowsDone == 0 && _folderFixed == 0) { HandBack(dlg); break; }
                        Say("共改完 " + _rowsDone + " 行文件名、补好 " + _folderFixed +
                            " 行文件夹（跳过 " + _rowsSkipped + " 行），点「确定(O)」提交");
                        if (!ClickButton(dlg, AcceptFrag, AcceptNeedles, "确定(O)"))
                            Say("警告：点不到「确定(O)」，请手工点一次");
                        break;
                    }
                    if (Dialog() == IntPtr.Zero) { Say("对话框已关闭，提交完成"); _stage = StageDone; }
                    else if (Expired(15000)) { Say("点了「确定」但 15 秒后对话框还在"); _stage = StageDone; }
                    break;
            }
        }

        /// <summary>双击「文件名」后没有「打开」＝ 行已到头；改过行就提交，一行没改就交还。</summary>
        private static void EndOfRows(IntPtr dlg)
        {
            Say("第 " + (_row + 1) + " 行没弹出「打开」，行已走完");
            if (_rowsDone > 0 || _folderFixed > 0)
            {
                Advance(StageCommit);
            }
            else if (_already > 0 && _foreign == 0)
            {
                // 每一行读出来就已经指向目标文件 ⇒ 这张图纸本来就没问题：
                // 点「取消(C)」干净收尾（不提交＝不改任何数据），别把对话框留在原地让人猜。
                Say(_already + " 行本来就指向 " + _fileBase + " —— **无需改链**，点「取消(C)」收尾");
                if (ClickButton(dlg, CancelFrag, CancelNeedles, "取消(C)"))
                {
                    PopupAutoYes.CountExternalClick();
                    _stage = StageDone;
                }
                else HandBack(dlg);
            }
            else if (_rowsSkipped > 0)
            {
                Say("跳过 " + _rowsSkipped + " 行（别的件的链接 " + _foreign +
                    " 行 / 已经是目标 " + _already + " 行）—— 一行都没改，不点「确定」");
                HandBack(dlg);
            }
            else HandBack(dlg);
        }

        /// <summary>一行都没改到：不再替用户点确定，把界面原样交还。</summary>
        private static void HandBack(IntPtr dlg)
        {
            Say("一行都没改到，**没有**替你点「确定」：对话框留在原地，" +
                "请手工把两列改好后自己点「确定」（这段时间不会再碰你的「打开」「浏览文件夹」）");
            PopupAutoYes.FileDialogTarget = null;
            _stage = StageDone;
            _handedBack = true;    // 护住用户手工用的「打开」「浏览文件夹」
            _armed = false;        // 交还：探针不再动手，「改变链接文件」仍被分流挡住
        }

        private static void Advance(int stage)
        {
            _stage = stage;
            _stageSince = Environment.TickCount;
            _stepStarted = false;
        }

        private static bool Expired(int ms)
        {
            return Environment.TickCount - _stageSince > ms;
        }

        private static void BeginRow()
        {
            if (_row >= MaxRows) { Say("已达行数上限 " + MaxRows + "，停止逐行"); Advance(StageCommit); return; }
            if (!_viaGrid && _row >= _rows.Count) { Advance(StageCommit); return; }
            _didFolder = false;
            _peek = PeekNone;
            Advance(StagePeek);
        }

        private static void NextRow(IntPtr dlg)
        {
            _row++;
            BeginRow();
        }

        // ------------------------------------------------------------ 本行该不该动

        /// <summary>
        /// 试探判定，四种结果各走各路（**先认行、后动手**，v6）：
        ///   对不上号 → 别的件的链接，一行都不碰；
        ///   名字和目录都对 → 已经是目标；
        ///   名字对、目录不对 → 只补「在文件夹」，不碰文件名；
        ///   名字不对但同源（改名试错留下的 444444/6666 那种尾巴）→ 目录也补、文件名也改。
        /// 认"是不是本件"只看**文件名**，不能拿目录当判据 —— 目录正是要改的那一列，
        /// 改前先比目录会把该修的行判成"别人的行"；只看目录又会把同目录里别的件一起改掉。
        /// </summary>
        private static void Judge(string cur, string dir)
        {
            _peekDir = DirState(dir);
            bool sameName = SameFile(cur);
            bool ours = cur.Length > 0 && (sameName || KeyMatch(cur));
            Say("第 " + (_row + 1) + " 行现值=" + (cur.Length == 0 ? "（读不到）" : cur) +
                "　所在文件夹=" + DescribeDir(dir) +
                "　【目标 " + _fileBase + " @ " + _folder + "】");
            if (!ours)
            {
                _peek = PeekNotOurs;
                Say("    对不上号" + (cur.Length == 0 ? "（现值读不到，更不敢动）" : "") +
                    "：那是图纸里**别的**外链，本行两列都不碰");
            }
            else if (sameName && _peekDir == DirYes)
            {
                _peek = PeekAlready;
                Say("    本行已经指向目标文件，不动它");
            }
            else if (sameName && _peekDir == DirUnknown && _dirChannelDead)
            {
                // 「在文件夹」已经补齐（文件名走全路径，链接按全路径生效），而目录列的四条读值通道
                // 本轮已证明取不到值 —— 再为这种行点一次「在文件夹」→「浏览文件夹」→「确定」，
                // 既验证不了，也只是往 CAXA 上多叠三下无效操作（实机 14:54:55/14:56:56 每行 1.3 秒）。
                _peek = PeekAlready;
                Say("    文件名已经对，目录列四条通道全空（本轮已判死）—— " +
                    "这一行本来就是对的，不再补「在文件夹」，直接下一行");
            }
            else if (sameName)
            {
                // 目录实测不一致 → 必须补；读不到但通道还没判死 → 按 C-2 补一次并顺带验证。
                _peek = PeekFolderOnly;
                Say(_peekDir == DirNo
                    ? "    文件名已经对，文件夹实测不是目标目录 —— 只补「在文件夹」，不碰文件名"
                    : "    文件名已经对，文件夹" + DirBlindNote() + " —— 按规范仍补一次「在文件夹」，" +
                      "但不再为验证单开一张「打开」");
            }
            else
            {
                _peek = PeekChange;
                Say("    同源旧名，本行要改" + (_peekDir == DirYes ? "：目录实测已在目标，只改文件名"
                                                   : "：两列都要改"));
            }
        }

        /// <summary>这一行现在的目录（「打开」的查找范围）三态：实测一致 / 实测不一致 / 读不到。</summary>
        private static int DirState(string rowDir)
        {
            if (string.IsNullOrEmpty(rowDir)) return DirUnknown;
            return string.Equals(Norm(rowDir), Norm(_folder), StringComparison.OrdinalIgnoreCase)
                ? DirYes : DirNo;
        }

        private static string DescribeDir(string rowDir)
        {
            return rowDir.Length == 0
                ? "（读不到" + (_dirHow.Length == 0 ? "" : "，" + _dirHow) + "）"
                : rowDir + "（读法：" + _dirHow + "）";
        }

        private static string DirBlindNote()
        {
            return _dirHow.Length == 0 ? "读不到" : "读不到（" + _dirHow + "）";
        }

        /// <summary>去掉扩展名和尾部的数字/空白后是否同名 —— 即"同一个件的不同改名尝试"。</summary>
        private static bool KeyMatch(string current)
        {
            return string.Equals(KeyOf(current), KeyOf(_fileBase), StringComparison.OrdinalIgnoreCase);
        }

        private static string KeyOf(string pathOrName)
        {
            string s = Path.GetFileNameWithoutExtension(pathOrName ?? "");
            s = Norm(s);
            int i = s.Length;
            while (i > 0)
            {
                char c = s[i - 1];
                if (!char.IsDigit(c) && c != ' ' && c != '_' && c != '-') break;
                i--;
            }
            // 全被削掉（名字本身就是数字）时退回原值，别把两个不同件削成空串判定相同
            return i == 0 ? s : s.Substring(0, i);
        }

        private static bool SameFile(string current)
        {
            string name = Path.GetFileName(current ?? "");
            return name.Length > 0 &&
                   string.Equals(Norm(name), Norm(_fileBase), StringComparison.OrdinalIgnoreCase);
        }

        private static string DirOf(string path)
        {
            try
            {
                if (path.IndexOf('\\') < 0) return "";
                return Path.GetDirectoryName(path.Trim()) ?? "";
            }
            catch (Exception) { return ""; }
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().TrimEnd('\\');
            int i = s.IndexOf(" - ", StringComparison.Ordinal);   // 「xxx - 可写」这类后缀
            if (i > 1) s = s.Substring(0, i);
            return s;
        }

        // ------------------------------------------------------------ 读行与改单元格

        /// <summary>MSAA 侧能报出文字的行（实机 0 行，留着以防换版本后有了）。</summary>
        private static void ReadRows(IntPtr dlg)
        {
            _rows.Clear();
            foreach (MsaaNode n in Cells(dlg, true))
                _rows.Add(n.Name);
            // 一次全树遍历（实机 115 个节点）换来的结论是"一个单元格都没有"。
            // 记下这个结论：后面每行两次 EditCell 各再走一遍全树纯属白跑
            // （实机 10:36 一轮里 7 次 Cells 全空）。留着 Cells 本体，换版本可能就有单元格了。
            _msaaBlind = _rows.Count == 0;
            Say("网格读到 " + _rows.Count + " 行：" + Join(_rows));
        }

        /// <summary>
        /// 改某一列：MSAA 有单元格就走 accDoDefaultAction，否则按量测坐标往网格投双击。
        /// 返回 false = 这条通道连发起都没发起（调用方靠"有没有弹出子框"判成败）。
        /// </summary>
        private static bool EditCell(IntPtr dlg, bool fileNameColumn, string label)
        {
            // _msaaBlind：Plan 阶段一次全树遍历已证明 MSAA 没有单元格，后续别再重复遍历（7 次 × 115 节点）。
            List<MsaaNode> cells = _msaaBlind ? new List<MsaaNode>() : Cells(dlg, fileNameColumn);
            if (cells.Count > 0)
            {
                int i = Math.Min(_row, cells.Count - 1);
                MsaaNode cell = cells[i];
                if (Msaa.Click(cell, "双击「" + label + "」单元格"))
                {
                    Say("已对第 " + (i + 1) + " 行「" + label + "」发起默认动作 [" + cell.Action + "] " +
                        Clip(cell.Name));
                    return true;
                }
                Say("「" + label + "」单元格 accDoDefaultAction 失败");
                return false;
            }
            if (!_viaGrid) return false;

            IntPtr grid = Grid(dlg);
            if (grid == IntPtr.Zero) { Say("找不到 MFCGridCtrlEx 网格子窗口"); return false; }
            RECT rc;
            if (!GetClientRect(grid, out rc)) return false;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            int x = fileNameColumn ? Math.Min(XFile, w / 3) : Math.Min(XFolder, w - 20);
            int y = RowZeroCenter + RowStep * _row;
            if (y >= h - 2)
            {
                Say("第 " + (_row + 1) + " 行的行中心 y=" + y + " 已超出网格客户高 " + h + "，行到此为止");
                return false;
            }
            DblClick(grid, x, y);
            Say("已往网格 (" + x + "," + y + ") 投双击：第 " + (_row + 1) + " 行「" + label + "」");
            return true;
        }

        /// <summary>网格子窗口（class 含 Grid，实机为 MFCGridCtrlEx id=7158）。</summary>
        private static IntPtr Grid(IntPtr dlg)
        {
            IntPtr found = IntPtr.Zero;
            if (dlg == IntPtr.Zero || !IsWindow(dlg)) return found;
            EnumChildWindows(dlg, (h, l) =>
            {
                string cls = ClassOf(h);
                if (cls.IndexOf("Grid", StringComparison.OrdinalIgnoreCase) < 0) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// 往网格投一次真实序列：按下→抬起→双击→抬起。
        /// 用 PostMessage 不用 SendMessage：模态框的泵会把它转到主线程执行，本线程不阻塞。
        /// </summary>
        private static void DblClick(IntPtr grid, int x, int y)
        {
            int lp = (y << 16) | (x & 0xFFFF);
            PostMessage(grid, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, (IntPtr)lp);
            PostMessage(grid, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lp);
            PostMessage(grid, WM_LBUTTONDBLCLK, (IntPtr)MK_LBUTTON, (IntPtr)lp);
            PostMessage(grid, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)lp);
        }

        private static List<MsaaNode> Cells(IntPtr dlg, bool fileNameColumn)
        {
            var list = new List<MsaaNode>();
            if (dlg == IntPtr.Zero || !IsWindow(dlg)) return list;
            foreach (MsaaNode n in Msaa.AllIn(dlg, 1500))
            {
                if (n == null || string.IsNullOrEmpty(n.Name)) continue;
                string s = n.Name.Trim();
                bool isIcs = s.EndsWith(".ics", StringComparison.OrdinalIgnoreCase);
                bool isPath = s.Length > 2 && s[1] == ':' && s.IndexOf('\\') >= 0;
                if (fileNameColumn ? isIcs : (isPath && !isIcs)) list.Add(n);
            }
            return list;
        }

        // ------------------------------------------------------------ 「浏览文件夹」填写

        private static IntPtr FolderDialog()
        {
            return UiWin.FindTopByTitle(FolderTitle);
        }

        /// <summary>
        /// 往「文件夹(F)」编辑框写目标目录。那张框只有一个 Edit（+ 树 + 确定/取消），
        /// 所以取第一个可见 Edit；写不进去就交给用户手工选，绝不改树。
        /// </summary>
        private static void FillFolderEdit(IntPtr dlg)
        {
            IntPtr edit = IntPtr.Zero;
            EnumChildWindows(dlg, (h, l) =>
            {
                if (ClassOf(h) != "Edit" || !IsWindowVisible(h)) return true;
                edit = h;
                return false;
            }, IntPtr.Zero);
            if (edit == IntPtr.Zero) { Say("警告：「浏览文件夹」里没有「文件夹(F)」编辑框，只能手工选目录"); return; }
            SetWindowTextW(edit, _folder);
            string back = TextOf(edit);
            Say("已把 " + _folder + " 写进「文件夹(F)」" +
                (string.Equals(back.Trim(), _folder.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? "" : "（回读=" + back + "）"));
        }

        /// <summary>
        /// 点「浏览文件夹」的「确定」：文字就是「确定」两字（无助记符，09:11 拿 "确定(o)" 找所以
        /// HWND 命中=False），BM_CLICK 按不动时再给对话框发一条 WM_COMMAND/IDOK+BN_CLICKED。
        /// </summary>
        private static bool AcceptFolder(IntPtr dlg)
        {
            UiCtrl c = UiWin.FindInSubtree(dlg, AcceptPlain);
            if (c != null && UiWin.Click(c, "浏览文件夹「确定」")) return true;
            IntPtr btn = OkButton(dlg);
            if (btn != IntPtr.Zero)
            {
                PostMessage(dlg, WM_COMMAND, (IntPtr)((IDOK & 0xFFFF) | (BN_CLICKED << 16)), btn);
                Diagnostics.Log("ChangeLinks: 「浏览文件夹」BM_CLICK 无效，已改投 WM_COMMAND/IDOK");
                return true;
            }
            MsaaNode n = Msaa.FirstIn(dlg, FolderNeedles, false, 1200);
            return n != null && Msaa.Click(n, "浏览文件夹「确定」(MSAA)");
        }

        private static IntPtr OkButton(IntPtr dlg)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(dlg, (h, l) =>
            {
                if (ClassOf(h) != "Button" || !IsWindowVisible(h)) return true;
                if (TextOf(h).IndexOf(AcceptPlain, StringComparison.Ordinal) < 0) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // ------------------------------------------------------------ 「打开」框取证

        private static IntPtr OpenDialog()
        {
            return UiWin.FindTopByTitle(OpenTitle);
        }

        /// <summary>
        /// 读「打开」框文件名编辑框的现值（先 Edit，再退回 ComboBox 里的 Edit）。
        /// 只读不写：写与点「打开」仍由 PopupAutoYes 的 FileDialogTarget 通道做。
        /// </summary>
        private static string OpenBoxText(IntPtr ofd)
        {
            string fromEdit = "", fromCombo = "";
            EnumChildWindows(ofd, (h, l) =>
            {
                string cls = ClassOf(h);
                if (cls == "Edit")
                {
                    string t = TextOf(h);
                    if (fromEdit.Length == 0 && t.Length > 0) fromEdit = t;
                }
                else if (cls.IndexOf("ComboBox", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string t = TextOf(h);
                    if (fromCombo.Length == 0 && t.Length > 0) fromCombo = t;
                }
                return true;
            }, IntPtr.Zero);
            return fromEdit.Length > 0 ? fromEdit : fromCombo;
        }

        /// <summary>
        /// 读「打开」框的「查找范围」下拉框 = 这一行现在的**文件夹**。
        /// 这是唯一能证明「在文件夹」列真的改到位的观测点（网格单元格读不到）。
        /// 三条通道依次试：① 编辑框里就是全路径；② 下拉框自报文字；③ 下拉框**内嵌**的 Edit
        /// ——Vista 风格文件框外层 GetWindowText 是空的，文字在里面那层（实机 10:36/10:38 只走到
        /// ①②，所以全线「（读不到）」，还顺带产出两句假警报「那一列没改上」）；④ 无障碍树里
        /// 名字像绝对目录的节点。全读不到就记住这张框（_dirBlindFor），同一张「打开」不再重复扫全树，
        /// 判定按 DirUnknown 走 —— 不能把"读不到"当"目录不对"。
        /// </summary>
        private static string OpenBoxDir(IntPtr ofd)
        {
            _dirHow = "";
            if (ofd == IntPtr.Zero) return "";

            string combo = "", editPath = "";
            EnumChildWindows(ofd, (h, l) =>
            {
                string cls = ClassOf(h);
                if (cls.IndexOf("ComboBox", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string t = TextOf(h);
                    // 「文件类型」下拉显示的是 *.ics 这类掩码，只有带盘符路径的才是「查找范围」
                    if (combo.Length == 0)
                    {
                        if (t.Length > 0 && t.IndexOf('\\') >= 0) combo = t;
                        else combo = InnerEditPath(h);
                    }
                }
                else if (cls == "Edit")
                {
                    string t = TextOf(h);
                    if (editPath.Length == 0 && t.Length > 2 && t[1] == ':') editPath = t;
                }
                return true;
            }, IntPtr.Zero);
            if (editPath.Length > 0) { _dirHow = "窗口·编辑框全路径"; return DirOf(editPath); }
            if (combo.Length > 0) { _dirHow = "窗口·下拉框"; return combo; }

            if (_dirBlindFor == ofd) return "";        // 这张框已经扫过一遍无障碍，别再走全树
            string viaMsaa = DirFromMsaa(ofd);
            if (viaMsaa.Length > 0) { _dirHow = "无障碍·节点名"; return viaMsaa; }
            _dirBlindFor = ofd;
            _dirChannelDead = true;
            _dirHow = "四条通道全空";
            return "";
        }

        /// <summary>组合框内嵌那层 Edit 的文字（外层的 GetWindowText 在 Vista 风格文件框里是空的）。</summary>
        private static string InnerEditPath(IntPtr combo)
        {
            string found = "";
            EnumChildWindows(combo, (h, l) =>
            {
                string t = TextOf(h);
                if (t.Length > 2 && t.IndexOf('\\') >= 0 &&
                    !t.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                {
                    found = t;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string DirFromMsaa(IntPtr ofd)
        {
            foreach (MsaaNode n in Msaa.AllIn(ofd, 800))
            {
                if (n == null || string.IsNullOrEmpty(n.Name)) continue;
                string s = n.Name.Trim();
                if (s.Length > 2 && s[1] == ':' && s.IndexOf('\\') >= 0 &&
                    !s.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) return s;
            }
            return "";
        }

        // ------------------------------------------------------------ 按钮通道

        /// <summary>HWND 文字命中优先（真 Button 用 BM_CLICK），问不到再走以对话框为根的 MSAA。</summary>
        private static bool ClickButton(IntPtr dlg, string hwndFrag, string[] msaaNeedles, string label)
        {
            UiCtrl c = UiWin.FindInSubtree(dlg, hwndFrag);
            if (c != null && UiWin.Click(c, "改变链接文件「" + label + "」")) return true;
            MsaaNode n = Msaa.FirstIn(dlg, msaaNeedles, false, 1500);
            if (n != null && Msaa.Click(n, "改变链接文件「" + label + "」(MSAA)")) return true;
            Diagnostics.Log("ChangeLinks: 「" + label + "」点不动（HWND命中=" + (c != null) +
                " MSAA命中=" + (n != null) + "）");
            return false;
        }

        // ------------------------------------------------------------ 反馈

        /// <summary>
        /// 每行既进 trace 文件，也实时并进主程序日志（<see cref="Log"/>，11-02 用户口径）。
        /// v7 曾经「只落文件、收尾一次性回放」，实机确实不再打两遍，但把过程压到结束后才看到，
        /// 界面上这段就没有反馈了；现在回放已经删掉，所以实时一条、播出一行，仍不会重复。
        /// </summary>
        private static void Say(string line)
        {
            _report.Add(line);
            Diagnostics.Trace("ChangeLinks: " + line);
            Action<string> live = Log;
            if (live != null) live("    " + line);
        }

        private static string Join(List<string> items)
        {
            var sb = new List<string>();
            for (int i = 0; i < items.Count && i < 8; i++) sb.Add(items[i]);
            return string.Join(" | ", sb.ToArray()) + (items.Count > 8 ? " …" : "");
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }

        // ------------------------------------------------------------ 本类自用 P/Invoke
        // 后台线程要直接读写网格与「浏览文件夹」「打开」的编辑框，这些不能等主线程泵消息。

        private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                          WM_LBUTTONDBLCLK = 0x0203, MK_LBUTTON = 0x0001;
        private const int WM_COMMAND = 0x0111, IDOK = 1, BN_CLICKED = 0;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT rc);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr h, string s);

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TextOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString().Trim();
        }
    }
}
