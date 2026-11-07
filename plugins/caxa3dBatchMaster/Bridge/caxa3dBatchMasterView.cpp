//=====================================================================
// caxa3dBatchMasterView.cpp — caxa3dBatchMaster「刷新工程图」桥接型 CRX
// （CAXA 3D 2027 图纸环境 CAXADraft，IronCAD.exe 进程内）
//
// ★ 定位（规则第 11 节 21/22/25/26/27，桥接型）
//   本 CRX 唯一职责：收 C# 请求 → 在白名单内「图纸保存 + 关闭」原生命令 → 必落恰好一条回包。
//   零 UI、零命令注册（不 addCommand、不建 Ribbon、不发广播消息、不引 afxdllx.h）、
//   零决策（决策权全在 C# 侧）、零工作线程（节拍 = message-only 窗口上的 WM_TIMER 100 ms）、
//   不挂命名管道、不 pin。
//   · 随 CxAutorun.cx 自动加载（登记由 C# 侧部署脚本负责，本文件不碰）。
//   · 加载只做准备工作（顺序固定）：生成会话令牌 → 写 marker（哪怕后面某步崩了，
//     marker 也已落地）→ 清残留请求/回包 + seq 归零 → 基类 init → 开日志 → 挂定时器。
//     加载时绝不执行任何命令（哪怕 %TEMP% 里躺着上会话 confirm=yes 的请求）。
//   · 卸载路径固定（桥接型不 pin）：
//     StopBridge()（KillTimer + DestroyWindow）→ WriteMarker(false) 摘 marker →
//     g_logReady=false → CloseHandle(日志) → DeleteCriticalSection → 基类。
//     漏 DestroyWindow = 定时器在没有代码的镜像里继续跑（坑 187 的实证形状）。
//
// ── 文件桥协议（%TEMP%，UTF-8 无 BOM，按行 key=value，行尾 CRLF）───────────
//   请求  caxa3dbatchmaster_view_request.txt :
//     seq=<int> / action=<命令名> / confirm=yes|no / token=<会话令牌> / arg=<参数，可为空>
//   回包  caxa3dbatchmaster_view_response.txt:
//     seq=<int> / action=<命令名> / status=OK|ERR / token=<会话令牌> / message=<单行文本>
//   · CRX 侧只写 OK/ERR 两种 status；TIMEOUT/NOCRX 是 C# 侧判词，本模块绝不写。
//   · 回包完整性（铁律）：请求一旦被消费（文件被读走删掉）必须且只能落一条回包；
//     连 seq 都解析不出来也要回 status=ERR + message=parse-failed ... + seq=0。
//     空队列（没有请求文件）不算错误、不写回包。
//   · 样板桥的三条「静默丢请求」路径（读失败即删不回包 / 解析失败不回包 /
//     seq<=lastSeq 判过期不回包）在本模块逐条堵掉，见 BridgeTick 内 ★ 注释。
//
// ── 命令表（白名单；confirm / token 两列语义照抄样板）────────────────────
//   PING         confirm=否 token=否   —— 探活；message 原样带出 token=<令牌>
//   CLOSEDOC     confirm=是 token=是   —— 按文件名找图纸窗口（只比文件名）→
//                投 CX_save（WM_COMMAND 0xE103，与 Ctrl+S 同一条 MFC 命令路由）→
//                等 mtime 变 → WM_CLOSE 关窗（只关命中的 MDI 子窗口，绝不关主框架、
//                绝不退化关全部——否决 75）。
//                成功 message=close-done file=<名> modified=<0|1>
//   CLOSERAW     confirm=是 token=是   —— 不保存，直接 WM_CLOSE 关那张图纸窗口（预热收尾）。
//                成功 message=closeraw-done file=<名>
//   SAVE         confirm=是 token=是   —— 只对当前图纸（arg 非空则按文件名）投保存。
//                成功 message=save-done
//   EXEC_NATIVE  confirm=否 token=是   —— 只放行前缀 CX_DFT_ / CX_ 的宿主命令串，
//                其余一律 ERR；sendStringToExecute 只代表已投出，不代表执行完。
//                成功 message=injected=<命令串>
//   TITLEBLOCK_3DSET confirm=否 token=是 —— 「标题栏设默认」：4 通道轮换派发
//                CX_DFT_TITLEBLOCK_3DSET，以标题栏信息集合前后 diff 为唯一成功判据。
//                本 verb 立即回 OK(tb-queued)；判定结果异步落日志 REQ.tb-result / REQ.tb-giveup
//                / REQ.tb-done（C# 侧 CrxBridge.LogCount 按子串匹配，与参考工程口径一致）。
//   TB_ABORT     confirm=否 token=是   —— 终止标题栏派发与判定，回 OK(tb-aborted)。
//   · 三道拒绝闸（未知 action / 令牌不符 / 缺 confirm）各自 ERR 回包 + 一行 bridge: 日志。
//
// ── 会话令牌 ────────────────────────────────────────────────────────
//   每次加载生成 <pid>-<GetTickCount()>；PING 的 message 原样带出 token=<令牌>；
//   破坏性命令必须回填该令牌，不匹配一律 ERR、绝不执行 —— 上会话残留请求即使
//   没被清掉，令牌也对不上。
//
// ── 就绪探针 / 日志 / 安全开关 ──────────────────────────────────────
//   marker caxa3dbatchmaster_view_crx.marker ：内容含 RUNNING 与会话令牌；卸载摘掉。
//     C# 用「marker 写入时间 >= 本进程启动时间」判桥是否真活着。
//   日志   caxa3dbatchmaster_view_crx.log    ：追加写、共享读；关键判据行必须落文件
//     （marker: ready / tick: consumed seq=.. / REQ.ack | seq=.. / commandEnded | <命令> /
//      close-done / save-done / modified=0|1 / stop: bridge torn down）。
//   安全开关 caxa3dbatchmaster_view_crx.kill ：存在即停桥轮询（运维兜底）。
//     注意：此时请求**没有被消费、留在盘上**，与「消费不回包」是两回事（规则第 11 节 25）。
//=====================================================================

#include "StdAfx.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <vector>
#include <string>
#include <sstream>

#include "crxcmd.h"        // CRX 命令表基础头（被其它头间接依赖，保留）
#include "crxdocman.h"     // crxDocManager / sendStringToExecute / 文档迭代器
#include "crxentrypoint.h"
#include "crxed.h"         // CRxEditorReactor / crxedEditor —— commandEnded 判据行
#include "dbmain.h"        // CRxDbDatabase / CDraft
#include "crxdb.h"
#include "crxstrc.h"       // CDraft::eOk
#include "crxChar.h"       // CxCHAR

