// caxa3dcodenameextraction.cpp — CAXA 3D 2027 图纸环境 CRX 命令执行桥
//
// 作用：
//   1) 隐藏 message-only 窗口 + 100ms 定时器，轮询请求文件
//      %TEMP%\caxa3dcodenameextraction_request.txt（由 C# Add-in 写入）：
//        CMD:PING                     就绪探针，回 REQ.pong
//        CMD:PROBE                    输出当前空间实体类名直方图（识别标题栏实体类）
//        CMD:TBINFO                   快照当前标题栏信息（块名 + 属性名/值），只读
//        CMD:TB_SET|<属性名>|<值>      直写标题栏某个属性（getTitleInfo→setAttributeValue→setTitleInfo）
//        CMD:TITLEBLOCK_3DSET         派发 CX_DFT_TITLEBLOCK_3DSET（标题栏 3D 信息设默认配置）
//                                     4 条派发通道轮换：①命令名+标题栏实体名 ②+“配置名 default”
//                                     ③裸命令名 ④定时器里直接 sendStringToExecute（键入通道）；
//                                     以「标题栏信息集合是否真的变化」为唯一成功判据，
//                                     成功回 REQ.tb-result，试完仍无变化回 REQ.tb-giveup。
//        CMD:TITLEBLOCK_3DSET_TRY|arg 同上，但带一个字符串输入（实测“配置名”是否需要交互）
//        CMD:TB_SETMANY|名=值|名=值   一次打开图纸记录、批量写多个标题栏属性后统一提交，
//                                     回读并做前后 diff，回 REQ.tb-setmany-result（数据直写主通道）
//        CMD:CMDPROBE                 命令表探针：lookupGlobalCmd / lookupLocalCmd 查若干命令名
//                                     是否存在（区分“名字不在命令表”与“上下文不合法”），只读
//        CMD:SENDSTR|<文本>           把任意文本直接投进文档输入缓冲（sendStringToExecute，
//                                     bActivate=true / bWrapUpInactiveDoc=false / bEcho=true），
//                                     并记录返回值与 inputPending —— 键入通道的唯一合规用法
//        CMD:TB_ABORT                 用户点「停止」：立刻终止标题栏 3D 信息的轮换派发与判定，
//                                     释放临时选择集，回 REQ.tb-aborted（不再回 tb-result/tb-giveup）
//        CMD:3DAPI                    零风险探针：只 GetProcAddress 记录 3D 信息相关导出是否存在
//        CMD:CLOSEDOC|<fileName>      原生保存链（Ctrl+S 同路）+ WM_CLOSE，只关标题含
//                                     该文件名的图纸视图子窗口；fileName 必填，
//                                     一个都没匹配上就一个都不关（回 CLOSEDOC.name-mismatch）
//        CMD:CLOSERAW|<fileName>      与 CLOSEDOC 同一套窗口匹配，但**不存盘**：只投 WM_CLOSE
//                                     （图纸环境预热用的载体图纸没动过，用户要求不保存直接关）；
//                                     fileName 必填，匹配/拒绝规则与 CLOSEDOC 一致
//        CMD:CLOSE                    crxedCommand 直调 CX_DFT_ViewClose
//        CMD:SAVE                     crxedCommand 直调 CX_save（等待内层 CX_DFT_ViewSave）
//        CMD:EXEC|<命令名>            按命令名直调（实测用）
//        CMD:DUMPWND                  打印本进程顶层窗口树（诊断用）
//   2) CRxEditorReactor 把命令事件追加到 %TEMP%\caxa3dcodenameextraction_crx.log，
//      C# 端按偏移量增量读取以等待命令结束；命令前后做实体快照 diff 并记录 pickfirst。
//
// 日志格式（每行，UTF-8）：
//   HH:mm:ss | EDITOR.commandEnded | CX_DFT_TITLEBLOCK_3DSET
//   HH:mm:ss | REQ.ack | seq=N cmd=CMD:PING
//   HH:mm:ss | REQ.close-done | CX_DFT_ViewClose ended
//   HH:mm:ss | DB.snapshot | CX_DFT_TITLEBLOCK_3DSET | ended space=... count 53->53 +0 -0
//
// 部署：编译输出 .crx → 复制到 CAXADraft\bin，并登记 Modules\CxAutorun.cx。
//
// 铁律（见 caxa3d-csharp-二次开发规则）：
//   * 定时器回调里绝对不直接调 sendStringToExecute（在 CX_DFT_default 上下文内重入 → CAXA 崩溃）。
//     注册命令的正确派发路径：crxDocManager->executeInApplicationContext() 把派发推到应用上下文，
//     在回调里再 sendStringToExecute（规则：注册命令走键入通道；非命令上下文禁用 crxedCommand 直调）。
//     crxedCommand 直调只作兜底重试（编辑器真忙时会被静默丢弃：ret=0 但无对应 commandWillStart）。
//   * 只用 Win32 消息泵做派发，天然在 CAXA 主 UI 线程，满足 CRX/数据库“必须在主线程”的约束。

#include "StdAfx.h"

#include <set>
#include <vector>
#include <utility>

#include "dbapserv.h"
#include "dbid.h"
#include "dbmain.h"
#include "dbents.h"
#include "crxed.h"
#include "crxdocman.h"     // crxDocManager / sendStringToExecute
#include "crxcmd.h"        // crxedRegCmds / lookupGlobalCmd / lookupLocalCmd / functionAddr
#include "cdscodes.h"      // RTSTR / RTNONE / RTNORM
#include "crxedcds.h"      // crxedCommand / crxedSSGet / crxedSSAdd（命令表与选择集直调）
#include "crxentrypoint.h"
#include "CrxCusUI.h"      // crxUIManager / 工具栏按钮 / 菜单 / Ribbon
#include "CrxDbBomGlobals.h" // GetCrxDbTitleBlk / GetCrxDbFrameBlk（标题栏块 ObjectId）
#include "dbsymtb.h"         // CRxDbPaperTableRecord / CRxAttCollInfoObject（标题栏信息集合）

AC_IMPLEMENT_EXTENSION_MODULE(Caxa3dCodeNameExtractionDLL)

//-----------------------------------------------------------------------------
// 共享状态
//-----------------------------------------------------------------------------
static CRxEditorReactor* g_pEditorReactor = NULL;
static CRITICAL_SECTION  g_logCS;
static bool              g_initialized   = false;
static HANDLE            g_hLogFile      = INVALID_HANDLE_VALUE;
static HWND              g_hWatcherWnd   = NULL;   // 隐藏 message-only 窗口
static const UINT_PTR    kWatcherTimerId = 1;
static const UINT        kWatcherTimerMs = 100;     // 100ms：命令取件延迟直接决定单文件耗时

//-----------------------------------------------------------------------------
// 工具：%TEMP% 路径拼接
//-----------------------------------------------------------------------------
static void TempPath(wchar_t* out, size_t cap, const wchar_t* name)
{
    DWORD n = GetEnvironmentVariableW(L"TEMP", out, (DWORD)cap);
    if (n == 0) wcscpy_s(out, cap, L".");
    wchar_t* last = wcsrchr(out, L'\\');
    if (last && *(last + 1) == 0) out[last - out + 1] = 0;
    wcscat_s(out, cap, L"\\");
    wcscat_s(out, cap, name);
}

//-----------------------------------------------------------------------------
// 工具：打开/创建日志文件（追加模式，Windows API 避免 CRT 冲突）
//-----------------------------------------------------------------------------
static void OpenLogFile()
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, L"caxa3dcodenameextraction_crx.log");
    g_hLogFile = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             NULL, OPEN_ALWAYS, 0, NULL);
}

static void AppendLogBytes(const char* data, int len)
{
    if (g_hLogFile == INVALID_HANDLE_VALUE || !data || len <= 0) return;
    DWORD written = 0;
    WriteFile(g_hLogFile, data, len, &written, NULL);
    WriteFile(g_hLogFile, "\r\n", 2, &written, NULL);
}

//-----------------------------------------------------------------------------
// 工具：拼接一行完整日志（全部 CStringA，规避 MFC/CRT 冲突）
//-----------------------------------------------------------------------------
static void AppendLog(const CStringA& category, const CStringA& eventName,
                      const CStringA& arg1 = CStringA(),
                      const CStringA& arg2 = CStringA())
{
    if (!g_initialized) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    CStringA timeStr;
    timeStr.Format("%02d:%02d:%02d", st.wHour, st.wMinute, st.wSecond);

    CStringA line;
    line.Format("%s | %s.%s", timeStr, category, eventName);
    if (!arg1.IsEmpty()) { line += CStringA(" | "); line += arg1; }
    if (!arg2.IsEmpty()) { line += CStringA(" | "); line += arg2; }

    EnterCriticalSection(&g_logCS);
    AppendLogBytes(line, line.GetLength());
    LeaveCriticalSection(&g_logCS);
}

//-----------------------------------------------------------------------------
// 工具：CxCHAR (wchar_t) → CStringA / 宽字符 → UTF-8
//-----------------------------------------------------------------------------
static CStringA CxCharToMbs(const CxCHAR* w)
{
    if (!w || !w[0]) return CStringA();
    CStringA result;
    int need = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
    if (need > 0 && need < 4096) {
        char buf[4096];
        WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, need, NULL, NULL);
        result.SetString(buf);
    }
    return result;
}

static CStringA WideToUtf8(const wchar_t* w)
{
    if (!w || !w[0]) return CStringA();
    int need = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
    if (need <= 1) return CStringA();
    if (need > 1000) need = 1000;
    char buf[1024];
    buf[0] = 0;
    WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, need, NULL, NULL);
    buf[sizeof(buf) - 1] = 0;
    return CStringA(buf);
}

//-----------------------------------------------------------------------------
// 请求文件 → 命令字符串（UTF-8 → 宽字符，保证末尾有 "\n"）
//-----------------------------------------------------------------------------
static bool ReadRequestFile(CStringW& cmdOut)
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, L"caxa3dcodenameextraction_request.txt");

    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return false;

    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;

    char buf[2048];
    DWORD read = 0;
    BOOL ok = ReadFile(h, buf, sizeof(buf) - 1, &read, NULL);
    CloseHandle(h);

    // 先删请求文件再执行：即使执行耗时，C# 端也能排队下一条
    DeleteFileW(path);

    if (!ok || read == 0) return false;
    buf[read] = 0;

    // UTF-8 → 宽字符（容错跳过 BOM）
    const char* p = buf;
    if (read >= 3 && (unsigned char)buf[0] == 0xEF &&
        (unsigned char)buf[1] == 0xBB && (unsigned char)buf[2] == 0xBF)
        p += 3;

    int need = MultiByteToWideChar(CP_UTF8, 0, p, -1, NULL, 0);
    if (need <= 0 || need >= 2048) return false;
    wchar_t wbuf[2048];
    MultiByteToWideChar(CP_UTF8, 0, p, -1, wbuf, need);

    cmdOut.SetString(wbuf);
    cmdOut.Trim();
    return !cmdOut.IsEmpty();
}

//-----------------------------------------------------------------------------
// 写请求文件（纯 Win32 调用，不碰任何 CRX API）——实测用
//-----------------------------------------------------------------------------
static void WriteRequestFile(const char* utf8)
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, L"caxa3dcodenameextraction_request.txt");

    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, CREATE_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) { AppendLog("REQ", "write-fail", ""); return; }

    DWORD w = 0;
    WriteFile(h, utf8, (DWORD)strlen(utf8), &w, NULL);
    CloseHandle(h);
}

