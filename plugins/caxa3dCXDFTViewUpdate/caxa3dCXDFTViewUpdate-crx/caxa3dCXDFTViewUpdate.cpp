// caxa3dCXDFTViewUpdate.cpp — CAXA 3D 2027 图纸环境 CRX 命令执行桥
//
// 作用：
//   1) 隐藏窗口 + 300ms 定时器轮询请求文件 %TEMP%\caxa3d_viewupdate_request.txt，
//      读到命令后用 sendStringToExecute 投递到当前图纸文档执行（C# Add-in 写入）。
//   2) CRxEditorReactor 把 commandWillStart/commandEnded/commandCancelled/commandFailed
//      追加到 %TEMP%\caxa3d_viewupdate_crx.log，C# 端按偏移量读取以等待命令结束。
//
// 日志格式（每行，ASCII / UTF-8）：
//   HH:mm:ss | EDITOR.commandEnded | CX_DFT_ViewUpdate
//   HH:mm:ss | EXEC.request | CX_DFT_ViewUpdate
//   HH:mm:ss | EXEC.fail | reason
//
// 部署：编译输出 .crx → 复制到 CAXADraft\bin，并登记 Modules\CxAutorun.cx。

#include "StdAfx.h"

#include "dbapserv.h"
#include "dbid.h"
#include "dbmain.h"
#include "crxed.h"
#include "crxdocman.h"     // crxDocManager / sendStringToExecute
#include "cdscodes.h"      // RTSTR / RTNONE / RTNORM
#include "crxedcds.h"      // crxedCommand（命令表直调，等价 acedCommand）
#include "crxentrypoint.h"
#include "CrxCusUI.h"      // crxUIManager / 工具栏按钮 / 菜单 / Ribbon

AC_IMPLEMENT_EXTENSION_MODULE(Caxa3dViewUpdateDLL)

//-----------------------------------------------------------------------------
// 共享状态
//-----------------------------------------------------------------------------
static CRxEditorReactor* g_pEditorReactor = NULL;
static CRITICAL_SECTION  g_logCS;
static bool              g_initialized  = false;
static HANDLE            g_hLogFile     = INVALID_HANDLE_VALUE;
static HWND              g_hWatcherWnd  = NULL;   // 隐藏 message-only 窗口
static const UINT_PTR    kWatcherTimerId = 1;
static const UINT        kWatcherTimerMs = 100;   // 100ms：命令取件延迟直接决定批处理单文件耗时

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
    TempPath(path, MAX_PATH, L"caxa3d_viewupdate_crx.log");
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
                      const CStringA& arg1 = CStringA())
{
    if (!g_initialized) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    CStringA timeStr;
    timeStr.Format("%02d:%02d:%02d", st.wHour, st.wMinute, st.wSecond);

    CStringA line;
    line.Format("%s | %s.%s", timeStr, category, eventName);
    if (!arg1.IsEmpty()) { line += CStringA(" | "); line += arg1; }

    EnterCriticalSection(&g_logCS);
    AppendLogBytes(line, line.GetLength());
    LeaveCriticalSection(&g_logCS);
}

//-----------------------------------------------------------------------------
// 工具：CxCHAR (wchar_t) → CStringA（命名避开 ATL 宏 W2A）
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

//-----------------------------------------------------------------------------
// 请求文件 → 命令字符串（UTF-8 → 宽字符，保证末尾有 "\n"）
//-----------------------------------------------------------------------------
static bool ReadRequestFile(CStringW& cmdOut)
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, L"caxa3d_viewupdate_request.txt");

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
// 写请求文件（纯 Win32 调用，不碰任何 CRX API）
// 工具栏按钮命令体只做这一件事，保证任何调用上下文下都不会崩
//-----------------------------------------------------------------------------
static void WriteRequestFile(const char* utf8)
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, L"caxa3d_viewupdate_request.txt");

    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, CREATE_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) { AppendLog("REQ", "write-fail", ""); return; }

    DWORD w = 0;
    WriteFile(h, utf8, (DWORD)strlen(utf8), &w, NULL);
    CloseHandle(h);
}

//-----------------------------------------------------------------------------
// 窗口工具：描述 / 遍历（诊断）/ 原生关闭文档窗口
//
// 2026-10-23 15:23 日志实证：crxedCommand(RTSTR,"CX_save") 返回 0，
// 但 reactor 里连 CX_save_Impl / CX_DFT_ViewSave 都不出现 —— 内部命令名不在
// 命令表里，crxedCommand 是"静默成功但什么都不做"。
// 手工「保存 / 关闭」却能成功，说明唯一可靠路径是让 CAXA 自己走 UI 派发：
// 这里改成向文档窗口 PostMessage(WM_CLOSE)，CAXA 会按原生流程弹
// 「是否保存修改」→ PopupAutoYes 代点「是」→ CAXA 自己执行
// CX_save → CX_save_Impl → CX_DFT_ViewSave 并关闭文档（与手工完全一致）。
// 只用 Win32，不碰任何 CRX API，规避历史上的崩溃点。
//-----------------------------------------------------------------------------
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