AC_IMPLEMENT_EXTENSION_MODULE(Caxa3dBatchMasterViewDLL)

//---------------------------------------------------------------------
// 常量（%TEMP% 文件前缀统一 caxa3dbatchmaster_view_，与本机既有 6 个 CRX /
// 已占用的三系前缀全部换开）
//---------------------------------------------------------------------
static const wchar_t* kReqFile    = L"caxa3dbatchmaster_view_request.txt";
static const wchar_t* kRespFile   = L"caxa3dbatchmaster_view_response.txt";
static const wchar_t* kKillFile   = L"caxa3dbatchmaster_view_crx.kill";
static const wchar_t* kMarkerFile = L"caxa3dbatchmaster_view_crx.marker";
static const wchar_t* kLogFile    = L"caxa3dbatchmaster_view_crx.log";

static const UINT_PTR kBridgeTimerId = 0xE3;
static const UINT     kBridgeTimerMs = 100;   // message-only 窗口节拍，与产线桥一致

// 超时闸门：投 CX_save 后等 mtime 变化 / 投 WM_CLOSE 后等窗口真消失
static const ULONGLONG kSaveMtimeWaitMs   = 15000;
static const ULONGLONG kCloseWindowWaitMs = 30000;

// 读请求连续失败重试次数：超过即按「消费 + parse-failed」处理，堵住永久卡死路径
static const int kReadFailMaxRetry = 3;

// EXEC_NATIVE 白名单前缀：只放行 CAXA 原生图纸命令族，绝不写空前缀
static const wchar_t* kNativeCmdPrefixes[] = { L"CX_DFT_", L"CX_" };

// ID_FILE_SAVE：手工 Ctrl+S / 保存按钮走的同一条 MFC 命令路由（图纸保存实证通道），
// SendMessage 到文档 MDI 子窗口即同步落盘；不用 crxedCommand("CX_save")——
// 该名字不在命令表里，crxedCommand 是「静默成功但什么都不做」（10-23 日志实证）。
static const WPARAM kIdFileSave = 0xE103;

// 图纸文档 MDI 子窗口的类名特征（坑 169：判「是不是图纸窗口」只按类名认 AfxFrameOrView，
// 主框架 / BCGP 阴影窗 / WinForms 窗一律不算）
static const wchar_t* kDraftWndClassFragment = L"AfxFrameOrView";

//---------------------------------------------------------------------
// 全局状态
//---------------------------------------------------------------------
static HWND  g_hDispWnd = NULL;                 // message-only 窗口（零可见 UI）
static bool  g_logReady = false;
static CRITICAL_SECTION g_logCS;
static HANDLE g_hLog = INVALID_HANDLE_VALUE;

static int   g_lastSeq        = 0;              // 已处理的最大 seq（幂等）
static bool  g_killSeen       = false;
static bool  g_inTick         = false;          // WM_TIMER 重入闸
static int   g_readFailStreak = 0;              // 请求读失败连续计数（★ 堵静默路径用）

static std::wstring g_token;                   // 本次加载的会话令牌 <pid>-<tick>

// CLOSEDOC / CLOSERAW 的跨节拍状态机（同一时刻只允许一张图纸在收尾，不阻塞主线程）
enum JobStage { kJobNone = 0, kJobSaveWait, kJobCloseWait };
struct CloseJob
{
    int           stage    = kJobNone;
    int           seq      = 0;
    std::wstring  action;            // CLOSEDOC / CLOSERAW（回包要原样带出）
    std::wstring  fileName;          // 比较用的文件名（不带路径）
    std::wstring  fullPath;          // 文档全路径（判 mtime 用；拿不到则为空）
    HWND          hwnd     = NULL;   // 目标 MDI 子窗口
    ULONGLONG     baseMtime = 0;
    int           modified  = 0;     // 投递保存时的 CDocument::IsModified()
    ULONGLONG     deadline  = 0;     // GetTickCount64() 期限
};
static CloseJob g_job;

//---------------------------------------------------------------------
// 日志：%TEMP%\caxa3dbatchmaster_view_crx.log（追加写、共享读）
//---------------------------------------------------------------------
static void TempPath(wchar_t* out, size_t cap, const wchar_t* name)
{
    DWORD n = GetEnvironmentVariableW(L"TEMP", out, (DWORD)cap);
    if (n == 0 || n >= cap) wcscpy_s(out, cap, L".");
    size_t len = wcslen(out);
    if (len && out[len - 1] == L'\\') out[len - 1] = 0;
    wcscat_s(out, cap, L"\\");
    wcscat_s(out, cap, name);
}

static void LogOpen()
{
    if (g_hLog != INVALID_HANDLE_VALUE) return;
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, kLogFile);
    g_hLog = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                         NULL, OPEN_ALWAYS, 0, NULL);
}

static void Log(const char* fmt, ...)
{
    if (!g_logReady || g_hLog == INVALID_HANDLE_VALUE) return;
    char body[768];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(body, sizeof(body), _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[900];
    _snprintf_s(line, sizeof(line), _TRUNCATE, "%02d:%02d:%02d.%03d [tid %lu] %s\r\n",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                GetCurrentThreadId(), body);

    EnterCriticalSection(&g_logCS);
    DWORD written = 0;
    WriteFile(g_hLog, line, (DWORD)strlen(line), &written, NULL);
    LeaveCriticalSection(&g_logCS);
}

// 宽字符 → UTF-8（日志参数统一走这里，保证日志文件字节是 UTF-8）
static std::string W2U8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), NULL, 0, NULL, NULL);
    if (n <= 0) return std::string();
    std::vector<char> buf(n + 1, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &buf[0], n, NULL, NULL);
    return std::string(&buf[0]);
}