//-----------------------------------------------------------------------------
// 窗口工具：描述 / 遍历（诊断）/ 原生保存关闭文档
//
// 原生保存链实证：crxedCommand(RTSTR,"CX_save") 返回 0，但 reactor 里连
// CX_save_Impl / CX_DFT_ViewSave 都不出现 —— 内部命令名不在命令表里，
// crxedCommand 是"静默成功但什么都不做"。唯一可靠路径是让 CAXA 自己走 UI 派发：
// 向文档窗口 SendMessage(WM_COMMAND, ID_FILE_SAVE) 保存（与 Ctrl+S / 保存按钮同路），
// 再 PostMessage(WM_CLOSE) 关闭（文档已落盘则不再弹「是否保存」）。
// 只用 Win32，不碰任何 CRX API，规避历史上的崩溃点。
//-----------------------------------------------------------------------------
static CStringA WinDesc(HWND h)
{
    wchar_t cls[128] = {0};
    wchar_t txt[512] = {0};
    GetClassNameW(h, cls, 128);
    GetWindowTextW(h, txt, 512);
    CStringA out;
    out.Format("hwnd=%p class=%s title=%s vis=%d",
               (void*)h, WideToUtf8(cls).GetString(), WideToUtf8(txt).GetString(),
               IsWindowVisible(h) ? 1 : 0);
    return out;
}

static void ProbeMdi();

static void DumpWindowTree(const char* why)
{
    AppendLog("WND", "dump-start", why);
    int nTop = 0;
    for (HWND h = GetTopWindow(NULL); h && nTop < 12; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId()) continue;
        if (!IsWindowVisible(h)) continue;

        AppendLog("WND", "top", WinDesc(h));
        nTop++;

        int total = 0;
        for (HWND c = GetWindow(h, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT)) total++;
        CStringA s; s.Format("children=%d", total);
        AppendLog("WND", "child-count", s);

        int nChild = 0;
        for (HWND c = GetWindow(h, GW_CHILD); c && nChild < 60; c = GetWindow(c, GW_HWNDNEXT))
        {
            AppendLog("WND", " child", WinDesc(c));
            nChild++;
            int nG = 0;
            for (HWND g = GetWindow(c, GW_CHILD); g && nG < 10; g = GetWindow(g, GW_HWNDNEXT))
            {
                AppendLog("WND", "  grand", WinDesc(g));
                nG++;
            }
        }
    }
    ProbeMdi();
    AppendLog("WND", "dump-end", "");
}

static HWND g_mdiFound = NULL;

static BOOL CALLBACK FindMdiProc(HWND h, LPARAM)
{
    wchar_t cls[64] = {0};
    GetClassNameW(h, cls, 64);
    if (wcscmp(cls, L"MDIClient") == 0) { g_mdiFound = h; return FALSE; }
    return TRUE;
}

static void ProbeMdi()
{
    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId()) continue;
        if (!IsWindowVisible(h)) continue;

        HWND mdi = FindWindowExW(h, NULL, L"MDIClient", NULL);
        if (!mdi)
        {
            g_mdiFound = NULL;
            EnumChildWindows(h, FindMdiProc, 0);
            mdi = g_mdiFound;
        }

        CStringA s;
        s.Format("top=%p mdi=%p", (void*)h, (void*)mdi);
        AppendLog("WND", "mdi-probe", s);

        if (!mdi) continue;
        int n = 0;
        for (HWND c = GetWindow(mdi, GW_CHILD); c && n < 10; c = GetWindow(c, GW_HWNDNEXT))
        {
            CStringA d; d.Format("doc[%d] %s", n, WinDesc(c).GetString());
            AppendLog("WND", "mdi-child", d);
            n++;
        }
    }
}

// 统计本进程可见的 #32770 对话框（模态弹窗会把定时器/命令派发全堵住）
static int CountSelfDialogs(CStringA& firstTitle)
{
    int n = 0;
    firstTitle.Empty();
    wchar_t cls[64] = {0};
    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId()) continue;
        if (!IsWindowVisible(h)) continue;
        cls[0] = 0;
        GetClassNameW(h, cls, 64);
        if (wcscmp(cls, L"#32770") != 0) continue;
        if (n == 0)
        {
            wchar_t txt[256] = {0};
            GetWindowTextW(h, txt, 256);
            firstTitle = WideToUtf8(txt);
        }
        n++;
    }
    return n;
}

// 「设置3D信息」对话框是否正弹着（与 CRX 同进程：图纸环境内嵌在 IronCAD.exe 里）。
// 实测（08:13:45）：打字通道把 CX_DFT_TITLEBLOCK_3DSET 投进去后命令真的跑起来了，
// 但它是模态对话框命令 —— 对话框开着期间命令不会结束，旧逻辑 20s 看门狗会误判超时、
// 并继续轮换通道重复派发（等于反复弹框）。所以弹窗期间必须暂停看门狗与派发。
static BOOL CALLBACK Find3DDlgProc(HWND h, LPARAM lp)
{
    wchar_t cls[64] = {0};
    GetClassNameW(h, cls, 64);
    if (wcscmp(cls, L"#32770") != 0) return TRUE;
    wchar_t txt[256] = {0};
    GetWindowTextW(h, txt, 256);
    if (wcsstr(txt, L"设置3D信息"))
    {
        *(HWND*)lp = h;
        return FALSE;
    }
    return TRUE;
}

static HWND FindTitleBlock3DDlg()
{
    HWND found = NULL;
    EnumWindows(Find3DDlgProc, (LPARAM)&found);
    return found;
}

// 记录目标文档当前的「已修改」状态（MFC CDocument::IsModified）
static void LogDocModified(const CStringW& fileName){
    CRxApDocument* target = NULL;
    CRxApDocumentIterator* it = crxDocManagerPtr()->newAcApDocumentIterator();
    if (!it) { AppendLog("MOD", "no-iterator", ""); return; }
    for (; !it->done(); it->step())
    {
        CRxApDocument* d = it->document();
        if (!d) continue;
        const CxCHAR* fn = d->fileName();
        if (!fn || !fn[0]) continue;
        if (fileName.IsEmpty() || CStringW(fn).Find(fileName.GetString()) >= 0)
        { target = d; break; }
    }
    delete it;

    if (target && target->cDoc())
    {
        CStringA s;
        s.Format("modified=%d file=%s",
                 target->cDoc()->IsModified() ? 1 : 0,
                 WideToUtf8(target->fileName()).GetString());
        AppendLog("MOD", "state", s);
    }
    else
    {
        AppendLog("MOD", "doc-not-found", WideToUtf8(fileName).GetString());
    }
}

// 关闭前直接保存（与手动 Ctrl+S / 保存按钮同一条 MFC 命令路由）：
// 向文档 MDI 子窗口 SendMessage(WM_COMMAND, ID_FILE_SAVE=0xE103)，同步返回时已落盘。
static int SaveDocByCommand(const CStringW& fileName)
{
    int saved = 0;
    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId() || !IsWindowVisible(h)) continue;

        HWND mdi = FindWindowExW(h, NULL, L"MDIClient", NULL);
        if (!mdi) { g_mdiFound = NULL; EnumChildWindows(h, FindMdiProc, 0); mdi = g_mdiFound; }
        if (!mdi) continue;

        for (HWND c = GetWindow(mdi, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT))
        {
            wchar_t title[512] = {0};
            GetWindowTextW(c, title, 512);
            CStringW t(title);
            if (!fileName.IsEmpty() && t.Find(fileName.GetString()) < 0) continue;

            wchar_t cls[64] = {0};
            GetClassNameW(c, cls, 64);
            CStringA s;
            s.Format("class=%s title=%s", WideToUtf8(cls).GetString(), WideToUtf8(title).GetString());
            AppendLog("SAVE", "cmd-post", s);
            SendMessageW(c, WM_COMMAND, 0xE103 /*ID_FILE_SAVE*/, 0);
            AppendLog("SAVE", "direct-done", "");
            saved++;
            if (!fileName.IsEmpty()) break;   // 只保存匹配这一个
        }
        if (saved > 0) break;
    }
    if (saved == 0)
        AppendLog("SAVE", "window-not-found", WideToUtf8(fileName).GetString());
    return saved;
}

// 向「当前文档窗口」投递 WM_CLOSE。
//
// 铁律：只关标题含目标文件名的 MDI 子窗口，一个都没匹配上就什么都不关。
// 早先这里有个「退化为关闭全部 MDI 子窗口」的兜底，11:29:17 实机把它引爆了：
// 请求关 …内牙接头6666666666.exb，实际开着的是旧名 …内牙接头.exb ⇒ 未匹配 ⇒
// 兜底把 2 个子窗口全关了，其中一个是 SC08020102.01 壳体.ics（3D 设计环境，
// class Afx:…000E080B），整个设计环境被连带关掉。改名没成功时关错文件比不关文件
// 严重得多，所以宁可回来报「没匹配上」，让人/C# 自己决定。
// 另外只关 class 是 AfxFrameOrView140u（图纸视图）的子窗口，3D 场景子窗口永不关。
static bool IsDrawingView(HWND h)
{
    wchar_t cls[64] = {0};
    GetClassNameW(h, cls, 64);
    return lstrcmpW(cls, L"AfxFrameOrView140u") == 0;
}

static int CloseDocWindows(const CStringW& fileName)
{
    int closed = 0;
    int skippedNoMdi = 0;
    int seenAll = 0;
    CStringA seenTitles;

    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId()) continue;
        if (!IsWindowVisible(h)) continue;

        HWND mdi = FindWindowExW(h, NULL, L"MDIClient", NULL);
        if (!mdi)
        {
            g_mdiFound = NULL;
            EnumChildWindows(h, FindMdiProc, 0);
            mdi = g_mdiFound;
        }
        if (!mdi)
        {
            skippedNoMdi++;
            // 无 MDI 结构的顶层窗口 = CAXA 主框架（它的标题天然含当前文档名）。
            // 按标题匹配就投 WM_CLOSE 等于关掉整个程序 —— 这条绝对禁止，只记录不操作。
            wchar_t wt[512] = {0};
            GetWindowTextW(h, wt, 512);
            CStringW t(wt);
            if (!fileName.IsEmpty() && t.Find(fileName.GetString()) >= 0)
            {
                CStringA s;
                s.Format("顶层窗口标题含目标文件名，但它是主框架，按铁律不关：%s", WinDesc(h).GetString());
                AppendLog("CLOSEDOC", "skip-mainframe", s);
            }
            continue;
        }

        // 只关标题含目标文件名的图纸视图子窗口
        int matched = 0, all = 0;
        HWND c = GetWindow(mdi, GW_CHILD);
        for (; c; c = GetWindow(c, GW_HWNDNEXT))
        {
            wchar_t title[512] = {0};
            GetWindowTextW(c, title, 512);
            all++;
            seenAll++;
            if (seenTitles.GetLength() > 0) seenTitles += " | ";
            seenTitles += WideToUtf8(title).GetString();

            CStringW t(title);
            bool hit = !fileName.IsEmpty() && t.Find(fileName.GetString()) >= 0;
            if (hit && !IsDrawingView(c))
            {
                CStringA s;
                s.Format("标题匹配但不是图纸视图，跳过不关：%s", WinDesc(c).GetString());
                AppendLog("CLOSEDOC", "skip-not-view", s);
                continue;
            }
            if (hit)
            {
                AppendLog("CLOSEDOC", "post", WinDesc(c));
                PostMessageW(c, WM_CLOSE, 0, 0);
                closed++; matched++;
            }
        }

        if (matched == 0 && all > 0 && !fileName.IsEmpty())
        {
            CStringA s;
            s.Format("文件名未匹配，一个窗口都不关（本 MDI 子窗口共 %d 个：", all);
            AppendLog("CLOSEDOC", "name-mismatch", s + seenTitles.GetString() + "】");
        }
    }

    if (closed == 0)
    {
        CStringA firstTitle;
        int dlg = CountSelfDialogs(firstTitle);
        CStringA s;
        s.Format("没找到可关的 MDI 子窗口（无 MDIClient 的顶层窗口数=%d 当前弹窗=%d first=%s 实开文档=%d:%s）",
                 skippedNoMdi, dlg, firstTitle.GetString(), seenAll, seenTitles.GetString());
        AppendLog("CLOSEDOC", "no-window", s);
    }
    return closed;
}