// 一次性把本进程的窗口树写进日志（用于搞清 CAXA Draft 2027 的窗口结构：
// 是 MDI（有 MDIClient）还是 Tab 单文档）
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

        // 先记子窗口总数，再截断打印（上一版截断到 20，看不到文档视图窗口）
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

// 专门探测 MDIClient：有它 = MDI 多文档，文档子窗口才能投 WM_CLOSE 关掉
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

// 记录目标文档当前的「已修改」状态（MFC CDocument::IsModified）。
// 用途：C# 据此区分显示「有改动已落盘」/「已保存落盘」——
//   modified=1：打开时的数据源更新/视图更新把图纸改了（真有改动）
//   modified=0：图纸本来就没改动，保存只是强制落盘一次
static void LogDocModified(const CStringW& fileName)
{
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

// 关闭前直接执行一次保存（不走「标脏→弹窗→代点」那套，20:45 实测被否决：
// 无改动文件标脏后 CAXA 依然直接关、没保存）。
// 做法：向文档的 MDI 子窗口 SendMessage(WM_COMMAND, ID_FILE_SAVE=0xE103)。
// 这与手动 Ctrl+S / 保存按钮走的是同一条 MFC 命令路由
// （view → 子框架 → 主框架 → CDocument::OnFileSave），是原生保存链的正门；
// CDocument::OnFileSave 是 protected 不能直调，但命令路由能正常进入。
// SendMessage 同步返回：函数返回时保存已落盘，随后的 WM_CLOSE 直接关、不弹窗。
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
// fileName 非空时优先只关标题里含该文件名的 MDI 子窗口；一个都没匹配上就
// 退化为关闭全部 MDI 子窗口（批处理场景下本进程里只有当前这一个图纸）。
static int CloseDocWindows(const CStringW& fileName)
{
    int closed = 0;
    int skippedNoMdi = 0;

    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId()) continue;
        if (!IsWindowVisible(h)) continue;

        HWND mdi = FindWindowExW(h, NULL, L"MDIClient", NULL);
        if (!mdi)
        {
            // MDIClient 不一定是直接子窗口，深搜一遍
            g_mdiFound = NULL;
            EnumChildWindows(h, FindMdiProc, 0);
            mdi = g_mdiFound;
        }
        if (!mdi)
        {
            skippedNoMdi++;
            // 没有 MDI 结构：只有当这个顶层窗口标题里正好含目标文件名（=它就是当前文档窗口）
            // 才投 WM_CLOSE；否则只记录，不贸然关闭，避免误杀整个图纸进程。
            wchar_t wt[512] = {0};
            GetWindowTextW(h, wt, 512);
            CStringW t(wt);
            if (!fileName.IsEmpty() && t.Find(fileName.GetString()) >= 0)
            {
                AppendLog("CLOSEDOC", "sdi-post", WinDesc(h));
                PostMessageW(h, WM_CLOSE, 0, 0);
                closed++;
            }
            else
            {
                // 其它无 MDI 的顶层窗口（BCGPShadowSideWnd 阴影窗口、本插件自己的
                // WinForms 窗口…）每轮都会有一堆，不再逐个打日志，只在最终汇总里计数。
            }
            continue;
        }

        // 第一轮：按文件名精确匹配
        int matched = 0, all = 0;
        HWND c = GetWindow(mdi, GW_CHILD);
        for (; c; c = GetWindow(c, GW_HWNDNEXT))
        {
            wchar_t title[512] = {0};
            GetWindowTextW(c, title, 512);
            all++;
            CStringW t(title);
            if (fileName.IsEmpty() || t.Find(fileName.GetString()) >= 0)
            {
                AppendLog("CLOSEDOC", "post", WinDesc(c));
                PostMessageW(c, WM_CLOSE, 0, 0);
                closed++; matched++;
            }
        }

        if (matched == 0 && all > 0)
        {
            CStringA s;
            s.Format("文件名未匹配，退化为关闭全部 %d 个 MDI 子窗口", all);
            AppendLog("CLOSEDOC", "fallback-all", s);
            for (c = GetWindow(mdi, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT))
            {
                AppendLog("CLOSEDOC", "post", WinDesc(c));
                PostMessageW(c, WM_CLOSE, 0, 0);
                closed++;
            }
        }
    }

    if (closed == 0)
    {
        CStringA firstTitle;
        int dlg = CountSelfDialogs(firstTitle);
        CStringA s;
        s.Format("没找到可关的 MDI 子窗口（无 MDIClient 的顶层窗口数=%d 当前弹窗=%d first=%s）",
                 skippedNoMdi, dlg, firstTitle.GetString());
        AppendLog("CLOSEDOC", "no-window", s);
    }
    return closed;
}