//---------------------------------------------------------------------
// 文件桥基础读写
//---------------------------------------------------------------------
static bool FileExists(const wchar_t* path)
{
    DWORD a = GetFileAttributesW(path);
    return (a != INVALID_FILE_ATTRIBUTES) && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

static bool ReadUtf8File(const wchar_t* path, std::wstring& out)
{
    out.clear();
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD size = GetFileSize(h, NULL);
    bool ok = false;
    if (size != INVALID_FILE_SIZE && size > 0)
    {
        std::vector<unsigned char> buf(size);
        DWORD rd = 0;
        if (ReadFile(h, &buf[0], size, &rd, NULL) && rd > 0)
        {
            // 容错跳过 UTF-8 BOM（协议规定无 BOM，残留文件可能带）
            int off = 0;
            if (rd >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) off = 3;
            int wlen = MultiByteToWideChar(CP_UTF8, 0, (char*)&buf[off], (int)(rd - off), NULL, 0);
            if (wlen > 0)
            {
                out.resize(wlen);
                MultiByteToWideChar(CP_UTF8, 0, (char*)&buf[off], (int)(rd - off), &out[0], wlen);
                ok = true;
            }
        }
    }
    CloseHandle(h);
    return ok;
}

static bool WriteUtf8File(const wchar_t* path, const std::wstring& content)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, content.c_str(), (int)content.size(), NULL, 0, NULL, NULL);
    if (n <= 0) return false;
    std::vector<char> buf(n);
    WideCharToMultiByte(CP_UTF8, 0, content.c_str(), (int)content.size(), &buf[0], n, NULL, NULL);
    HANDLE h = CreateFileW(path, GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, CREATE_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD wr = 0;
    WriteFile(h, &buf[0], (DWORD)n, &wr, NULL);
    CloseHandle(h);
    return wr == (DWORD)n;
}

/// <summary>
/// 写回包（整文件覆盖，UTF-8 无 BOM，行尾 CRLF）。
/// 字段固定五条：seq / action / status / token / message —— status 只有 OK/ERR，
/// TIMEOUT/NOCRX 归 C# 侧，本函数结构上写不出来那两个词。
/// </summary>
static void WriteResp(int seq, const std::wstring& action, const wchar_t* status,
                      const std::wstring& msg)
{
    std::wstring one = msg;
    // message 必须单行：把任何残留换行换成空格
    for (size_t i = 0; i < one.size(); ++i)
        if (one[i] == L'\r' || one[i] == L'\n') one[i] = L' ';

    std::wstring s;
    s += L"seq=";     s += std::to_wstring(seq); s += L"\r\n";
    s += L"action=";  s += action;               s += L"\r\n";
    s += L"status=";  s += status;               s += L"\r\n";
    s += L"token=";   s += g_token;              s += L"\r\n";
    s += L"message="; s += one;                  s += L"\r\n";

    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, kRespFile);
    if (!WriteUtf8File(path, s))
    {
        Log("resp: WRITE-FAILED seq=%d err=%lu", seq, GetLastError());
        return;
    }
    Log("resp: seq=%d action=%s status=%hs message=%hs",
        seq, W2U8(action).c_str(), status, W2U8(one).c_str());
}

/// <summary>解析 key=value 行。返回 false = 请求不合法（缺 seq 或 action）；
/// 但无论成败，能解析出的字段都留在出参里，供回包尽量回显。</summary>
static bool ParseRequest(const std::wstring& text, int& seq, std::wstring& action,
                         bool& confirm, std::wstring& token, std::wstring& arg)
{
    seq = 0; action.clear(); confirm = false; token.clear(); arg.clear();
    bool hasSeq = false, hasAction = false;
    std::wstringstream ss(text);
    std::wstring line;
    while (std::getline(ss, line))
    {
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        if (line.empty()) continue;
        size_t eq = line.find(L'=');
        if (eq == std::wstring::npos) continue;
        std::wstring key = line.substr(0, eq);
        std::wstring val = line.substr(eq + 1);
        if (key == L"seq")          { seq = _wtoi(val.c_str()); hasSeq = true; }
        else if (key == L"action")  { action = val;             hasAction = true; }
        else if (key == L"confirm") confirm = (val == L"yes");
        else if (key == L"token")   token = val;
        else if (key == L"arg")     arg = val;
    }
    return hasSeq && hasAction && seq > 0 && !action.empty();
}

//---------------------------------------------------------------------
// 小工具
//---------------------------------------------------------------------
static std::wstring ToLowerW(const std::wstring& s)
{
    std::wstring r; r.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) r.push_back((wchar_t)towlower(s[i]));
    return r;
}

static bool ContainsNoCase(const std::wstring& hay, const std::wstring& needle)
{
    if (needle.empty()) return true;
    return ToLowerW(hay).find(ToLowerW(needle)) != std::wstring::npos;
}

// arg 可能带全路径，也可能只有文件名 —— 统一取基名（只比文件名，规则口径）
static std::wstring ExtractBaseName(const std::wstring& arg)
{
    size_t p = arg.find_last_of(L"\\/");
    std::wstring base = (p == std::wstring::npos) ? arg : arg.substr(p + 1);
    // 去掉可能带的投递后缀尾巴（如 "|arg" 形态残留），只留纯文件名
    size_t q = base.find(L'|');
    if (q != std::wstring::npos) base = base.substr(0, q);
    while (!base.empty() && (base.back() == L' ' || base.back() == L'\t')) base.pop_back();
    return base;
}

static ULONGLONG GetFileMtime(const std::wstring& path)
{
    if (path.empty()) return 0;
    WIN32_FILE_ATTRIBUTE_DATA fad;
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) return 0;
    ULARGE_INTEGER ul;
    ul.HighPart = fad.ftLastWriteTime.dwHighDateTime;
    ul.LowPart  = fad.ftLastWriteTime.dwLowDateTime;
    return ul.QuadPart;
}

//---------------------------------------------------------------------
// 窗口层：找图纸文档窗口（MDI 子窗口，只认 AfxFrameOrView 类名，坑 169）
// 关闭只允许命中单个窗口 —— 绝不关主框架（坑 141），绝不「没匹配上就关全部」（否决 75）。
//---------------------------------------------------------------------
static HWND g_mdiFound = NULL;

static BOOL CALLBACK FindMdiProc(HWND h, LPARAM)
{
    wchar_t cls[64] = { 0 };
    GetClassNameW(h, cls, 64);
    if (wcscmp(cls, L"MDIClient") == 0) { g_mdiFound = h; return FALSE; }
    return TRUE;
}

static HWND LocateMdiClient(HWND hTop)
{
    HWND mdi = FindWindowExW(hTop, NULL, L"MDIClient", NULL);
    if (!mdi)
    {
        g_mdiFound = NULL;
        EnumChildWindows(hTop, FindMdiProc, 0);
        mdi = g_mdiFound;
    }
    return mdi;
}