//-----------------------------------------------------------------------------
// 数据库快照：DB reactor 在内嵌图纸环境不触发，改用命令前后实体集合 diff。
//-----------------------------------------------------------------------------
static std::set<CAXA::IntDbId> g_snapBefore;
static CStringA                g_snapCmd;
static int                     g_snapCountBefore = 0;
static bool                    g_snapValid       = false;

// 纯状态切换命令，跳过快照（避免每次状态切换都遍历一遍数据库）
static bool SkipSnapshotCmd(const CStringA& cmd)
{
    return cmd == "CX_DFT_default" || cmd == "CX_DFT_DocAcitve";
}

static void TakeSnapshot(std::set<CAXA::IntDbId>& ids, int& count, CStringA& spaceInfo)
{
    ids.clear();
    count = 0;
    CRxDbDatabase* pDb = crxdbHostApplicationServices()->workingDatabase();
    if (!pDb) { spaceInfo = "no-db"; return; }

    CRxDbObjectId spId = pDb->currentSpaceId();
    spaceInfo.Format("space=%lld", (long long)spId.asOldId());

    CRxDbBlockTableRecord* pRec = NULL;
    if (crxdbOpenObject(pRec, spId, CRxDb::kForRead) != CDraft::eOk || !pRec)
    {
        spaceInfo += "(open-failed)";
        return;
    }

    CRxDbBlockTableRecordIterator* pIt = NULL;
    if (pRec->newIterator(pIt) == CDraft::eOk && pIt)
    {
        for (; !pIt->done() && count < 2000; pIt->step())
        {
            CRxDbObjectId id;
            if (pIt->getEntityId(id) == CDraft::eOk) { ids.insert(id.asOldId()); count++; }
        }
        delete pIt;
    }
    pRec->close();
}

// 命令结束：比对快照，把新增/删除对象写进日志。
// 说明：CRxDbObject::isModified() 未导出，"修改现有对象"类命令只能看到 +0 -0。
static void ReportSnapshotDelta(const CStringA& cmd, const char* status)
{
    g_snapValid = false;

    CRxDbDatabase* pDb = crxdbHostApplicationServices()->workingDatabase();
    if (!pDb) { AppendLog("DB", "snapshot", cmd, "no-db"); return; }

    CRxDbObjectId spId = pDb->currentSpaceId();
    CRxDbBlockTableRecord* pRec = NULL;
    if (crxdbOpenObject(pRec, spId, CRxDb::kForRead) != CDraft::eOk || !pRec)
    { AppendLog("DB", "snapshot", cmd, "open-space-failed"); return; }

    CStringA added;
    std::set<CAXA::IntDbId> after;
    int total = 0, newCount = 0;
    CRxDbBlockTableRecordIterator* pIt = NULL;
    if (pRec->newIterator(pIt) == CDraft::eOk && pIt)
    {
        for (; !pIt->done() && total < 2000; pIt->step())
        {
            CRxDbObjectId id;
            if (pIt->getEntityId(id) != CDraft::eOk) continue;
            CAXA::IntDbId oldId = id.asOldId();
            after.insert(oldId);
            total++;

            if (g_snapBefore.find(oldId) != g_snapBefore.end()) continue;

            newCount++;
            if (newCount <= 10)
            {
                CRxDbEntity* pEnt = NULL;
                if (pIt->getEntity(pEnt, CRxDb::kForRead) == CDraft::eOk && pEnt)
                {
                    CRxClass* cls = pEnt->isA();
                    CStringA clsName = CxCharToMbs(cls ? cls->name() : NULL);
                    if (clsName.IsEmpty()) clsName = CStringA("Unknown");
                    if (!added.IsEmpty()) added += ", ";
                    added += clsName;
                    pEnt->close();
                }
            }
        }
        delete pIt;
    }
    pRec->close();

    int delCount = 0;
    for (std::set<CAXA::IntDbId>::const_iterator it = g_snapBefore.begin(); it != g_snapBefore.end(); ++it)
        if (after.find(*it) == after.end()) delCount++;

    CStringA head;
    head.Format("%s space=%lld count %d->%d +%d -%d",
                status, (long long)spId.asOldId(), g_snapCountBefore, total, newCount, delCount);
    if (!added.IsEmpty()) head += CStringA("; new: ") + added;
    AppendLog("DB", "snapshot", cmd, head);
}

//-----------------------------------------------------------------------------
// 选择集（pickfirst）记录：只改现有对象、不新增对象的命令靠快照看不到变化。
// 注意：crxedSSGetFirst / crxedSSSetFirst 在头文件声明但 CrxEdApi.lib 未导出
//       （链接期 LNK2019）；crxedSSGet(L"I") 确实导出，只读不提示、不改用户选择集。
//       程序化"设置"pickfirst 无可用 API —— 标题栏 3D 信息设置前需人工选中标题栏。
//-----------------------------------------------------------------------------
static CStringA DescribeSelectionSet(const crx_name ss)
{
    long len = 0;
    if (crxedSSLength(ss, &len) != RTNORM || len <= 0) return CStringA("0");

    CStringA detail;
    long shown = 0;
    for (long i = 0; i < len && shown < 10; i++)
    {
        crx_name entres;
        memset(entres, 0, sizeof(entres));
        if (crxedSSName(ss, i, entres) != RTNORM) continue;

        CRxDbObjectId oid;
        if (crxdbGetObjectId(oid, entres) != CDraft::eOk) continue;

        CRxDbEntity* pEnt = NULL;
        if (crxdbOpenObject(pEnt, oid, CRxDb::kForRead) != CDraft::eOk || !pEnt) continue;

        CRxClass* cls = pEnt->isA();
        CStringA clsName = CxCharToMbs(cls ? cls->name() : NULL);
        if (clsName.IsEmpty()) clsName = CStringA("Unknown");
        if (!detail.IsEmpty()) detail += ", ";
        detail += clsName;
        pEnt->close();
        shown++;
    }

    CStringA s;
    s.Format("%ld", len);
    if (!detail.IsEmpty()) s += CStringA(" [") + detail + "]";
    return s;
}

static void ReportSelection(const char* phase)
{
    crx_name ss;
    memset(ss, 0, sizeof(ss));
    // "I" = pickfirst / 隐含选择：不弹任何交互提示；没有预选时返回 RTERROR
    if (crxedSSGet(L"I", NULL, NULL, NULL, ss) != RTNORM) return;

    CStringA desc = DescribeSelectionSet(ss);
    crxedSSFree(ss);

    if (desc == "0") return;   // 选择集为空，不写日志（避免刷屏）

    CStringA s;
    s.Format("%s pickfirst=%s", phase, desc);
    AppendLog("DB", "sel", s);
}

//-----------------------------------------------------------------------------
// 实体类名直方图：识别「标题栏」对应的实体类，供人工实测判定。
//-----------------------------------------------------------------------------
static void ProbeEntityHistogram()
{
    CRxDbDatabase* pDb = crxdbHostApplicationServices()->workingDatabase();
    if (!pDb) { AppendLog("PROBE", "no-db", ""); return; }

    CRxDbObjectId spId = pDb->currentSpaceId();
    CRxDbBlockTableRecord* pRec = NULL;
    if (crxdbOpenObject(pRec, spId, CRxDb::kForRead) != CDraft::eOk || !pRec)
    { AppendLog("PROBE", "open-space-failed", ""); return; }

    std::map<CStringA, int> hist;
    int total = 0;
    CRxDbBlockTableRecordIterator* pIt = NULL;
    if (pRec->newIterator(pIt) == CDraft::eOk && pIt)
    {
        for (; !pIt->done() && total < 5000; pIt->step())
        {
            CRxDbObjectId id;
            if (pIt->getEntityId(id) != CDraft::eOk) continue;
            CRxDbEntity* pEnt = NULL;
            if (pIt->getEntity(pEnt, CRxDb::kForRead) != CDraft::eOk || !pEnt) continue;
            CRxClass* cls = pEnt->isA();
            CStringA clsName = CxCharToMbs(cls ? cls->name() : NULL);
            if (clsName.IsEmpty()) clsName = CStringA("Unknown");
            hist[clsName]++;
            total++;
            pEnt->close();
        }
        delete pIt;
    }
    pRec->close();

    CStringA head;
    head.Format("space=%lld total=%d", (long long)spId.asOldId(), total);
    AppendLog("PROBE", "histogram", head);
    for (std::map<CStringA, int>::const_iterator it = hist.begin(); it != hist.end(); ++it)
    {
        CStringA s;
        s.Format("%s = %d", it->first.GetString(), it->second);
        AppendLog("PROBE", "class", s);
    }
}

//-----------------------------------------------------------------------------
// 标题栏信息读写（官方 CRX 入口，免对话框）
//   GetCrxDbTitleBlk(pDb)                      → 标题栏块参照 ObjectId（块名即"标题栏"）
//   CRxDbDatabase::getPaperTable(...)          → 图纸表
//   CRxDbPaperTable::CurrentPaperId()          → 当前图纸记录 ObjectId
//   CRxDbPaperTableRecord::getTitleInfo(&p)    → CRxAttCollInfoObject（标题栏信息集合）
//   CRxAttCollInfoObject: getAttributesCount / getAttributeName(i) / getAttributeValue(name)
//                         setAttributeValue(value, iIndex)
//   CRxDbPaperTableRecord::setTitleInfo(p)     → 提交
//   注意：dbPaper.h 的 CRxFTInfoObject 版本是过时头（CRX\Lib 里无导出符号），禁止照其编程；
//        生效声明在 dbsymtb.h（CRxAttCollInfoObject @1670 / CRxDbPaperTableRecord @1736）。
//-----------------------------------------------------------------------------
static bool OpenCurrentPaper(CRxDb::OpenMode mode,
                             CRxDbPaperTable*& pTable, CRxDbPaperTableRecord*& pPaper)
{
    pTable = NULL; pPaper = NULL;
    CRxDbDatabase* pDb = crxdbHostApplicationServices()->workingDatabase();
    if (!pDb) { AppendLog("TB", "no-db", "workingDatabase 为空"); return false; }
    if (pDb->getPaperTable(pTable, CRxDb::kForRead) != CDraft::eOk || !pTable)
    { AppendLog("TB", "no-papertable", ""); return false; }

    CRxDbObjectId paperId = pTable->CurrentPaperId();
    if (paperId.isNull())
    { AppendLog("TB", "no-currentpaper", ""); pTable->close(); pTable = NULL; return false; }

    if (crxdbOpenObject(pPaper, paperId, mode) != CDraft::eOk || !pPaper)
    { AppendLog("TB", "open-paper-failed", ""); pTable->close(); pTable = NULL; return false; }
    return true;
}

static void CloseCurrentPaper(CRxDbPaperTable*& pTable, CRxDbPaperTableRecord*& pPaper)
{
    if (pPaper) { pPaper->close(); pPaper = NULL; }
    if (pTable) { pTable->close(); pTable = NULL; }
}