//-----------------------------------------------------------------------------
// watcher 定时器：只负责「把请求文件转成一次 CRX 命令调用」，
// 在定时器里绝对不碰 sendStringToExecute / 文档操作。
// 原因：定时器回调是在 CX_DFT_default（空闲命令）内部执行的，
//       在该上下文里调用 sendStringToExecute 会让 CAXA 重入崩溃；
//       而 crxedCommand 只排队不重入，实测安全（12:51:08 返回 0 但命令随后正常执行）。
//-----------------------------------------------------------------------------
// crxedCommand 直调时 CAXA 走"命令体直调"路径，外层命令名（CX_save）不出现在 reactor，
// 只有内层实现名（CX_save_Impl / CX_DFT_ViewSave / CX_DFT_ViewClose）才出现。
// 所以 reactor 必须匹配内层命令名，且用 g_pendingKind 区分"这个 CX_DFT_ViewClose 是
// SAVE 内部副作用还是我们 CLOSE 命令的直接结果"。
//-----------------------------------------------------------------------------
enum { kPendingNone = 0, kPendingSave = 1, kPendingClose = 2, kPendingRefresh = 3 };
static volatile int  g_pendingKind   = kPendingNone;
static volatile bool g_updateEnded   = false;
static volatile bool g_saveEnded     = false;
static volatile bool g_closeEnded    = false;
static bool          g_inTimer       = false;