static bool IsDraftChild(HWND h)
{
    wchar_t cls[128] = { 0 };
    GetClassNameW(h, cls, 128);
    return wcsstr(cls, kDraftWndClassFragment) != NULL;
}

static std::wstring WndTitle(HWND h)
{
    wchar_t txt[512] = { 0 };
    GetWindowTextW(h, txt, 512);
    return std::wstring(txt);
}

/// <summary>按文件名（只比文件名）找那张图纸的 MDI 子窗口；找不到绝不退化。</summary>
static HWND FindDrawingWindowByFile(const std::wstring& baseName, std::wstring& why)
{
    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId() || !IsWindowVisible(h)) continue;
        HWND mdi = LocateMdiClient(h);
        if (!mdi) continue;
        for (HWND c = GetWindow(mdi, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT))
        {
            if (!IsDraftChild(c)) continue;
            if (ContainsNoCase(WndTitle(c), baseName)) return c;
        }
    }
    why = L"没有类名含 AfxFrameOrView 且标题含该文件名的 MDI 子窗口";
    return NULL;
}

/// <summary>找当前（活动）图纸窗口：WM_MDIGETACTIVE → 兜底第一个 AfxFrameOrView 子窗。</summary>
static HWND FindActiveDrawingWindow(std::wstring& why)
{
    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid != GetCurrentProcessId() || !IsWindowVisible(h)) continue;
        HWND mdi = LocateMdiClient(h);
        if (!mdi) continue;
        BOOL bMax = FALSE;
        HWND act = (HWND)SendMessageW(mdi, WM_MDIGETACTIVE, 0, (LPARAM)&bMax);
        if (act && IsWindow(act) && IsDraftChild(act)) return act;
        for (HWND c = GetWindow(mdi, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT))
            if (IsDraftChild(c)) return c;
    }
    why = L"没有打开中的图纸窗口";
    return NULL;
}

/// <summary>经 CRX 文档迭代器按文件名找图纸文档：拿全路径（判 mtime）+ IsModified。
/// COM 侧（IZBaseApp 四个枚举入口）看不到图纸文档，图纸文档的唯一观测面在 CRX 侧（规则 11 节 1）。</summary>
static bool FindDocState(const std::wstring& baseName, std::wstring& fullPath, bool& modified)
{
    fullPath.clear(); modified = false;
    if (!crxDocManager) return false;
    CRxApDocumentIterator* it = crxDocManager->newAcApDocumentIterator();
    if (!it) return false;
    bool found = false;
    for (; !it->done(); it->step())
    {
        CRxApDocument* d = it->document();
        if (!d) continue;
        const CxCHAR* fn = d->fileName();
        if (!fn || !fn[0]) continue;
        std::wstring wfn(fn);
        if (!baseName.empty() && !ContainsNoCase(wfn, baseName)) continue;
        found = true;
        fullPath = wfn;
        if (d->cDoc()) modified = d->cDoc()->IsModified() ? true : false;
        break;
    }
    delete it;
    return found;
}

//---------------------------------------------------------------------
// 「标题栏设默认」CRX 侧能力：4 通道轮换 + 标题栏信息 diff 判定 + 看门狗
//（机械移植自 caxa3dcodenameextraction-crx，见 TitleBlockBridge.inl 头部说明）。
// 必须放在 reactor 之前：里面定义的 g_tb* 状态与两个 verb handler 供下面使用。
//---------------------------------------------------------------------
#include "TitleBlockBridge.inl"

//---------------------------------------------------------------------
// CRxEditorReactor：把 commandWillStart/commandEnded/... 落进日志文件
//（C# 增量 tail 的判据行；幂等挂接，内嵌图纸环境不发 On_kLoadDwgMsg）
//---------------------------------------------------------------------
static CRxEditorReactor* g_pEditorReactor = NULL;

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
            Log("MOD: attach | editor @%s", why);
        } catch (...) {
            Log("MOD: attach FAILED | editor @%s", why);
        }
    }
}

class CBatchMasterViewReactor : public CRxEditorReactor
{
public:
    CBatchMasterViewReactor() {}
    virtual void commandWillStart(const CxCHAR* cmdStr) override
    {
        EnsureReactorsAttached("cmdStart");
        Log("commandWillStart | %hs", cmdStr ? W2U8(cmdStr).c_str() : "<null>");
        // 标题栏命令真的开始跑了：只置「本轮已运行」，绝不能当成功
        //（CAXA 每次打开图纸也会自己跑一次；成功与否由结束后 TB.diff 判定）
        if (cmdStr && _wcsicmp(cmdStr, kTbCmd) == 0 && (g_tbWanted || g_tbPending))
        {
            g_tbRunActive = true;
            Log("REQ.tb-run-start | chan=%d try=%d runs=%d", g_tbChan, g_tbTries, g_tbRuns);
        }
    }
    virtual void commandEnded(const CxCHAR* cmdStr) override
    {
        Log("commandEnded | %hs", cmdStr ? W2U8(cmdStr).c_str() : "<null>");
        if (cmdStr && _wcsicmp(cmdStr, kTbCmd) == 0 && g_tbPending) g_tbEnded = true;
    }
    virtual void commandCancelled(const CxCHAR* cmdStr) override
    {
        Log("commandCancelled | %hs", cmdStr ? W2U8(cmdStr).c_str() : "<null>");
        // 取消也算一轮结束：交由 TitleBlockTick 比对标题栏信息后决定换通道/成功/失败
        if (cmdStr && _wcsicmp(cmdStr, kTbCmd) == 0 && g_tbPending) g_tbEnded = true;
    }
    virtual void commandFailed(const CxCHAR* cmdStr) override
    {
        Log("commandFailed | %hs", cmdStr ? W2U8(cmdStr).c_str() : "<null>");
        if (cmdStr && _wcsicmp(cmdStr, kTbCmd) == 0 && g_tbPending) g_tbEnded = true;
    }
};

//---------------------------------------------------------------------
// CLOSEDOC / CLOSERAW 跨节拍状态机
//---------------------------------------------------------------------
static void ResetJob()
{
    g_job = CloseJob();
}

static void EnterCloseWait(ULONGLONG deadlineMs)
{
    if (!g_job.hwnd) return;
    PostMessageW(g_job.hwnd, WM_CLOSE, 0, 0);   // 只关命中的那张图纸窗口
    g_job.stage   = kJobCloseWait;
    g_job.deadline = GetTickCount64() + deadlineMs;
    Log("close-post | hwnd=%p file=%s", (void*)g_job.hwnd, W2U8(g_job.fileName).c_str());
}