// 标题栏块参照所属块记录名（用户前提：块名就是"标题栏"）
static void LogTitleBlkName(const char* phase)
{
    CRxDbObjectId tbId = GetCrxDbTitleBlk(NULL);
    if (tbId.isNull()) { AppendLog("TB", "titleblk-null", CStringA(phase)); return; }

    CRxDbBlockReference* pRef = NULL;
    if (crxdbOpenObject(pRef, tbId, CRxDb::kForRead) != CDraft::eOk || !pRef)
    { AppendLog("TB", "titleblk-open-failed", CStringA(phase)); return; }

    CRxDbBlockTableRecord* pRec = NULL;
    CRxDbObjectId recId = pRef->blockTableRecord();
    CStringA name;
    if (!recId.isNull()
        && crxdbOpenObject(pRec, recId, CRxDb::kForRead) == CDraft::eOk && pRec)
    {
        CxCHAR* pName = NULL;
        if (pRec->getName(pName) == CDraft::eOk && pName) name = CxCharToMbs(pName);
        pRec->close();
    }
    pRef->close();

    CStringA s;
    s.Format("%s titleBlkId=%lld recId=%lld blockName=%s", phase,
             (long long)tbId.asOldId(), (long long)recId.asOldId(),
             name.IsEmpty() ? "<unnamed>" : name.GetString());
    AppendLog("TB", "titleblk", s);
}

//-----------------------------------------------------------------------------
// 标题栏信息前后快照（判定“3D 信息真的被写成默认配置”的唯一依据）
// 背景：CX_DFT_TITLEBLOCK_3DSET 每次打开图纸都会由 CAXA 自己跑一次，且对当前图纸
//       不产生任何实体变化（DB.snapshot 恒为 +0 -0）——所以「命令跑过」≠「设置成功」。
//       COM 侧看不到标题栏（exb 文档对 COM 不可达、类型库无块级寻址），
//       因此成功与否只能由标题栏信息集合（CRxAttCollInfoObject）的真实差异来判定。
//-----------------------------------------------------------------------------
static std::map<CStringA, CStringA> g_tbPre;
static std::map<CStringA, CStringA> g_tbPost;
static int g_tbChanged = 0;   // 最近一次前后比对的变化条数（>0 = 判定成功）

// 从标题栏信息集合读一份只读快照
static bool ReadTitleInfo(std::map<CStringA, CStringA>& out)
{
    out.clear();
    CRxDbPaperTable* pTable = NULL; CRxDbPaperTableRecord* pPaper = NULL;
    if (!OpenCurrentPaper(CRxDb::kForRead, pTable, pPaper)) return false;

    CRxAttCollInfoObject* pInfo = NULL;
    pPaper->getTitleInfo(&pInfo);
    bool hadInfo = (pInfo != NULL);
    int n = hadInfo ? pInfo->getAttributesCount() : 0;
    for (int i = 0; i < n; i++)
    {
        CxCHAR* pName = NULL;
        pInfo->getAttributeName(pName, i);
        if (!pName) continue;
        CxCHAR* pVal = NULL;
        pInfo->getAttributeValue(pVal, pName);
        CStringA v;
        if (pVal) v = CxCharToMbs(pVal);
        out[CStringA(CxCharToMbs(pName))] = v;
    }
    if (pInfo) pPaper->ReleaseAttCollInfoObject(pInfo);
    CloseCurrentPaper(pTable, pPaper);
    return hadInfo;
}

// 前/后快照比对：逐条列出值发生变化的属性（定位“到底哪一项被改了”）
static void DiffTitleInfo(const char* phase)
{
    int changed = 0, added = 0;
    for (std::map<CStringA, CStringA>::const_iterator it = g_tbPre.begin(); it != g_tbPre.end(); ++it)
    {
        std::map<CStringA, CStringA>::const_iterator jt = g_tbPost.find(it->first);
        if (jt == g_tbPost.end()) continue;
        if (jt->second == it->second) continue;
        changed++;
        CStringA s;
        s.Format("%s %s: %s -> %s", phase, it->first.GetString(),
                 it->second.IsEmpty() ? "<empty>" : it->second.GetString(),
                 jt->second.IsEmpty() ? "<empty>" : jt->second.GetString());
        AppendLog("TB", "diff-item", s);
    }
    for (std::map<CStringA, CStringA>::const_iterator jt = g_tbPost.begin(); jt != g_tbPost.end(); ++jt)
    {
        if (g_tbPre.find(jt->first) != g_tbPre.end()) continue;
        added++;
        CStringA s;
        s.Format("%s +%s = %s", phase, jt->first.GetString(), jt->second.GetString());
        AppendLog("TB", "diff-item", s);
    }
    g_tbChanged = changed + added;

    CStringA head;
    head.Format("%s pre=%d post=%d changed=%d added=%d", phase,
                (int)g_tbPre.size(), (int)g_tbPost.size(), changed, added);
    AppendLog("TB", "diff", head);
}

// 标题栏块参照的实体名（crx_name），用于把标题栏作为 RTENAME 参数交给命令
static bool GetTitleBlkAdsName(crx_name nm)
{
    // 注意：crxdbGetAdsName 的形参是 crx_name&（数组引用），不能用退化成指针的形参直接传，
    //       必须在本函数内声明局部 crx_name 再拷贝出来（否则 C2664）。
    nm[0] = 0; nm[1] = 0;
    CRxDbObjectId tbId = GetCrxDbTitleBlk(NULL);
    if (tbId.isNull()) return false;

    crx_name tmp;
    tmp[0] = 0; tmp[1] = 0;
    if (crxdbGetAdsName(tmp, tbId) != CDraft::eOk) return false;
    nm[0] = tmp[0]; nm[1] = tmp[1];
    return true;
}

// 把标题栏实体放进一个临时选择集——这是“程序化预选标题栏”在本 SDK 下唯一可行的做法
// （crxedSSSetFirst 只有声明、无导出，无法设置 pickfirst），再把集合名作为命令的
// RTENAME 输入参数。集合必须活到命令真正执行，所以由调用方在下一次尝试前释放。
static bool BuildTitleBlkSet(crx_name ssOut)
{
    ssOut[0] = 0; ssOut[1] = 0;
    crx_name nm;
    if (!GetTitleBlkAdsName(nm))
    {
        AppendLog("TB", "tmp-set-noent", "GetCrxDbTitleBlk/crxdbGetAdsName 失败");
        return false;
    }
    int st = crxedSSAdd(nm, NULL, ssOut);
    long len = 0;
    crxedSSLength(ssOut, &len);
    CStringA s;
    s.Format("crxedSSAdd ret=%d len=%ld", st, len);
    AppendLog("TB", "tmp-set", s);
    return len > 0;
}

// 零风险探针：只查这些未公开导出在不在，绝不调用（首参语义未实测，调用有崩溃风险）
static void Probe3DNameApi()
{
    HMODULE hEd = GetModuleHandleW(L"CrxEdApi.dll");
    const char* names[] = { "crxedGet3DFullName", "crxedSet3DFullName",
                            "crxedForceCommit", "crxedThreadCommit" };
    for (int i = 0; i < 4; i++)
    {
        void* p = hEd ? (void*)GetProcAddress(hEd, names[i]) : NULL;
        CStringA s;
        s.Format("%s %s", names[i], p ? "present" : "absent");
        AppendLog("REQ", "3dapi", s);
    }
    HMODULE hIo = GetModuleHandleW(L"App3DIO.dll");
    const char* ios[] = { "App3DIORenameLinkedIcsFile", "App3DIOUpdateViews",
                          "App3DIOGetCustomTemplatePath" };
    for (int i = 0; i < 3; i++)
    {
        void* p = hIo ? (void*)GetProcAddress(hIo, ios[i]) : NULL;
        CStringA s;
        s.Format("App3DIO loaded=%d %s %s", hIo ? 1 : 0, ios[i], p ? "present" : "absent");
        AppendLog("REQ", "3dapi", s);
    }
}

//-----------------------------------------------------------------------------
// P0 探针①：命令表探针（只读，零副作用）
//
// 目的：一次性回答「CX_DFT_TITLEBLOCK_3DSET 到底在不在 CRX 命令表里」。
// 背景：crxedCommand 对命令表里不存在的名字是「静默成功」（ret=RTNORM、无事件、无报错），
//       所以只看返回值永远分不清「名字不在表」与「调用上下文不合法」。
//       lookupGlobalCmd / lookupLocalCmd 是 crx 命令表自身的查询接口（crxcmd.h:133/134），
//       返回 NULL 即证明该名字根本不在表里。顺带记录 functionAddr（有地址说明可直调）。
//-----------------------------------------------------------------------------
static void ProbeCommandTable()
{
    if (!crxedRegCmds)
    {
        AppendLog("PROBE", "cmdtbl", "crxedRegCmds=NULL（命令表不可用）");
        return;
    }

    // 对照组的选取原则：
    //   CX_DFT_View_Zoom_All / CX_DFT_ViewUpdate —— 图纸环境里能由 UI 正常触发的命令（对照）
    //   CX_DFT_TITLEBLOCK_3DSET                  —— 本次目标
    //   CX_save / CX_open_Impl                   —— 已知在 Commands.xml 里但（兄弟工程实证）不可直调
    //   CX_DFT_ChangeLinks / EditLinks           —— 「改变链接文件」（功能区「三维接口→注释」）：
    //       名字出自 CAXADraft\Config\zh-CN\{ICIntegrate,Mechanical}\Mdl3DIOCmd.xml（2024/2027 同名，
    //       CMDPRIORITY_MODAL，CLSID {4BF2824F-2C96-4DB1-82B3-22D17D2913CF} = App3DIO.dll 的
    //       CxChangeLinks coclass）；EditLinks 是 Mdl3DIOUI.xml <Ins> 里的键入别名。
    const wchar_t* names[] =
    {
        L"CX_DFT_TITLEBLOCK_3DSET",
        L"CX_DFT_View_Zoom_All",
        L"CX_DFT_ViewUpdate",
        L"CX_DFT_default",
        L"CX_save",
        L"CX_save_Impl",
        L"CX_open_Impl",
        L"设置3D信息",
        L"3DSET",
        L"CX_DFT_ChangeLinks",
        L"EditLinks",
    };
    const int n = (int)(sizeof(names) / sizeof(names[0]));

    for (int i = 0; i < n; i++)
    {
        CRxEdCommand* pGlobal = crxedRegCmds->lookupGlobalCmd(names[i]);
        CRxEdCommand* pLocal  = crxedRegCmds->lookupLocalCmd(names[i]);

        // 只有拿到命令对象才能问 functionAddr（NULL 时调用即崩，必须先判空）
        void* pGlobalFcn = NULL;
        void* pLocalFcn  = NULL;
        if (pGlobal) pGlobalFcn = (void*)pGlobal->functionAddr();
        if (pLocal)  pLocalFcn  = (void*)pLocal->functionAddr();

        CStringA s;
        s.Format("%s global=%s(addr=%p) local=%s(addr=%p)",
                 WideToUtf8(names[i]).GetString(),
                 pGlobal ? "HIT" : "null", pGlobalFcn,
                 pLocal  ? "HIT" : "null", pLocalFcn);
        AppendLog("PROBE", "cmdtbl", s);
    }
    AppendLog("PROBE", "cmdtbl", "查表结束（global=HIT 才是可直调的命令名）");
}

//-----------------------------------------------------------------------------
// P0 探针②：键入通道（sendStringToExecute 直投）
//
// 这是本项目此前**从未真正执行过**的通道：旧代码把 sendStringToExecute 包在
// executeInApplicationContext 回调里，而图纸环境的空闲态本身就是一个命令
// （CX_DFT_default 长期持有命令上下文），应用上下文永远回不来，回调一次都没进
// （历史日志里 REQ.tb-appctx-sent 零命中）。
// 现改为在定时器里直接投递，与兄弟工程 caxa3dexbtest 的实证写法一致：
//   sendStringToExecute(pDoc, L"命令名 ", bActivate=true, bWrapUpInactiveDoc=false, bEchoString=true)
// 返回值与 inputPending（待处理字符数）都记日志：只有这对数字能证明“投进去了”。
//-----------------------------------------------------------------------------
static void SendStringDirect(const CStringW& text, const char* tag)
{
    if (!crxDocManager)
    { AppendLog("REQ", "sendstr", "crxDocManager=NULL"); return; }

    CRxApDocument* pDoc = crxDocManager->mdiActiveDocument();
    if (!pDoc)
    { AppendLog("REQ", "sendstr", "mdiActiveDocument=NULL"); return; }

    // 命令名后必须跟一个空格，等价用户敲完命令后回车前的分隔
    CStringW s = text; s.Trim();
    if (s.IsEmpty()) { AppendLog("REQ", "sendstr", "空文本"); return; }
    s += L" ";

    int pendBefore = crxDocManager->inputPending(pDoc);
    CDraft::ErrorStatus es = crxDocManager->sendStringToExecute(pDoc, s, true, false, true);
    int pendAfter = crxDocManager->inputPending(pDoc);

    CStringA a;
    a.Format("%s send=%d pending %d->%d text=%s", tag, (int)es, pendBefore, pendAfter,
             WideToUtf8(s.GetString()).GetString());
    AppendLog("REQ", "sendstr", a);
}