static void RefreshTick()
{
    if (g_inTimer) return;
    g_inTimer = true;

    CStringW raw;
    if (ReadRequestFile(raw))
    {
        // 请求末尾带 #seq=N：C# 靠 REQ.ack 确认"这条请求真的被 CRX 取走了"。
        // 16:13 实例：CLOSEDOC 与 CMD:CLOSE 同一毫秒写入，单槽请求文件被覆盖，
        // CLOSEDOC 根本没被执行——必须带序号 + 回执。
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
            // C# 据此确认"图纸环境里的桥真的活了"（比看 marker 文件时间可靠：
            // 上次会话遗留的 marker 会让 IsCrxLoaded 误判为已加载）。
            AppendLog("REQ", "pong", "crx alive");
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
            // 原生关闭：投 WM_CLOSE → CAXA 弹「是否保存修改」→ PopupAutoYes 点「是」
            // → CAXA 自己跑 CX_save → CX_save_Impl → CX_DFT_ViewSave 并关文档
            CStringW fileName;
            int p = raw.Find(L'|');
            if (p >= 0) fileName = raw.Mid(p + 1);
            fileName.Trim();

            g_pendingKind = kPendingClose;
            g_closeEnded  = false;

            // 先记录文档「有/无改动」，C# 据此区分「有改动已落盘 / 已保存落盘」
            LogDocModified(fileName);
            // 无改动也强制保存：先 Ctrl+S 同路保存（同步落盘），再投 WM_CLOSE。
            // 保存后文档已落盘且未修改，WM_CLOSE 不弹窗直接关——不再依赖「是否保存」弹窗。
            SaveDocByCommand(fileName);
            int n = CloseDocWindows(fileName);
            CStringA s;
            s.Format("WM_CLOSE 投递给 %d 个文档窗口 file=%s", n,
                     fileName.IsEmpty() ? "<any>" : WideToUtf8(fileName).GetString());
            AppendLog("REQ", "closedoc-dispatched", s);
        }
        else if (cmd.Find("CMD:CLOSE") >= 0)
        {
            g_pendingKind = kPendingClose;
            g_closeEnded = false;
            int st = crxedCommand(RTSTR, L"CX_DFT_ViewClose", RTNONE);
            CStringA s; s.Format("CX_DFT_ViewClose ret=%d", st);
            AppendLog("REQ", "close-dispatched", s);
        }
        else if (cmd.Find("CMD:REFRESH_VIEWS") >= 0)
        {
            g_pendingKind = kPendingRefresh;
            g_updateEnded = false;
            int st = crxedCommand(RTSTR, L"CX_DFT_ViewUpdate", RTNONE);
            CStringA s; s.Format("CX_DFT_ViewUpdate ret=%d", st);
            AppendLog("REQ", "refresh-dispatched", s);
        }
        else if (cmd.Find("CMD:EXEC|") >= 0)
        {
            // 试验用：按"命令名"直调，方便在不重编译的前提下验证某个命令名是否有效
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

    // 窗口树 dump / 弹窗计数轮询：调试期用完即撤（每次 CRX 加载会刷 130+ 行日志、
    // 且 DLG.count 每 3s 一条，既拖慢又不便读日志）。需要时用 CMD:DUMPWND 手动触发。

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
    if (g_updateEnded)
    {
        g_updateEnded = false;
        g_pendingKind = kPendingNone;
        AppendLog("REQ", "refresh-done", "CX_DFT_ViewUpdate ended");
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

    const wchar_t* clsName = L"Caxa3dViewUpdateWatcher";
    WNDCLASSW wc;
    memset(&wc, 0, sizeof(wc));
    wc.lpfnWndProc   = WatcherWndProc;
    wc.hInstance     = Caxa3dViewUpdateDLL.ModuleResourceInstance();
    wc.lpszClassName = clsName;
    RegisterClassW(&wc);   // 重复注册返回 0，无妨

    g_hWatcherWnd = CreateWindowExW(0, clsName, clsName, 0,
                                    0, 0, 0, 0, HWND_MESSAGE, NULL,
                                    Caxa3dViewUpdateDLL.ModuleResourceInstance(), NULL);
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
// CRxEditorReactor —— 命令事件（C# 端据此等待 CX_DFT_ViewUpdate 结束）
//-----------------------------------------------------------------------------
class CEditorReactor : public CRxEditorReactor
{
public:
    CEditorReactor() {}
    virtual void commandWillStart(const CxCHAR* cmdStr) override
    {
        EnsureReactorsAttached("cmdStart");
        AppendLog("EDITOR", "commandWillStart", CxCharToMbs(cmdStr));
    }
    virtual void commandEnded(const CxCHAR* cmdStr) override
    {
        AppendLog("EDITOR", "commandEnded", CxCharToMbs(cmdStr));
        if (!cmdStr) return;
        // crxedCommand 直调时外层命令名不进 reactor，只有内层实现名才进。
        // 必须结合 g_pendingKind 防止 SAVE 内部副作用（CX_DFT_ViewClose）误置位 closeEnded。
        int pk = g_pendingKind;
        if (_wcsicmp(cmdStr, L"CX_DFT_ViewUpdate") == 0 && pk == kPendingRefresh)
            g_updateEnded = true;
        else if (_wcsicmp(cmdStr, L"CX_DFT_ViewSave") == 0 && pk == kPendingSave)
            g_saveEnded = true;
        else if (_wcsicmp(cmdStr, L"CX_DFT_ViewClose") == 0 && pk == kPendingClose)
            g_closeEnded = true;
    }
    virtual void commandCancelled(const CxCHAR* cmdStr) override
    { AppendLog("EDITOR", "commandCancelled", CxCharToMbs(cmdStr)); }
    virtual void commandFailed(const CxCHAR* cmdStr) override
    { AppendLog("EDITOR", "commandFailed", CxCharToMbs(cmdStr)); }
    virtual void unknownCommand(const CxCHAR* cmdStr, CRxDbVoidPtrArray* al) override
    { AppendLog("EDITOR", "unknownCommand", CxCharToMbs(cmdStr)); }
    virtual void viewChanged() override
    { EnsureReactorsAttached("viewChanged"); }
};

//-----------------------------------------------------------------------------
// 模块入口
//-----------------------------------------------------------------------------
class CCaxa3dViewUpdateApp : public AcRxArxApp
{
public:
    CCaxa3dViewUpdateApp() : AcRxArxApp() {}

    virtual AcRx::AppRetCode On_kInitAppMsg(void* pkt) override
    {
        // 最早期的加载探针：不依赖 MFC / CriticalSection / 任何全局状态。
        {
            wchar_t markPath[MAX_PATH];
            TempPath(markPath, MAX_PATH, L"caxa3d_viewupdate_crx.marker");
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
        TempPath(path, MAX_PATH, L"caxa3d_viewupdate_crx.log");
        DeleteFileW(path);
        wchar_t reqPath[MAX_PATH];
        TempPath(reqPath, MAX_PATH, L"caxa3d_viewupdate_request.txt");
        DeleteFileW(reqPath);

        OpenLogFile();
        AppendLog("MOD", "On_kInitAppMsg", "CRX module loaded");

        g_pEditorReactor = new CEditorReactor();
        EnsureReactorsAttached("init");
        CreateWatcher();  // 300ms 轮询请求文件
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
        TempPath(markPath, MAX_PATH, L"caxa3d_viewupdate_crx.marker");
        DeleteFileW(markPath);
        return AcRxArxApp::On_kUnloadAppMsg(pkt);
    }

    virtual void RegisterServerComponents() override {}
};

IMPLEMENT_ARX_ENTRYPOINT(CCaxa3dViewUpdateApp)