/// <summary>每个节拍推进一次收尾状态机（不阻塞主线程；等待全部跨节拍）。</summary>
static void JobTick()
{
    if (g_job.stage == kJobSaveWait)
    {
        ULONGLONG cur = GetFileMtime(g_job.fullPath);
        if (cur != 0 && cur != g_job.baseMtime)
        {
            Log("mtime-changed | file=%s -> 投 WM_CLOSE", W2U8(g_job.fileName).c_str());
            EnterCloseWait(kCloseWindowWaitMs);
            return;
        }
        if (GetTickCount64() > g_job.deadline)
        {
            if (g_job.modified == 0)
            {
                // 干净文档强制保存后 mtime 不变（OnFileSave 短路）⇒ 数据无损，直接关
                Log("save-mtime-timeout-clean | modified=0 file=%s 直接关窗",
                    W2U8(g_job.fileName).c_str());
                EnterCloseWait(kCloseWindowWaitMs);
                return;
            }
            Log("save-timeout | modified=1 file=%s —— 未关窗，状态留给 C#",
                W2U8(g_job.fileName).c_str());
            WriteResp(g_job.seq, g_job.action, L"ERR",
                      L"save-timeout file=" + g_job.fileName + L" modified=1");
            ResetJob();
        }
        return;
    }

    if (g_job.stage == kJobCloseWait)
    {
        if (!g_job.hwnd || !IsWindow(g_job.hwnd))
        {
            std::wstring msg;
            if (g_job.action == L"CLOSEDOC")
            {
                msg = L"close-done file=" + g_job.fileName +
                      L" modified=" + std::to_wstring(g_job.modified);
                Log("%hs", W2U8(L"close-done | file=" + g_job.fileName).c_str());
            }
            else
            {
                msg = L"closeraw-done file=" + g_job.fileName;
                Log("%hs", W2U8(L"closeraw-done | file=" + g_job.fileName).c_str());
            }
            WriteResp(g_job.seq, g_job.action, L"OK", msg);
            ResetJob();
            return;
        }
        if (GetTickCount64() > g_job.deadline)
        {
            std::wstring tag = (g_job.action == L"CLOSEDOC")
                             ? L"close-timeout file=" : L"closeraw-timeout file=";
            Log("close-timeout | file=%s 窗口仍在 —— 弹窗可能未被处理",
                W2U8(g_job.fileName).c_str());
            WriteResp(g_job.seq, g_job.action, L"ERR", tag + g_job.fileName);
            ResetJob();
        }
    }
}

//---------------------------------------------------------------------
// 命令实现（verb 处理器）
//---------------------------------------------------------------------
/// <summary>握手/探活：message 原样带出本次会话令牌（C# 拿到令牌才敢发破坏性命令）。</summary>
static void DoPing(int seq, const std::wstring& /*arg*/)
{
    std::wstring msg = L"bridge alive; token=" + g_token +
                       L"; verbs=PING,CLOSEDOC,CLOSERAW,SAVE,EXEC_NATIVE,TITLEBLOCK_3DSET,TB_ABORT";
    WriteResp(seq, L"PING", L"OK", msg);
    Log("ping: token=%hs", W2U8(g_token).c_str());
}

/// <summary>按文件名找图纸 → 投 CX_save（WM_COMMAND 0xE103 同步路由）→ 等 mtime 变 → WM_CLOSE。</summary>
static void DoCloseDoc(int seq, const std::wstring& arg)
{
    std::wstring baseName = ExtractBaseName(arg);
    if (baseName.empty())
    {
        WriteResp(seq, L"CLOSEDOC", L"ERR", L"parse-failed CLOSEDOC 需要 arg=<图纸文件名>");
        Log("bridge: CLOSEDOC 缺 arg，已拒绝");
        return;
    }

    bool docFound = false;
    std::wstring fullPath;
    bool modified = false;
    docFound = FindDocState(baseName, fullPath, modified);
    Log("MOD.state | modified=%d file=%s doc=%d", modified ? 1 : 0,
        W2U8(baseName).c_str(), docFound ? 1 : 0);

    std::wstring why;
    HWND hwnd = FindDrawingWindowByFile(baseName, why);
    if (!hwnd)
    {
        Log("close-window-not-found | file=%s（%hs）", W2U8(baseName).c_str(),
            W2U8(why).c_str());
        WriteResp(seq, L"CLOSEDOC", L"ERR",
                  L"window-not-found file=" + baseName + L"（绝不关主框架/全部窗口）");
        return;
    }

    g_job.stage     = kJobNone;
    g_job.seq       = seq;
    g_job.action    = L"CLOSEDOC";
    g_job.fileName  = baseName;
    g_job.fullPath  = docFound ? fullPath : L"";
    g_job.hwnd      = hwnd;
    g_job.modified  = modified ? 1 : 0;
    g_job.baseMtime = GetFileMtime(g_job.fullPath);

    // 与手动 Ctrl+S / 保存按钮同一条 MFC 命令路由；SendMessage 同步返回时已落盘
    SendMessageW(hwnd, WM_COMMAND, kIdFileSave, 0);
    Log("save-posted | WM_COMMAND 0xE103 file=%s", W2U8(baseName).c_str());

    ULONGLONG cur = GetFileMtime(g_job.fullPath);
    if (!g_job.fullPath.empty() && g_job.modified == 1 &&
        (cur == 0 || cur == g_job.baseMtime))
    {
        // 脏文档且 mtime 暂未变 ⇒ 跨节拍等一下（不阻塞主线程）
        g_job.stage    = kJobSaveWait;
        g_job.deadline = GetTickCount64() + kSaveMtimeWaitMs;
        Log("tick: save-wait mtime | file=%s", W2U8(baseName).c_str());
    }
    else
    {
        EnterCloseWait(kCloseWindowWaitMs);
    }
}