// 快照标题栏信息集合（只读）：属性数 + 每条 name = value
static void DumpTitleInfo(const char* phase)
{
    CRxDbPaperTable* pTable = NULL; CRxDbPaperTableRecord* pPaper = NULL;
    if (!OpenCurrentPaper(CRxDb::kForRead, pTable, pPaper))
    { AppendLog("TB", "info-skip", CStringA(phase)); return; }

    CRxAttCollInfoObject* pInfo = NULL;
    pPaper->getTitleInfo(&pInfo);
    int n = pInfo ? pInfo->getAttributesCount() : 0;

    CStringA head;
    head.Format("%s attrs=%d", phase, n);
    AppendLog("TB", "info", head);

    for (int i = 0; i < n; i++)
    {
        CxCHAR* pName = NULL;
        pInfo->getAttributeName(pName, i);
        if (!pName) continue;
        CxCHAR* pVal = NULL;
        pInfo->getAttributeValue(pVal, pName);
        CStringA s;
        s.Format("[%d] %s = %s", i, CxCharToMbs(pName).GetString(),
                 pVal ? CxCharToMbs(pVal).GetString() : "<null>");
        AppendLog("TB", "attr", s);
    }

    if (pInfo) pPaper->ReleaseAttCollInfoObject(pInfo);
    CloseCurrentPaper(pTable, pPaper);
}

// 直写标题栏属性（免对话框）：按属性名逐个比对，命中后 setAttributeValue + setTitleInfo 提交
static bool SetTitleInfoByName(const CxCHAR* pAttrName, const CxCHAR* pValue)
{
    if (!pAttrName || !pValue) return false;
    CRxDbPaperTable* pTable = NULL; CRxDbPaperTableRecord* pPaper = NULL;
    if (!OpenCurrentPaper(CRxDb::kForWrite, pTable, pPaper))
    { AppendLog("TB", "set-open-failed", ""); return false; }

    CRxAttCollInfoObject* pInfo = NULL;
    pPaper->getTitleInfo(&pInfo);
    int n = pInfo ? pInfo->getAttributesCount() : 0;

    bool found = false;
    for (int i = 0; i < n; i++)
    {
        CxCHAR* pName = NULL;
        pInfo->getAttributeName(pName, i);
        if (!pName) continue;
        if (_wcsicmp(pName, pAttrName) != 0) continue;

        CxCHAR* pOld = NULL;
        pInfo->getAttributeValue(pOld, pName);
        pInfo->setAttributeValue(pValue, i);
        CStringA s;
        s.Format("%s: %s -> %s (index=%d)", CxCharToMbs(pAttrName).GetString(),
                 pOld ? CxCharToMbs(pOld).GetString() : "<null>",
                 CxCharToMbs(pValue).GetString(), i);
        AppendLog("TB", "set-attr", s);
        found = true;
        break;
    }

    CDraft::ErrorStatus es = CDraft::eOk;
    if (found) es = pPaper->setTitleInfo(pInfo);
    if (pInfo) pPaper->ReleaseAttCollInfoObject(pInfo);

    CStringA s2;
    s2.Format("found=%d setTitleInfo-status=%d", found ? 1 : 0, (int)es);
    AppendLog("TB", "set-result", s2);
    CloseCurrentPaper(pTable, pPaper);
    return found;
}

//-----------------------------------------------------------------------------
// 批量直写标题栏属性（数据直写主通道）
//
// 依据（2026-10-26 对比样本实证）：手工执行「选中标题栏-右键-设置3D信息-
// 配置选缺省(默认)-自定义设置勾选默认-确定-保存」后，exb 内**只有两处标题栏
// 属性值发生变化**：
//     名称：折弯板123123123123123  →  折弯板
//     重量：0.00                  →  9.39   （Blk 流里同时存 9.388559 的原始值）
// 也就是说「设置 3D 信息」并非写入某个开关/标志，而是用 3D 模型的默认配置
// **刷新标题栏里由 3D 驱动的属性值**。因此可以完全绕开命令队列与模态对话框：
// 把这些属性值直接写进标题栏信息集合（getTitleInfo → setAttributeValue →
// setTitleInfo），由 CAXA 自己保存落盘，两条流（SpaceStream / *Blk_fffffffb）
// 自然保持同步。
//
// 与 TB_SET 的区别：一次打开图纸记录、批量设置、一次提交，并做前后快照 diff，
// 回 REQ.tb-setmany-result（changed 数即判定依据）。
//-----------------------------------------------------------------------------
static bool SetTitleInfoMany(const std::vector<std::pair<CStringW, CStringW> >& kv)
{
    if (kv.empty()) { AppendLog("TB", "setmany-empty", ""); return false; }

    // 前快照：判定“到底写进去没有”的基线
    g_tbPre.clear();
    if (!ReadTitleInfo(g_tbPre))
        AppendLog("TB", "setmany-pre-empty", "前快照为空（标题栏信息集合不可读）");

    CRxDbPaperTable* pTable = NULL; CRxDbPaperTableRecord* pPaper = NULL;
    if (!OpenCurrentPaper(CRxDb::kForWrite, pTable, pPaper))
    { AppendLog("TB", "setmany-open-failed", ""); return false; }

    CRxAttCollInfoObject* pInfo = NULL;
    pPaper->getTitleInfo(&pInfo);
    int n = pInfo ? pInfo->getAttributesCount() : 0;

    int setCount = 0, missing = 0;
    for (size_t k = 0; k < kv.size(); k++)
    {
        const CxCHAR* pAttr = kv[k].first.GetString();
        const CxCHAR* pVal  = kv[k].second.GetString();
        bool found = false;
        for (int i = 0; i < n && pInfo; i++)
        {
            CxCHAR* pName = NULL;
            pInfo->getAttributeName(pName, i);
            if (!pName) continue;
            if (_wcsicmp(pName, pAttr) != 0) continue;

            CxCHAR* pOld = NULL;
            pInfo->getAttributeValue(pOld, pName);
            pInfo->setAttributeValue(pVal, i);
            CStringA s;
            s.Format("%s: %s -> %s (index=%d)",
                     CxCharToMbs(pAttr).GetString(),
                     pOld ? CxCharToMbs(pOld).GetString() : "<null>",
                     CxCharToMbs(pVal).GetString(), i);
            AppendLog("TB", "setmany-item", s);
            found = true; setCount++;
            break;
        }
        if (!found)
        {
            missing++;
            AppendLog("TB", "setmany-missing", CxCharToMbs(pAttr));
        }
    }

    CDraft::ErrorStatus es = CDraft::eOk;
    if (setCount > 0) es = pPaper->setTitleInfo(pInfo);
    if (pInfo) pPaper->ReleaseAttCollInfoObject(pInfo);
    CloseCurrentPaper(pTable, pPaper);

    // 后快照 + diff：唯一成功判据是集合里真的变了（不看任何返回值）
    g_tbPost.clear();
    ReadTitleInfo(g_tbPost);
    DiffTitleInfo("setmany");

    CStringA r;
    r.Format("requested=%d set=%d missing=%d setTitleInfo=%d changed=%d",
             (int)kv.size(), setCount, missing, (int)es, g_tbChanged);
    AppendLog("REQ", "tb-setmany-result", r);
    return setCount > 0;
}

//-----------------------------------------------------------------------------
// watcher 定时器：只负责「把请求文件转成一次命令派发 + 读写标题栏」。
// 本回调跑在 CAXA UI 线程的 WM_TIMER 里，但上下文可能是 CX_DFT_default（空闲态命令）——
// 所以这里绝不直接 sendStringToExecute；需要键入通道时先 executeInApplicationContext 推到应用上下文。
//-----------------------------------------------------------------------------
enum { kPendingNone = 0, kPendingSave = 1, kPendingClose = 2, kPendingTitleBlock = 3 };
static volatile int  g_pendingKind = kPendingNone;
static volatile bool g_saveEnded   = false;
static volatile bool g_closeEnded  = false;
static volatile bool g_tbEnded     = false;
static bool          g_inTimer     = false;

// 编辑器忙闲态（由 CRxEditorReactor 维护）：CX_DFT_default 运行期间编辑器“忙”，
// 此时 crxedCommand 会被静默丢弃（ret=0 但命令不执行——第二次故障取证结论）。
// 注意：CX_DFT_default 既是图纸环境的“空闲态”命令，也可能是打开图纸后长达数分钟的
// “3D 视图数据源更新”，所以下面这个标志只作诊断记录，不能用来判断“能否派发”。
static volatile bool g_defaultActive = false;   // 最后一次 CX_DFT_default 事件是否为 commandWillStart

// 标题栏派发：4 条通道轮换，直到「标题栏信息集合真的发生变化」为止。
//   通道 0：crxedCommand(RTSTR 命令名, RTENAME 标题栏实体名, RTNONE) —— 等价“选中标题栏-右键-设置3d信息”
//   通道 1：通道 0 + RTSTR "default"                                —— 试“配置选 default”是否为命令输入
//   通道 2：crxedCommand(RTSTR 命令名, RTNONE)                      —— 裸命令名兜底
//   通道 3：executeInApplicationContext → sendStringToExecute        —— 注册命令的合规键入通道
// 判定：命令跑完（commandEnded/Cancelled/Failed）后比对标题栏信息快照（TB.diff），
//       有变化即成功；全部通道试完仍无变化 → 判失败。
// 依据：crxedCommand 在编辑器忙时会静默丢弃（ret=0 且无 commandWillStart），返回值不可信。
static volatile bool  g_tbWanted      = false;   // 已收到请求、尚未确认成功
static volatile bool  g_tbTrying      = false;   // 已投递一次派发、正等本轮结果
static volatile bool  g_tbRunActive   = false;   // 本轮派发的命令真的开始跑了（willStart 置位）
static DWORD          g_tbLastTry     = 0;       // 上次派发尝试的 tick
static DWORD          g_tbQueuedAt    = 0;       // 收到请求的 tick（总截止计时起点）
static volatile int   g_tbTries       = 0;       // 已尝试次数（供通道轮换与诊断）
static volatile int   g_tbRuns        = 0;       // 命令真正跑起来的轮数（诊断）
static volatile int   g_tbChan        = 0;       // 最近一次尝试使用的通道号（诊断）
static crx_name       g_tbSet;                   // 上一次造的临时选择集（下次尝试前释放）
static volatile bool  g_tbSetValid    = false;
// 时序预算（关键）：
//   编辑器忙时（CX_DFT_default 可能是长达 156s 的“3D 视图数据源更新”）crxedCommand 会被静默丢弃，
//   所以「失败」绝不能按尝试次数判、必须按时间判——否则忙碌期 12 次重试会在 20 秒内烧完并误报失败。
//   kTbRetryMs      两次尝试间隔；命令没跑起来就按这个节奏继续重投，不计入失败。
//   kTbRunTimeoutMs 单轮命令最长等待：本来就 2 秒左右的命令跑这么久还没结束，视为异常强制收尾。
//   kTbDeadlineMs   总截止：从入队起算的整体上限（本插件要求兜底延迟不超过 10s），
//                   与 C# 端 10s 等待对齐。
//   kTbMaxRuns      真正跑起来（且无变化）的轮数上限，即 4 通道 × 6 轮。
static const DWORD    kTbRetryMs      = 1000;    // 兜底通道（crxedCommand）之间的间隔
// 提速（2026-10-26）：chan3（键入通道）是唯一能真正把命令跑起来的通道，正常 100ms 内就起来；
// 三条 crxedCommand 通道已实证全废（runs=0），只当兜底。故 chan3 失败后的重投间隔单独取值，
// 且不小于 1.5s —— 避免同一条命令被投两次、弹出两个对话框。
static const DWORD    kTbRetryFastMs  = 1200;    // 键入通道失败后的重投间隔
static const DWORD    kTbRunTimeoutMs = 5000;   // 单轮命令最长等待（毫秒）
static const DWORD    kTbDeadlineMs   = 10000;  // 总截止（毫秒）
static const int      kTbMaxRuns      = 6;      // 真正跑起来的轮数上限

// 应用上下文回调：已废止（保留说明，勿再启用）
// 历史实现把 sendStringToExecute 包在 crxDocManager->executeInApplicationContext() 里，
// 期望“命令都结束后”再投递。实证：图纸环境的空闲态本身就是一个命令
// （CX_DFT_default 长期持有命令上下文），应用上下文永远回不来，回调 0 次命中
// （所有历史 CRX 日志里 REQ.tb-appctx-sent 均为 0 条）。故改为 SendStringDirect 直投。

static void RefreshTickBody()
{
    CStringW raw;
    if (ReadRequestFile(raw))
    {
        // 请求末尾带 #seq=N：C# 靠 REQ.ack 确认"这条请求真的被 CRX 取走了"。
        int seq = 0;
        int sq = raw.Find(L"#seq=");
        if (sq >= 0)
        {
            seq = (int)wcstol(raw.Mid(sq + 5), NULL, 10);
            raw = raw.Left(sq);
        }

        CStringA cmd = CxCharToMbs(raw);
        AppendLog("REQ", "received", cmd);

        if (cmd.Find("CMD:PING") >= 0)
        {
            // 就绪探针：C# 在打开图纸前先投一条 PING，CRX 一旦加载就会取走并回 ack，
            // C# 据此确认"图纸环境里的桥真的活了"（比 marker 文件时间可靠）。
            AppendLog("REQ", "pong", "crx alive");
        }
        else if (cmd.Find("CMD:PROBE") >= 0)
        {
            ProbeEntityHistogram();
        }
        else if (cmd.Find("CMD:TBINFO") >= 0)
        {
            // 只读快照：标题栏块名 + 当前图纸的标题栏信息集合（属性名/值）
            LogTitleBlkName("probe");
            DumpTitleInfo("probe");
        }
        else if (cmd.Find("CMD:TB_SETMANY|") >= 0)
        {
            // 数据直写主通道：CMD:TB_SETMANY|属性名=值|属性名=值
            // 例：CMD:TB_SETMANY|名称=折弯板|重量=9.39
            CStringW rest = raw.Mid(raw.Find(L'|') + 1);
            std::vector<std::pair<CStringW, CStringW> > kv;
            int pos = 0;
            while (pos <= rest.GetLength())
            {
                int bar = rest.Find(L'|', pos);
                CStringW item = (bar < 0) ? rest.Mid(pos) : rest.Mid(pos, bar - pos);
                item.Trim();
                int eq = item.Find(L'=');
                if (eq > 0)
                {
                    CStringW k = item.Left(eq); k.Trim();
                    CStringW v = item.Mid(eq + 1); v.Trim();
                    if (!k.IsEmpty()) kv.push_back(std::make_pair(k, v));
                }
                if (bar < 0) break;
                pos = bar + 1;
            }
            CStringA cnt; cnt.Format("%d 项", (int)kv.size());
            AppendLog("REQ", "tb-setmany-req", cnt);
            SetTitleInfoMany(kv);
        }
        else if (cmd.Find("CMD:CMDPROBE") >= 0)
        {
            // P0 探针：命令表查名（只读，不派发、不弹框）
            ProbeCommandTable();
        }
        else if (cmd.Find("CMD:SENDSTR|") >= 0)
        {
            // P0 探针：键入通道直投任意文本（会真的执行，慎用）
            CStringW text = raw.Mid(raw.Find(L'|') + 1);
            SendStringDirect(text, "manual");
        }
        else if (cmd.Find("CMD:TB_SET|") >= 0)
        {
            // 直写兜底：CMD:TB_SET|属性名|值（免对话框）
            CStringW rest = raw.Mid(raw.Find(L'|') + 1);
            CStringW nameW = rest, valW;
            int p = rest.Find(L'|');
            if (p >= 0) { nameW = rest.Left(p); valW = rest.Mid(p + 1); }
            nameW.Trim(); valW.Trim();

            bool ok = SetTitleInfoByName(nameW.GetString(), valW.GetString());
            CStringA s;
            s.Format("%s=%s ok=%d", WideToUtf8(nameW.GetString()).GetString(),
                     WideToUtf8(valW.GetString()).GetString(), ok ? 1 : 0);
            AppendLog("REQ", "tb-set", s);
        }
        else if (cmd.Find("CMD:TITLEBLOCK_3DSET_TRY") >= 0)
        {
            // 实测用：带一个字符串输入派发（试"配置名/default"是否需要交互输入）
            CStringW arg;
            int p = raw.Find(L'|');
            if (p >= 0) { arg = raw.Mid(p + 1); arg.Trim(); }

            ReportSelection("before-tb");
            g_pendingKind = kPendingTitleBlock;
            g_tbEnded     = false;

            int st;
            if (arg.IsEmpty())
                st = crxedCommand(RTSTR, L"CX_DFT_TITLEBLOCK_3DSET", RTNONE);
            else
                st = crxedCommand(RTSTR, L"CX_DFT_TITLEBLOCK_3DSET", RTSTR,
                                  arg.GetString(), RTNONE);
            CStringA s;
            s.Format("TRY CX_DFT_TITLEBLOCK_3DSET ret=%d arg=%s", st,
                     arg.IsEmpty() ? "<none>" : CxCharToMbs(arg.GetString()).GetString());
            AppendLog("REQ", "tb-try-dispatched", s);
        }
        else if (cmd.Find("CMD:TB_ABORT") >= 0)
        {
            // 用户点「停止」：立即终止标题栏 3D 信息的轮换派发与判定。
            // 注意：这里只停本插件的派发与判定，不强行关图/保存（避免二次卡死）；
            // 若本轮命令确实还在跑，其后续 commandEnded 也因 g_pendingKind 已复位而不再被当成本轮结果。
            CStringA stat;
            stat.Format("chan=%d try=%d runs=%d", g_tbChan, g_tbTries, g_tbRuns);
            g_tbWanted    = false;
            g_tbTrying    = false;
            g_tbRunActive = false;
            g_tbEnded     = false;
            if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
            if (g_pendingKind == kPendingTitleBlock) g_pendingKind = kPendingNone;
            AppendLog("REQ", "tb-aborted",
                      "收到停止请求 -> 已终止标题栏 3D 信息派发与判定（不保存、不关图）");
            AppendLog("REQ", "tb-aborted-stat", stat);
        }
        else if (cmd.Find("CMD:TITLEBLOCK_3DSET") >= 0)
        {
            // 标题栏 3D 信息设置：等价"选中标题栏-右键-设置3d信息-配置选 default-确定"。
            // 派发与判定全部交给 RefreshTick：4 条通道轮换，直到标题栏信息集合真的变化。
            if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
            g_tbWanted        = true;
            g_tbTrying        = false;   // 允许本轮 RefreshTick 立刻尝试
            g_tbRunActive     = false;
            g_tbTries         = 0;
            g_tbRuns          = 0;
            g_tbChan          = -1;
            g_tbChanged       = 0;
            g_pendingKind     = kPendingTitleBlock;
            g_tbEnded         = false;
            g_tbLastTry       = 0;
            g_tbQueuedAt      = GetTickCount();
            AppendLog("REQ", "tb-queued",
                      "标题栏 3D 信息设置已入队：4 通道轮换，以标题栏信息变化为成功判据");
        }
        else if (cmd.Find("CMD:3DAPI") >= 0)
        {
            // 零风险探针：只查 3D 信息相关导出是否存在（供后续是否直调 crxedSet3DFullName 决策）
            Probe3DNameApi();
        }
        else if (cmd.Find("CMD:SAVE") >= 0)
        {
            g_pendingKind = kPendingSave;
            g_saveEnded   = false;
            int st = crxedCommand(RTSTR, L"CX_save", RTNONE);
            CStringA s; s.Format("CX_save ret=%d (waiting CX_DFT_ViewSave)", st);
            AppendLog("REQ", "save-dispatched", s);
        }
        else if (cmd.Find("CMD:CLOSEDOC") >= 0)
        {
            // 原生关闭：先 Ctrl+S 同路保存（同步落盘）→ 再投 WM_CLOSE
            CStringW fileName;
            int p = raw.Find(L'|');
            if (p >= 0) fileName = raw.Mid(p + 1);
            fileName.Trim();

            if (fileName.IsEmpty())
            {
                // 空文件名 = 「保存并关闭全部」，会把 3D 设计环境一起关掉，一律拒绝。
                AppendLog("REQ", "closedoc-rejected",
                          "CMD:CLOSEDOC 没带文件名，拒绝执行（关闭范围无法限定到单张图纸）");
                return;
            }

            g_pendingKind = kPendingClose;
            g_closeEnded  = false;

            // 关图前先彻底停掉标题栏轮换：否则残留通道会在文档关闭后继续派发，
            // 把命令排到"下一个打开的文档"上抢跑（08:24 那次就是这样毁掉后两次尝试的）。
            if (g_tbWanted || g_tbRunActive)
            {
                g_tbWanted    = false;
                g_tbTrying    = false;
                g_tbRunActive = false;
                if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
                AppendLog("REQ", "tb-stopped-on-close", "关闭文档前停止标题栏轮换");
            }

            LogDocModified(fileName);
            SaveDocByCommand(fileName);
            int n = CloseDocWindows(fileName);
            CStringA s;
            s.Format("WM_CLOSE 投递给 %d 个文档窗口 file=%s", n,
                     fileName.IsEmpty() ? "<any>" : WideToUtf8(fileName).GetString());
            AppendLog("REQ", "closedoc-dispatched", s);
        }
        else if (cmd.Find("CMD:CLOSERAW") >= 0)
        {
            // 直接关闭（不存盘）：预热载体图纸没动过，CLOSEDOC 的 CX_save 链对它是多余动作；
            // 窗口匹配/铁律与 CLOSEDOC 完全同一套（只关图纸视图子窗口，绝不碰顶层主框架）。
            CStringW fileName;
            int p = raw.Find(L'|');
            if (p >= 0) fileName = raw.Mid(p + 1);
            fileName.Trim();

            if (fileName.IsEmpty())
            {
                AppendLog("REQ", "closeraw-rejected",
                          "CMD:CLOSERAW 没带文件名，拒绝执行（关闭范围无法限定到单张图纸）");
                return;
            }

            g_pendingKind = kPendingClose;
            g_closeEnded  = false;

            // 关图前先彻底停掉标题栏轮换（与 CLOSEDOC 同理：防残留在下一个文档上抢跑）
            if (g_tbWanted || g_tbRunActive)
            {
                g_tbWanted    = false;
                g_tbTrying    = false;
                g_tbRunActive = false;
                if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
                AppendLog("REQ", "tb-stopped-on-close", "关闭文档前停止标题栏轮换");
            }

            LogDocModified(fileName);
            int n = CloseDocWindows(fileName);
            CStringA s;
            s.Format("WM_CLOSE 投递给 %d 个文档窗口（不保存） file=%s", n,
                     WideToUtf8(fileName).GetString());
            AppendLog("REQ", "closeraw-dispatched", s);
        }
        else if (cmd.Find("CMD:CLOSE") >= 0)
        {
            g_pendingKind = kPendingClose;
            g_closeEnded = false;
            int st = crxedCommand(RTSTR, L"CX_DFT_ViewClose", RTNONE);
            CStringA s; s.Format("CX_DFT_ViewClose ret=%d", st);
            AppendLog("REQ", "close-dispatched", s);
        }
        else if (cmd.Find("CMD:EXEC|") >= 0)
        {
            // 实测用：按"命令名"直调，方便在不重编译的前提下验证某个命令名是否有效
            CStringW name = raw.Mid(raw.Find(L'|') + 1);
            name.Trim();
            int st = crxedCommand(RTSTR, name.GetString(), RTNONE);
            CStringA s; s.Format("%s ret=%d", WideToUtf8(name).GetString(), st);
            AppendLog("REQ", "exec-dispatched", s);
        }
        else if (cmd.Find("CMD:DUMPWND") >= 0)
        {
            DumpWindowTree("on-request");
        }
        else
        {
            AppendLog("REQ", "unknown", cmd);
        }

        // 回执：表示这条请求已被 CRX 消费（不管执行结果如何）
        CStringA ack;
        ack.Format("seq=%d cmd=%s", seq, cmd.GetString());
        AppendLog("REQ", "ack", ack);
    }

    if (g_saveEnded)
    {
        g_saveEnded   = false;
        g_pendingKind = kPendingNone;
        AppendLog("REQ", "save-done", "CX_DFT_ViewSave ended");
    }
    if (g_closeEnded)
    {
        g_closeEnded  = false;
        g_pendingKind = kPendingNone;
        AppendLog("REQ", "close-done", "CX_DFT_ViewClose ended");
    }
    // ① 单轮看门狗：命令跑起来却迟迟不结束（异常 / 被未识别弹窗堵住）→ 强制收尾，
    //    让通道轮换继续推进，避免 C# 端干等到 300s 超时。
    //    例外：「设置3D信息」对话框开着时命令**本来就不会结束**（模态等用户操作），
    //    此时既不判超时、也不重复派发，等 C# 侧把对话框填完点确定。
    HWND tbDlg = FindTitleBlock3DDlg();
    static bool s_dlgLogged = false;
    if (tbDlg && g_tbWanted)
    {
        if (!s_dlgLogged)
        {
            s_dlgLogged = true;
            CStringA s;
            s.Format("检测到「设置3D信息」弹窗 hwnd=%p：暂停单轮看门狗与重复派发，"
                     "等对话框被确认后按 TB.diff 判定", (void*)tbDlg);
            AppendLog("REQ", "tb-dialog", s);
        }
    }
    else if (!tbDlg) s_dlgLogged = false;

    if (g_tbRunActive && g_tbWanted && !tbDlg
        && (GetTickCount() - g_tbLastTry) >= kTbRunTimeoutMs)
    {
        g_tbRunActive = false;
        g_tbEnded     = true;
        AppendLog("REQ", "tb-run-timeout", "命令长时间未结束 -> 强制结束本轮并做信息比对");
    }

    // ② 本轮标题栏命令已跑完 → 用「标题栏信息集合是否真的变化」判定成功
    //    （不能拿 commandWillStart 当成功：CAXA 每次打开图纸都会自己跑一次该命令）
    if (g_tbEnded)
    {
        g_tbEnded     = false;
        g_tbRunActive = false;
        if (!g_tbWanted)
        {
            // 打开图纸时 CAXA 自己也会跑一次该命令（且不产生实体变化）→ 不能当成本轮结果
            g_pendingKind = kPendingNone;
            AppendLog("REQ", "tb-stale",
                      "收到 CX_DFT_TITLEBLOCK_3DSET 结束，但当前无待判定请求 -> 仅记录");
        }
        else
        {
            g_pendingKind = kPendingNone;

            LogTitleBlkName("post");
            g_tbPost.clear();
            ReadTitleInfo(g_tbPost);
            DiffTitleInfo("post");
            g_tbRuns++;

            CStringA head;
            head.Format("run=%d try=%d chan=%d pre=%d post=%d changed=%d",
                        g_tbRuns, g_tbTries, g_tbChan,
                        (int)g_tbPre.size(), (int)g_tbPost.size(), g_tbChanged);
            AppendLog("REQ", "tb-done", head);

            // 注意：失败与否按时间判、不按尝试次数判——编辑器忙时命令会被静默丢弃，
            //       固定的 12 次重试会在 20 秒内烧完，从而在 156s 的视图更新期间误报失败。
            bool deadlineHit = ((GetTickCount() - g_tbQueuedAt) >= kTbDeadlineMs);
            if (g_tbChanged > 0)
            {
                g_tbWanted = false;
                g_tbTrying = false;
                AppendLog("REQ", "tb-result",
                          "标题栏信息已变化 -> 判定为设置成功（含 TB.diff 明细）");
            }
            else if (g_tbRuns >= kTbMaxRuns || deadlineHit)
            {
                g_tbWanted = false;
                g_tbTrying = false;
                AppendLog("REQ", "tb-giveup",
                          "4 条通道轮换已试完，标题栏信息无任何变化 -> 判定失败");
            }
            else
            {
                // 命令跑了但标题栏信息没变 → 换下一条通道继续试
                g_tbTrying  = true;
                g_tbLastTry = GetTickCount();
                AppendLog("REQ", "tb-nochange", "命令已执行但标题栏信息无变化 -> 换通道重试");
            }
        }
    }

    // ③ 总截止兜底：编辑器长时间忙（crxedCommand 被静默丢弃）时到此为止，给出明确失败信号
    if (g_tbWanted && !g_tbRunActive && (GetTickCount() - g_tbQueuedAt) >= kTbDeadlineMs)
    {
        g_tbWanted    = false;
        g_tbTrying    = false;
        g_pendingKind = kPendingNone;
        AppendLog("REQ", "tb-giveup",
                  "总截止时间已达（编辑器长时间忙/命令始终未启动）-> 判定失败");
    }

    // ④ 标题栏派发：按通道轮换重试
    //    通道 0/1 需要标题栏实体名（RTENAME）；通道 2 是裸命令名；通道 3 走应用上下文键入。
    //    本回调跑在 CAXA UI 线程的 WM_TIMER 里，crxedCommand 只排队不重入，安全。
    if (g_tbWanted && !g_tbRunActive && !tbDlg)
    {
        DWORD now = GetTickCount();
        // 键入通道（chan3）起来得快、失败也该快重投；crxedCommand 通道只是兜底，慢节奏即可
        DWORD retryMs = (g_tbChan == 3 && g_tbTries <= 3) ? kTbRetryFastMs : kTbRetryMs;
        if (!g_tbTrying || (now - g_tbLastTry) >= retryMs)
        {
            // 释放上一轮的临时选择集（不能更早释放：命令可能还在用它）
            if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }

            bool firstTry = (g_tbTries == 0);
            g_tbTrying  = true;
            g_tbLastTry = now;
            g_tbTries++;

            if (firstTry)
            {
                // 「前」快照 = 成功判定的基线
                ReportSelection("before-tb");
                LogTitleBlkName("pre");
                g_tbPre.clear();
                ReadTitleInfo(g_tbPre);
                g_tbPost.clear();
                g_tbChanged = 0;
                CStringA h;
                h.Format("attrs=%d", (int)g_tbPre.size());
                AppendLog("TB", "pre", h);
            }

            g_pendingKind = kPendingTitleBlock;
            g_tbEnded     = false;

            // 提速（2026-10-26）：实测只有 chan3（键入通道）能把 CX_DFT_TITLEBLOCK_3DSET
            // 真正跑起来（chan1/0/2 的 crxedCommand 每次都 runs=0，白等 2s×3 = 6s）。
            // 所以前 3 次全投 chan3，仍不成功才轮到三条 crxedCommand 兜底。
            static const int kChanOrder[6] = { 3, 3, 3, 1, 0, 2 };
            g_tbChan = kChanOrder[(g_tbTries - 1) % 6];

            bool needSet = (g_tbChan == 0 || g_tbChan == 1);
            bool setOk   = needSet ? BuildTitleBlkSet(g_tbSet) : false;
            if (setOk) g_tbSetValid = true;
            if (needSet && !setOk) g_tbChan = 2;   // 取不到标题栏实体名 → 回退裸命令名

            int st = -1;
            CStringA desc;
            if (g_tbChan == 0)
            {
                st = crxedCommand(RTSTR, L"CX_DFT_TITLEBLOCK_3DSET",
                                  RTENAME, g_tbSet, RTNONE);
                desc = "crxedCommand + RTENAME(标题栏)";
            }
            else if (g_tbChan == 1)
            {
                st = crxedCommand(RTSTR, L"CX_DFT_TITLEBLOCK_3DSET",
                                  RTENAME, g_tbSet, RTSTR, L"default", RTNONE);
                desc = "crxedCommand + RTENAME(标题栏) + \"default\"";
            }
            else if (g_tbChan == 2)
            {
                st = crxedCommand(RTSTR, L"CX_DFT_TITLEBLOCK_3DSET", RTNONE);
                desc = "crxedCommand 裸命令名";
            }
            else
            {
                // P0 修正：**不再**走 executeInApplicationContext。
                // 实证：图纸环境的空闲态本身就是一个命令（CX_DFT_default 长期持有命令上下文），
                // 应用上下文永远回不来，回调 0 次命中（历史日志无 REQ.tb-appctx-sent），
                // 也就是说这条“键入通道”在本项目里从未真正执行过。
                // 现改为定时器里直接投递，写法与兄弟工程 caxa3dexbtest 一致。
                if (crxDocManager)
                {
                    SendStringDirect(CStringW(L"CX_DFT_TITLEBLOCK_3DSET"), "tb-chan3");
                    st = 0;
                    desc = "sendStringToExecute 定时器直投（键入通道）";
                }
                else desc = "无 crxDocManager";
            }

            CStringA s;
            s.Format("chan=%d try=%d runs=%d ret=%d setOk=%d next=%dms %s",
                     g_tbChan, g_tbTries, g_tbRuns, st, setOk ? 1 : 0, (int)retryMs,
                     desc.GetString());
            AppendLog("REQ", "tb-dispatch-try", s);
        }
    }

    // ⑤ 早失败可见化：重投 6 次以上、命令一次都没跑起来 → 8 秒就打一条明确告警，
    //    不再让用户干等到 10s 总截止（旧行为＝界面静默数分钟，体验等同卡死）。
    static bool s_noRunLogged = false;
    if (!g_tbWanted) s_noRunLogged = false;
    if (g_tbWanted && !s_noRunLogged && g_tbRuns == 0 && g_tbTries >= 6
        && (GetTickCount() - g_tbQueuedAt) >= 8000)
    {
        s_noRunLogged = true;
        CStringA s;
        s.Format("tries=%d runs=0 已 8s：命令一次都没跑起来（该命令名在当前上下文无效），"
                 "仍会继续轮换到键入通道并按 TB.diff 判定", g_tbTries);
        AppendLog("REQ", "tb-no-run", s);
    }

    // ⑤' 文档没了就别再轮换：否则命令会被排队到"下一个打开的文档"上抢跑。
    //    实测（08:24:38）：文档刚被关掉，通道轮到 chan=0 去取标题栏实体名失败，
    //    随后 crxedCommand 在无文档状态下把命令排进了队列 —— 08:24:48 文档一重开，
    //    那条命令立刻执行，直接把下一次运行的现场搞乱。
    if (g_tbWanted && (!crxDocManager || crxDocManager->mdiActiveDocument() == NULL))
    {
        g_tbWanted    = false;
        g_tbTrying    = false;
        g_tbRunActive = false;
        g_pendingKind = kPendingNone;
        if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
        AppendLog("REQ", "tb-stop-nodoc",
                  "当前没有活动图纸文档 -> 立即停止标题栏派发（不判失败，交 C# 侧收尾）");
    }

}