/// <summary>不保存，直接关那张图纸窗口（预热收尾 / CLOSERAW 定案语义，规则 11 节 15）。</summary>
static void DoCloseRaw(int seq, const std::wstring& arg)
{
    std::wstring baseName = ExtractBaseName(arg);
    if (baseName.empty())
    {
        WriteResp(seq, L"CLOSERAW", L"ERR", L"parse-failed CLOSERAW 需要 arg=<图纸文件名>");
        Log("bridge: CLOSERAW 缺 arg，已拒绝");
        return;
    }
    std::wstring why;
    HWND hwnd = FindDrawingWindowByFile(baseName, why);
    if (!hwnd)
    {
        Log("closeraw-window-not-found | file=%s（%hs）", W2U8(baseName).c_str(),
            W2U8(why).c_str());
        WriteResp(seq, L"CLOSERAW", L"ERR",
                  L"window-not-found file=" + baseName + L"（绝不关主框架/全部窗口）");
        return;
    }

    bool modified = false; std::wstring fullPath;
    FindDocState(baseName, fullPath, modified);   // 只为日志留痕，不影响动作

    g_job.seq      = seq;
    g_job.action   = L"CLOSERAW";
    g_job.fileName = baseName;
    g_job.hwnd     = hwnd;
    g_job.modified = modified ? 1 : 0;
    Log("closeraw-dispatched | 不保存直接 WM_CLOSE file=%s modified=%d",
        W2U8(baseName).c_str(), modified ? 1 : 0);
    EnterCloseWait(kCloseWindowWaitMs);
}

/// <summary>只对当前图纸投保存（arg 非空时按文件名定位那张）。同步路由，当场回包。</summary>
static void DoSave(int seq, const std::wstring& arg)
{
    std::wstring why;
    std::wstring baseName = ExtractBaseName(arg);
    HWND hwnd = baseName.empty() ? FindActiveDrawingWindow(why)
                                 : FindDrawingWindowByFile(baseName, why);
    if (!hwnd)
    {
        Log("save-window-not-found | %hs", W2U8(why).c_str());
        WriteResp(seq, L"SAVE", L"ERR", L"window-not-found " + why);
        return;
    }

    std::wstring title = WndTitle(hwnd);
    bool modified = false; std::wstring fullPath;
    FindDocState(ExtractBaseName(title), fullPath, modified);
    Log("MOD.state | modified=%d file=%s", modified ? 1 : 0, W2U8(title).c_str());

    SendMessageW(hwnd, WM_COMMAND, kIdFileSave, 0);
    Log("save-done | 当前图纸已投 CX_save 同路保存 file=%s", W2U8(title).c_str());
    WriteResp(seq, L"SAVE", L"OK", L"save-done");
}

/// <summary>注入一条白名单内的宿主命令。sendStringToExecute 只代表已投出，不代表执行完。</summary>
static void DoExecNative(int seq, const std::wstring& arg)
{
    if (arg.empty())
    {
        WriteResp(seq, L"EXEC_NATIVE", L"ERR", L"EXEC_NATIVE 缺少 arg（要注入的宿主命令串）");
        Log("bridge: EXEC_NATIVE 缺 arg，已拒绝");
        return;
    }
    bool allowed = false;
    for (size_t i = 0; i < _countof(kNativeCmdPrefixes); ++i)
    {
        const wchar_t* p = kNativeCmdPrefixes[i];
        if (arg.compare(0, wcslen(p), p) == 0) { allowed = true; break; }
    }
    if (!allowed)
    {
        WriteResp(seq, L"EXEC_NATIVE", L"ERR",
                  L"命令未被 CRX 认可（前缀不在白名单 CX_DFT_/CX_ 内），已拒绝注入");
        Log("bridge: exec REJECT（白名单外）arglen=%d", (int)arg.size());
        return;
    }
    if (!crxDocManager || !crxDocManager->mdiActiveDocument())
    {
        WriteResp(seq, L"EXEC_NATIVE", L"ERR", L"无活动图纸文档，无法注入命令");
        Log("bridge: exec NO-DOC cmd=%hs", W2U8(arg).c_str());
        return;
    }

    std::wstring cmd = arg;
    if (cmd.back() != L' ' && cmd.back() != L'\n') cmd += L' ';   // 命令串以空白收尾
    crxDocManager->sendStringToExecute(crxDocManager->mdiActiveDocument(),
                                       cmd.c_str(), true, false, true);
    Log("exec: injected | cmd=%hs（仅保证已投出）", W2U8(cmd).c_str());
    WriteResp(seq, L"EXEC_NATIVE", L"OK", L"injected=" + arg);
}

//---------------------------------------------------------------------
// 命令表：C# 只能激活表内 verb；表外一律 ERR。needsConfirm/needsToken 语义照抄样板。
//---------------------------------------------------------------------
typedef void (*CmdHandler)(int seq, const std::wstring& arg);

struct CmdEntry
{
    const wchar_t* name;
    bool           needsConfirm;
    bool           needsToken;
    CmdHandler     handler;
    const wchar_t* desc;
};

static const CmdEntry kCmdTable[] =
{
    { L"PING",        false, false, DoPing,        L"握手/探活，message 带会话令牌" },
    { L"CLOSEDOC",    true,  true,  DoCloseDoc,    L"保存并关闭指定图纸（等 mtime 变再 WM_CLOSE）" },
    { L"CLOSERAW",    true,  true,  DoCloseRaw,    L"不保存直接关闭指定图纸窗口" },
    { L"SAVE",        true,  true,  DoSave,        L"只对当前图纸投保存" },
    { L"EXEC_NATIVE", false, true,  DoExecNative,  L"注入一条白名单内的宿主命令串" },
    { L"TITLEBLOCK_3DSET", false, true, DoTitleBlock3DSet,
                                            L"标题栏 3D 信息设默认：4 通道轮换 + TB.diff 判定（结果落日志 REQ.tb-*）" },
    { L"TB_ABORT",         false, true, DoTbAbort,
                                            L"终止标题栏 3D 信息派发与判定（收尾必投）" },
};

static const CmdEntry* FindCmd(const std::wstring& action)
{
    for (size_t i = 0; i < _countof(kCmdTable); ++i)
        if (action == kCmdTable[i].name) return &kCmdTable[i];
    return NULL;
}