// 外层包装：无论 tick 里发生什么，都保证 g_inTimer 被复位。
// 教训（2026-10-26 08:24）：通道轮到 chan=0 时文档恰好被关闭，派发路径抛异常，
// g_inTimer 被卡在 true —— 之后所有 WM_TIMER 一律在函数开头 return，
// 请求文件再也不被读取（日志表现：REQ.received 从此彻底消失，而 reactor 事件仍正常，
// 于是第二、三次点「更新标题栏」全部卡在等 CRX 就绪）。
static DWORD g_inTimerSince = 0;

static void RefreshTick()
{
    if (g_inTimer)
    {
        if (GetTickCount() - g_inTimerSince > 15000)
        {
            AppendLog("REQ", "tick-unstuck", "上一次 tick 卡住超过 15s -> 强制解锁（异常路径未复位）");
            g_inTimer = false;
        }
        else return;
    }

    g_inTimer      = true;
    g_inTimerSince = GetTickCount();
    try
    {
        RefreshTickBody();
    }
    catch (...)
    {
        AppendLog("REQ", "tick-exception", "tick 抛出异常（已捕获，桥继续工作）");
    }
    g_inTimer = false;
}

//-----------------------------------------------------------------------------
// 隐藏 watcher 窗口：定时器在 CAXA 主 UI 线程消息泵里触发，天然满足
// CRX/数据库「必须在主线程调用」的约束，无需自建线程。
//-----------------------------------------------------------------------------
static LRESULT CALLBACK WatcherWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (msg == WM_TIMER && wParam == kWatcherTimerId)
    {
        RefreshTick();
        return 0;
    }
    return DefWindowProcW(hWnd, msg, wParam, lParam);
}