//---------------------------------------------------------------------
// 节拍主体：安全开关 → 收尾状态机 → 取请求 → 三道闸 → 执行
//---------------------------------------------------------------------
static void BridgeTick()
{
    if (g_inTick) return;          // 重入闸（收尾等待期间不被二次进入）
    g_inTick = true;

    // 安全开关：存在即停桥轮询。请求**未被消费、留在盘上**（规则 11 节 25 明示不算违规）。
    wchar_t killPath[MAX_PATH];
    TempPath(killPath, MAX_PATH, kKillFile);
    if (FileExists(killPath))
    {
        if (!g_killSeen)
        {
            g_killSeen = true;
            Log("bridge: 检测到安全开关 %hs，停止桥轮询", W2U8(killPath).c_str());
            if (g_hDispWnd) KillTimer(g_hDispWnd, kBridgeTimerId);
        }
        g_inTick = false;
        return;
    }

    // 「标题栏设默认」状态机每拍都推进（放在收尾状态机之前；kill 期间不跑）。
    // 它只动自己那组 g_tb* 状态，与 CLOSEDOC 的 g_job 互不干扰。
    TitleBlockTick();

    // 收尾状态机优先推进（一张图纸收尾期间不取新请求 —— C# 是同步一问一答，
    // 请求留在盘上等这一笔做完，天然排队，不算丢）
    if (g_job.stage != kJobNone)
    {
        JobTick();
        g_inTick = false;
        return;
    }

    wchar_t reqPath[MAX_PATH];
    TempPath(reqPath, MAX_PATH, kReqFile);
    if (!FileExists(reqPath)) { g_inTick = false; return; }   // 空队列：不算错误、不写回包

    std::wstring content;
    if (!ReadUtf8File(reqPath, content))
    {
        // ★ 堵掉样板路径 1（读失败 → 直接删文件不回包）：
        //   前 kReadFailMaxRetry 次不删、留在盘上下拍重试（C# 原子写入后必然可读）；
        //   连续超限则视为消费 ⇒ 必须落 parse-failed 回包，绝不静默。
        ++g_readFailStreak;
        if (g_readFailStreak <= kReadFailMaxRetry)
        {
            Log("tick: read-retry %d/%d（请求未消费，留在盘上）",
                g_readFailStreak, kReadFailMaxRetry);
            g_inTick = false;
            return;
        }
        DeleteFileW(reqPath);
        g_readFailStreak = 0;
        Log("tick: consumed seq=0（请求连续读失败，按消费处理）");
        WriteResp(0, L"", L"ERR", L"parse-failed request unreadable（连续读取失败已消费）");
        g_inTick = false;
        return;
    }
    g_readFailStreak = 0;
    DeleteFileW(reqPath);   // 自此请求已被消费 ⇒ 后面每一条出口都必须落回包

    int seq = 0; std::wstring action, token, arg; bool confirm = false;
    if (!ParseRequest(content, seq, action, confirm, token, arg))
    {
        // ★ 堵掉样板路径 2（解析失败 → 不回包）：连 seq 都拿不到也回
        //   seq=0 + ERR + parse-failed，让 C# 读成「协议错」而不是「桥死」。
        Log("tick: consumed seq=0（请求解析失败）");
        WriteResp(0, action, L"ERR", L"parse-failed missing seq or action");
        g_inTick = false;
        return;
    }

    Log("tick: consumed seq=%d action=%hs", seq, W2U8(action).c_str());
    Log("REQ.ack | seq=%d action=%hs", seq, W2U8(action).c_str());   // 消费回执（先于执行）

    if (seq <= g_lastSeq)
    {
        // ★ 堵掉样板路径 3（seq<=lastSeq 判过期 → 不回包）：请求已消费，
        //   照样落一条 ERR，C# 端「重发同 seq」与「过期残留」都拿得到判定。
        WriteResp(seq, action, L"ERR",
                  L"stale-or-duplicate seq=" + std::to_wstring(seq) +
                  L" lastSeq=" + std::to_wstring(g_lastSeq));
        Log("bridge: 过期/重复请求 seq=%d lastSeq=%d，已拒绝", seq, g_lastSeq);
        g_inTick = false;
        return;
    }
    g_lastSeq = seq;

    // 闸一：action 必须在命令表里
    const CmdEntry* pCmd = FindCmd(action);
    if (!pCmd)
    {
        WriteResp(seq, action, L"ERR",
                  L"CRX 不认可该命令：" + action +
                  L"（可用：PING / CLOSEDOC / CLOSERAW / SAVE / EXEC_NATIVE / TITLEBLOCK_3DSET / TB_ABORT）");
        Log("bridge: 未知/未登记命令「%hs」，已拒绝", W2U8(action).c_str());
        g_inTick = false;
        return;
    }

    // 闸二：需要令牌的命令只认本次加载生成的会话令牌（上会话残留请求在这里被挡住）
    if (pCmd->needsToken && token != g_token)
    {
        WriteResp(seq, action, L"ERR",
                  L"会话令牌不匹配，已拒绝执行（请重新 PING 握手；这可能是上次会话的残留请求）");
        Log("bridge: 令牌不匹配 action=%hs got=%hs want=%hs，已拒绝",
            W2U8(action).c_str(), W2U8(token).c_str(), W2U8(g_token).c_str());
        g_inTick = false;
        return;
    }

    // 闸三：破坏性命令双保险 confirm=yes
    if (pCmd->needsConfirm && !confirm)
    {
        WriteResp(seq, action, L"ERR", L"破坏性命令未带 confirm=yes，已拒绝（防伪造请求）");
        Log("bridge: %hs 未带 confirm，已拒绝", W2U8(action).c_str());
        g_inTick = false;
        return;
    }

    Log("bridge: 执行命令 %hs（seq=%d）", W2U8(action).c_str(), seq);
    pCmd->handler(seq, arg);
    g_inTick = false;
}

//---------------------------------------------------------------------
// 就绪探针：RUNNING + 会话令牌 + pid。C# 判「marker 写入时间 >= 本进程启动时间」。
// 卸载一律摘掉（WriteMarker(false)）——桥接型 marker 语义 = 「桥活着」，
// 与入口型「这份代码还能用」相反，不许留残留行（坑 187）。
//---------------------------------------------------------------------
static void WriteMarker(bool on)
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, kMarkerFile);
    if (!on) { DeleteFileW(path); return; }

    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { Log("marker: create failed err=%lu", GetLastError()); return; }
    std::wstring s = L"RUNNING token=" + g_token +
                     L" pid=" + std::to_wstring((unsigned long)GetCurrentProcessId()) + L"\r\n";
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0, NULL, NULL);
    if (n > 0)
    {
        std::vector<char> buf(n);
        WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), &buf[0], n, NULL, NULL);
        DWORD written = 0;
        WriteFile(h, &buf[0], (DWORD)n, &written, NULL);
    }
    CloseHandle(h);
    Log("marker: ready token=%hs", W2U8(g_token).c_str());
}

//---------------------------------------------------------------------
// message-only 窗口：只挂文件桥定时器。零可见窗口、零 UI。
// WNDCLASS.hInstance 用本模块 ModuleResourceInstance()（规则 11 节 21，
// 不用 GetModuleHandleW(NULL)，窗口过程在 DLL 里、类归属也应在 DLL）。
//---------------------------------------------------------------------
static LRESULT CALLBACK DispWndProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_TIMER && wp == kBridgeTimerId) { BridgeTick(); return 0; }
    return DefWindowProcW(hWnd, msg, wp, lp);
}

static bool CreateDispatcherWindow()
{
    static bool s_classRegistered = false;
    const wchar_t* kClass = L"caxa3dBatchMasterViewBridgeWnd";

    HINSTANCE hInst = Caxa3dBatchMasterViewDLL.ModuleResourceInstance();
    if (!hInst) hInst = GetModuleHandleW(NULL);
    if (!s_classRegistered)
    {
        WNDCLASSEXW wc;
        ZeroMemory(&wc, sizeof(wc));
        wc.cbSize        = sizeof(wc);
        wc.lpfnWndProc   = DispWndProc;
        wc.hInstance     = hInst;
        wc.lpszClassName = kClass;
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
        s_classRegistered = true;
    }

    g_hDispWnd = CreateWindowExW(0, kClass, L"caxa3dBatchMasterViewBridge", 0, 0, 0, 0, 0,
                                 HWND_MESSAGE, NULL, hInst, NULL);
    return g_hDispWnd != NULL;
}

static void StartBridge()
{
    if (!CreateDispatcherWindow())
    {
        Log("init: dispatcher window failed err=%lu", GetLastError());
        return;
    }
    Log("init: dispatcher window=%p class=caxa3dBatchMasterViewBridgeWnd", g_hDispWnd);
    SetTimer(g_hDispWnd, kBridgeTimerId, kBridgeTimerMs, NULL);
    Log("init: passive command bridge active (timer=%u ms, no UI/no command-registered)",
        kBridgeTimerMs);
}

// 桥接型卸载第一步：KillTimer + DestroyWindow 都必须做——
// 漏 DestroyWindow ⇒ 窗口过程还注册在系统里、镜像已卸载 ⇒ 定时器跳进已卸载地址（坑 187）。
static void StopBridge()
{
    if (g_hDispWnd) KillTimer(g_hDispWnd, kBridgeTimerId);
    if (g_hDispWnd) { DestroyWindow(g_hDispWnd); g_hDispWnd = NULL; }
    Log("stop: bridge torn down");
}

//---------------------------------------------------------------------
// 自动加载专有风险：清上次会话残留的请求/回包 + seq 归零 + 生成本次会话令牌
//   ① 令牌每次加载都变 ⇒ 残留 confirm=yes 请求即使漏清也对不上（结构保证）；
//   ② 清残在挂定时器之前 ⇒ 不存在「定时器已开、残留请求被取走」的窗口期。
//---------------------------------------------------------------------
static void PurgeStaleRequests()
{
    wchar_t p[MAX_PATH];
    TempPath(p, MAX_PATH, kReqFile);  DeleteFileW(p);
    TempPath(p, MAX_PATH, kRespFile); DeleteFileW(p);
    g_lastSeq = 0;
}

static void InitSessionToken()
{
    wchar_t buf[64];
    _snwprintf_s(buf, 64, _TRUNCATE, L"%lu-%lu",
                 (unsigned long)GetCurrentProcessId(), (unsigned long)GetTickCount());
    g_token = buf;
}

//---------------------------------------------------------------------
// 模块入口（随 CxAutorun.cx 自动加载；本函数只做准备工作：
// 生成令牌 → 写 marker → 清残 → 基类 → 开日志 → 挂 reactor → 挂定时器。
// 不执行任何命令、不做业务判断、不碰 UI、不注册命令。）
//---------------------------------------------------------------------
class CCaxa3dBatchMasterViewApp : public AcRxArxApp
{
public:
    CCaxa3dBatchMasterViewApp() : AcRxArxApp() {}

    virtual AcRx::AppRetCode On_kInitAppMsg(void* pkt) override
    {
        // ── ① 最早期探针：令牌 + marker 只用纯 Win32 写盘，不依赖 MFC/CS/日志等
        //    任何全局状态；哪怕基类初始化崩了，marker 也已落地（上次闪退的教训：
        //    连 On_kInitAppMsg 都没写进日志，只能靠 marker 判「桥到底加载没」）。
        InitSessionToken();
        WriteMarker(true);

        // ── ② 清残留请求/回包 + seq 归零（必须在挂定时器之前）──
        PurgeStaleRequests();

        AcRx::AppRetCode ret = AcRxArxApp::On_kInitAppMsg(pkt);

        // ── ③ 开日志，挂 reactor（commandEnded 判据行），开桥（100 ms 节拍）──
        InitializeCriticalSection(&g_logCS);
        g_logReady = true;
        LogOpen();
        Log("MOD: On_kInitAppMsg (auto-loaded passive bridge), exe pid=%lu module=caxa3dBatchMasterView",
            (unsigned long)GetCurrentProcessId());
        Log("marker: ready");
        Log("init: stale request/response purged, lastSeq reset, token=%hs",
            W2U8(g_token).c_str());
        g_pEditorReactor = new CBatchMasterViewReactor();
        EnsureReactorsAttached("init");
        StartBridge();
        return ret;
    }

    // 桥接型不 pin、卸载顺序固定：StopBridge（KillTimer+DestroyWindow）→
    // 摘 marker → 关日志 → DeleteCriticalSection → 基类。逐字对齐规则 11 节 27 ②。
    virtual AcRx::AppRetCode On_kUnloadAppMsg(void* pkt) override
    {
        Log("MOD: On_kUnloadAppMsg | CRX module unloading");
        StopBridge();
        WriteMarker(false);
        if (g_pEditorReactor)
        {
            CRxEditor* pEditor = crxedEditor;
            if (pEditor) { try { pEditor->removeReactor(g_pEditorReactor); } catch (...) {} }
            delete g_pEditorReactor;
            g_pEditorReactor = NULL;
        }
        g_logReady = false;
        if (g_hLog != INVALID_HANDLE_VALUE) { CloseHandle(g_hLog); g_hLog = INVALID_HANDLE_VALUE; }
        DeleteCriticalSection(&g_logCS);
        return AcRxArxApp::On_kUnloadAppMsg(pkt);
    }

    virtual void RegisterServerComponents() override {}
};

IMPLEMENT_ARX_ENTRYPOINT(CCaxa3dBatchMasterViewApp)