static void CreateWatcher()
{
    if (g_hWatcherWnd) return;

    const wchar_t* clsName = L"Caxa3dCodeNameExtractionWatcher";
    WNDCLASSW wc;
    memset(&wc, 0, sizeof(wc));
    wc.lpfnWndProc   = WatcherWndProc;
    wc.hInstance     = Caxa3dCodeNameExtractionDLL.ModuleResourceInstance();
    wc.lpszClassName = clsName;
    RegisterClassW(&wc);   // 重复注册返回 0，无妨

    g_hWatcherWnd = CreateWindowExW(0, clsName, clsName, 0,
                                    0, 0, 0, 0, HWND_MESSAGE, NULL,
                                    Caxa3dCodeNameExtractionDLL.ModuleResourceInstance(), NULL);
    if (g_hWatcherWnd)
        SetTimer(g_hWatcherWnd, kWatcherTimerId, kWatcherTimerMs, NULL);
}

static void DestroyWatcher()
{
    if (!g_hWatcherWnd) return;
    KillTimer(g_hWatcherWnd, kWatcherTimerId);
    DestroyWindow(g_hWatcherWnd);
    g_hWatcherWnd = NULL;
}

//-----------------------------------------------------------------------------
// 挂 reactor（幂等）
// 注意：CAXA 3D 内嵌图纸环境里 On_kLoadDwgMsg 实际不触发，
//       所以必须在 On_kInitAppMsg 及编辑器回调里补挂。
//-----------------------------------------------------------------------------
static void EnsureReactorsAttached(const char* why)
{
    if (!g_pEditorReactor) return;
    static bool editorAttached = false;
    if (editorAttached) return;

    CRxEditor* pEditor = crxedEditor;
    if (pEditor)
    {
        try {
            pEditor->addReactor(g_pEditorReactor);
            editorAttached = true;
            CStringA a; a.Format("editor @%s", why);
            AppendLog("MOD", "attach", a);
        } catch (...) {
            CStringA a; a.Format("editor FAILED @%s", why);
            AppendLog("MOD", "attach", a);
        }
    }
}

//-----------------------------------------------------------------------------
// CRxEditorReactor —— 命令事件（C# 端据此等待 CX_DFT_TITLEBLOCK_3DSET /
// CX_DFT_ViewClose 等结束）。命令前后做实体快照 diff。
//-----------------------------------------------------------------------------
class CEditorReactor : public CRxEditorReactor
{
public:
    CEditorReactor() {}
    virtual void commandWillStart(const CxCHAR* cmdStr) override
    {
        EnsureReactorsAttached("cmdStart");
        CStringA cmd = CxCharToMbs(cmdStr);
        AppendLog("EDITOR", "commandWillStart", cmd);
        // 忙闲态：CX_DFT_default 开始 = 编辑器忙（新增）
        if (cmdStr && _wcsicmp(cmdStr, L"CX_DFT_default") == 0) g_defaultActive = true;
        // 标题栏命令真的开始跑了：只置「本轮已运行」，绝不能当成功——
        // 成功与否由结束后比对标题栏信息快照判定（TB.diff）；且 CAXA 打开图纸时也会自己跑一次。
        if (cmdStr && _wcsicmp(cmdStr, L"CX_DFT_TITLEBLOCK_3DSET") == 0
            && (g_tbWanted || g_pendingKind == kPendingTitleBlock))
        {
            g_tbRunActive = true;
            CStringA s;
            s.Format("chan=%d try=%d runs=%d", g_tbChan, g_tbTries, g_tbRuns);
            AppendLog("REQ", "tb-run-start", s);
        }
        ReportSelection("start");

        g_snapValid = false;
        if (!SkipSnapshotCmd(cmd))
        {
            CStringA spaceInfo;
            TakeSnapshot(g_snapBefore, g_snapCountBefore, spaceInfo);
            g_snapCmd   = cmd;
            g_snapValid = true;
        }
    }
    virtual void commandEnded(const CxCHAR* cmdStr) override
    {
        CStringA cmd = CxCharToMbs(cmdStr);
        AppendLog("EDITOR", "commandEnded", cmd);
        // 忙闲态：CX_DFT_default 结束 = 编辑器空闲（新增，供延后派发判定）
        if (cmdStr && _wcsicmp(cmdStr, L"CX_DFT_default") == 0) g_defaultActive = false;
        ReportSelection("end");
        if (g_snapValid && cmd == g_snapCmd) ReportSnapshotDelta(cmd, "ended");

        if (!cmdStr) return;
        int pk = g_pendingKind;
        if (_wcsicmp(cmdStr, L"CX_DFT_TITLEBLOCK_3DSET") == 0 && pk == kPendingTitleBlock)
            g_tbEnded = true;
        else if (_wcsicmp(cmdStr, L"CX_DFT_ViewSave") == 0 && pk == kPendingSave)
            g_saveEnded = true;
        else if (_wcsicmp(cmdStr, L"CX_DFT_ViewClose") == 0 && pk == kPendingClose)
            g_closeEnded = true;
    }
    virtual void commandCancelled(const CxCHAR* cmdStr) override
    {
        CStringA cmd = CxCharToMbs(cmdStr);
        AppendLog("EDITOR", "commandCancelled", cmd);
        ReportSelection("cancel");
        if (g_snapValid && cmd == g_snapCmd) ReportSnapshotDelta(cmd, "cancelled");
        // 取消也算一轮结束：交由 RefreshTick 比对标题栏信息后决定“换通道/成功/失败”
        if (cmdStr && _wcsicmp(cmdStr, L"CX_DFT_TITLEBLOCK_3DSET") == 0
            && g_pendingKind == kPendingTitleBlock)
            g_tbEnded = true;
    }
    virtual void pickfirstModified() override
    { AppendLog("EDITOR", "pickfirstModified"); }
    virtual void commandFailed(const CxCHAR* cmdStr) override
    {
        CStringA cmd = CxCharToMbs(cmdStr);
        AppendLog("EDITOR", "commandFailed", cmd);
        if (g_snapValid && cmd == g_snapCmd) ReportSnapshotDelta(cmd, "failed");
        // 失败也算一轮结束（同样交给 RefreshTick 判定：无变化则换下一条通道）
        if (cmdStr && _wcsicmp(cmdStr, L"CX_DFT_TITLEBLOCK_3DSET") == 0
            && g_pendingKind == kPendingTitleBlock)
            g_tbEnded = true;
    }
    virtual void unknownCommand(const CxCHAR* cmdStr, CRxDbVoidPtrArray* al) override
    { AppendLog("EDITOR", "unknownCommand", CxCharToMbs(cmdStr)); }
    virtual void viewChanged() override
    { EnsureReactorsAttached("viewChanged"); }
};

//-----------------------------------------------------------------------------
// 模块入口
//-----------------------------------------------------------------------------
class CCaxa3dCodeNameExtractionApp : public AcRxArxApp
{
public:
    CCaxa3dCodeNameExtractionApp() : AcRxArxApp() {}

    virtual AcRx::AppRetCode On_kInitAppMsg(void* pkt) override
    {
        // 最早期的加载探针：不依赖 MFC / CriticalSection / 任何全局状态。
        {
            wchar_t markPath[MAX_PATH];
            TempPath(markPath, MAX_PATH, L"caxa3dcodenameextraction_crx.marker");
            HANDLE h = CreateFileW(markPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   NULL, CREATE_ALWAYS, 0, NULL);
            if (h != INVALID_HANDLE_VALUE) {
                DWORD w = 0;
                WriteFile(h, "init-app-msg-reached\r\n", 21, &w, NULL);
                CloseHandle(h);
            }
        }

        AcRx::AppRetCode ret = AcRxArxApp::On_kInitAppMsg(pkt);
        InitializeCriticalSection(&g_logCS);
        g_initialized = true;

        // 清空旧日志与可能残留的过期请求（上次会话崩溃遗留的命令不能执行）
        wchar_t path[MAX_PATH];
        TempPath(path, MAX_PATH, L"caxa3dcodenameextraction_crx.log");
        DeleteFileW(path);
        wchar_t reqPath[MAX_PATH];
        TempPath(reqPath, MAX_PATH, L"caxa3dcodenameextraction_request.txt");
        DeleteFileW(reqPath);

        OpenLogFile();
        AppendLog("MOD", "On_kInitAppMsg", "CRX module loaded");

        g_pEditorReactor = new CEditorReactor();
        EnsureReactorsAttached("init");
        CreateWatcher();   // 100ms 轮询请求文件
        return ret;
    }

    virtual AcRx::AppRetCode On_kLoadDwgMsg(void* pkt) override
    {
        AcRx::AppRetCode ret = AcRxArxApp::On_kLoadDwgMsg(pkt);
        EnsureReactorsAttached("loadDwg");
        return ret;
    }

    virtual AcRx::AppRetCode On_kUnloadAppMsg(void* pkt) override
    {
        AppendLog("MOD", "On_kUnloadAppMsg", "CRX module unloading");

        DestroyWatcher();
        delete g_pEditorReactor; g_pEditorReactor = NULL;
        if (g_hLogFile != INVALID_HANDLE_VALUE) { CloseHandle(g_hLogFile); g_hLogFile = INVALID_HANDLE_VALUE; }
        g_initialized = false;
        DeleteCriticalSection(&g_logCS);

        wchar_t markPath[MAX_PATH];
        TempPath(markPath, MAX_PATH, L"caxa3dcodenameextraction_crx.marker");
        DeleteFileW(markPath);
        return AcRxArxApp::On_kUnloadAppMsg(pkt);
    }

    virtual void RegisterServerComponents() override {}
};

IMPLEMENT_ARX_ENTRYPOINT(CCaxa3dCodeNameExtractionApp)