﻿//=====================================================================
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
//   CONVERT_DXF  confirm=是 token=是   —— TDExportControl.Export 转 dxf/dwg/pdf
//                （★ 2027 实机 GetIDispatch(true) 恒 NULL 已判死=规则总纲 29②，仅留作拍板候选）。
//   OPEN_EXB     confirm=是 token=是   —— 图纸环境侧打开/激活 exb（A 文档管理器 /
//                B 键入 CX_open_Impl / C 原生命令三通道，判成功只认文档在册）；
//                业务 exb 打开的唯一入口，替代 Scene 侧 OpenFile2 退役路线。
//   GETSCALE     confirm=否 token=否   —— 当前图纸比例+图幅宽高（PaperTable），二维码比例补偿用。
//   BITMAP_NATIVE confirm=是 token=是  —— 投宿主 CX_DFT_InsertBitmap 插图（回包先落盘再投命令）。
//   QR_STATE     confirm=否 token=否   —— 模型空间实体计数增量，delta>0 = 插图成功唯一权威判据。
//   LISTENTS     confirm=否 token=否   —— 只读 dump 模型空间类名分布 + imageRef 计数。
//   DELETE_QR    confirm=是 token=是   —— arg="x,y"：按图片实体包围盒精确删除目标点上的旧二维码。
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
#include "dbapserv.h"      // crxdbHostApplicationServices（QR_STATE/GETSCALE 用，2026-11-15 移植自 caxa3dqrcode_bridge）
#include "dbsymtb.h"       // CRxDbBlockTable / CRxDbPaperTable（模型空间计数 / 图纸比例；安装目录头为准）
#include "dbents.h"
#include "dbImageRef.h"    // CRxDbImageRef（图片实体）：LISTENTS/DELETE_QR 按类判定的依据（caxa3dqrcode 11-15 实机坐实）

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

// 「工程图附加二维码」（2026-11-15 移植自 caxa3dqrcode_bridge，11-15 实机整链 verified）：
// BITMAP_NATIVE 投宿主 CX_DFT_InsertBitmap 让 CAXA 自己插图，实体由宿主命令收养，
// 所以 QR_STATE 的 modelSpace 计数增量是权威判据（自绘等宽线 = 假成功，caxa3dqrcode 19:11 实机已判死）。
static bool              g_qrNativeMode = false;
static volatile LONG     g_qrPending = 0;
static int               g_qrBefore = -1, g_qrAfter = -1, g_qrDelta = -1;

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

// ★ 文件名去扩展名（取 stem）：窗口标题在存盘后常去掉 .exb，只靠全名匹配会失配 → CLOSEDOC 找不到窗口、exb 关不掉。
//   stem 始终稳定出现在标题里。
static std::wstring DocStemOf(const std::wstring& f)
{
    size_t dot = f.find_last_of(L'.');
    if (dot == std::wstring::npos || dot == 0) return f;
    return f.substr(0, dot);
}
// 标题是否命中目标文档：全名（含 .exb）或 stem 任一命中即可（不区分大小写，与 ContainsNoCase 一致）。
static bool TitleMatchesDoc(const std::wstring& title, const std::wstring& baseName)
{
    if (baseName.empty()) return true;
    if (ContainsNoCase(title, baseName)) return true;
    std::wstring stem = DocStemOf(baseName);
    return !stem.empty() && ContainsNoCase(title, stem);
}

/// <summary>按文件名（只比文件名）找那张图纸的 MDI 子窗口；找不到绝不退化。</summary>
static HWND FindDrawingWindowByFile(const std::wstring& baseName, std::wstring& why)
{
    // ★ 绝不按进程(pid)过滤：exb 图纸窗口属于 Draft 进程(CDRAFT_M_IC.exe)，与加载本 CRX 的
    // IronCAD.exe 不是同一 pid。按 pid 过滤会跳过目标 exb ⇒ 找不到、关不掉（2026-11-08 实证）。
    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        if (!IsWindowVisible(h)) continue;
        HWND mdi = LocateMdiClient(h);
        if (!mdi) continue;
        for (HWND c = GetWindow(mdi, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT))
        {
            // 精确按标题命中 exb 文件名/stem；不再强求窗口类含 AfxFrameOrView
            // （Draft 进程视图类可能与 IronCAD 侧不同，按类卡死会漏关 exb）。
            if (TitleMatchesDoc(WndTitle(c), baseName)) return c;
        }
    }
    why = L"没有标题含该文件名的 MDI 子窗口";
    return NULL;
}

/// <summary>找当前（活动）图纸窗口：WM_MDIGETACTIVE → 兜底第一个 AfxFrameOrView 子窗。</summary>
static HWND FindActiveDrawingWindow(std::wstring& why)
{
    for (HWND h = GetTopWindow(NULL); h; h = GetWindow(h, GW_HWNDNEXT))
    {
        // ★ 绝不按进程(pid)过滤：见 FindDrawingWindowByFile 注释。exb 在 Draft 进程，本 CRX 在
        // IronCAD 进程，按 pid 过滤会漏掉目标 exb（2026-11-08 实证）。
        if (!IsWindowVisible(h)) continue;
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
                       L"; verbs=PING,CLOSEDOC,CLOSERAW,SAVE,EXEC_NATIVE,TITLEBLOCK_3DSET,TB_ABORT,CONVERT_DXF,OPEN_EXB";
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
// 「工程图附加二维码」三命令（2026-11-15 移植自 caxa3dqrcode_bridge.cpp，
// 11-15 实机整链 verified：GETSCALE → 文件框代填 → 图像框代填 → delta=1 → SAVE）。
//   GETSCALE      —— 读当前图纸 PaperTable.CurrentPaper 的 getPaperScale/getPaperSize
//                    （比例 + 图幅物理宽高 mm），C# 侧按分母做比例补偿与定位规则换算。
//   BITMAP_NATIVE —— 投宿主 CX_DFT_InsertBitmap（Commands.xml:2318 实证 Identify），
//                    回包 OK 只代表已投出；先回包再 sendStringToExecute（对话框占住
//                    UI 线程时 BridgeTick 不返回，C# 必须先拿到回包才进后台代点）。
//   QR_STATE      —— 实时重数模型空间实体（native 模式），delta>0 = 唯一权威成功判据。
//   LISTENTS      —— 只读 dump 模型空间类名分布 + imageRef 计数（先删后附的判定取证）。
//   DELETE_QR     —— arg="x,y"：按图片实体包围盒命中目标点 ±1mm 精确删除旧二维码
//                    （两趟纪律：先只读收集 entityId，再 kForWrite open → erase）。
//   头文件取证：dbsymtb.h(1718/1745) 小写 CurrentPaper()/getPaperScale/getPaperSize
//   与 lib 符号一致；C:\code\ICAPI\CRX\Inc\dbPaper.h 是旧文档，不能作编译依据。
//---------------------------------------------------------------------
static int CountModelSpaceEntities()
{
    CRxDbDatabase* pDb = crxdbHostApplicationServices()
                       ? crxdbHostApplicationServices()->workingDatabase() : NULL;
    if (!pDb) return -1;
    CRxDbBlockTable* pTable = NULL;
    if (pDb->getSymbolTable(pTable, CRxDb::kForRead) != CDraft::eOk || !pTable) return -1;
    CRxDbBlockTableRecord* pRec = NULL;
    CDraft::ErrorStatus es = pTable->getAt(CRXDB_MODEL_SPACE, pRec, CRxDb::kForRead);
    pTable->close();
    if (es != CDraft::eOk || !pRec) return -1;
    CRxDbBlockTableRecordIterator* pItr = NULL;
    if (pRec->newIterator(pItr, true) != CDraft::eOk || !pItr) { pRec->close(); return -1; }
    int n = 0;
    for (; !pItr->done(); pItr->step()) ++n;
    delete pItr;
    pRec->close();
    return n;
}

static void DoGetScale(int seq, const std::wstring& /*arg*/)
{
    CRxDbDatabase* pDb = crxdbHostApplicationServices()
                       ? crxdbHostApplicationServices()->workingDatabase() : NULL;
    if (!pDb) { WriteResp(seq, L"GETSCALE", L"ERR", L"no-working-db（图纸文档未就绪）"); return; }
    CRxDbPaperTable* pPaper = NULL;
    if (pDb->getPaperTable(pPaper, CRxDb::kForRead) != CDraft::eOk || !pPaper)
    { WriteResp(seq, L"GETSCALE", L"ERR", L"paper-table-not-openable"); return; }
    int n = pPaper->getPaperCount();
    CRxDbPaperTableRecord* pRec = pPaper->CurrentPaper();
    double d = 0.0;
    double pw = 0.0, ph = 0.0;   // 图幅物理宽高（mm），供 C# 匹配「A3横」等定位规则
    if (pRec)
    {
        pRec->getPaperScale(d);
        pRec->getPaperSize(pw, ph);
    }
    pPaper->close();   // 与 CountModelSpaceEntities 的 BlockTable 同纪律
    wchar_t buf[64], bw[64], bh[64];
    swprintf(buf, 64, L"%.6g", d);
    swprintf(bw, 64, L"%.6g", pw);
    swprintf(bh, 64, L"%.6g", ph);
    if (d > 1e-9)
        WriteResp(seq, L"GETSCALE", L"OK",
            std::wstring(L"scale=") + buf +
            L"|papers=" + std::to_wstring(n) +
            L"|w=" + bw + L"|h=" + bh +
            L"|authority=PaperTable.CurrentPaper.getPaperScale.getPaperSize");
    else
        WriteResp(seq, L"GETSCALE", L"ERR",
            std::wstring(L"paper-scale-invalid: raw=") + buf +
            L"|papers=" + std::to_wstring(n) + L"（读不到=第三态，不折算）");
}

//---------------------------------------------------------------------
// LISTENTS（移植自 caxa3dqrcode_bridge 11-15：「插入位置已有二维码要先删再附」）
//   只读取证：枚举模型空间每实体 isA()->name()（CxCHAR=wchar_t 零转码），统计类名分布
//   + 按 CRxDbImageRef::desc() isKindOf 复核（dbImageRef.h，图片实体）。
//   绝不写库、不 erase、不投命令；读不到实体如实回报（第三态）。
//---------------------------------------------------------------------
static void DoListEnts(int seq, const std::wstring& /*arg*/)
{
    CRxDbDatabase* pDb = crxdbHostApplicationServices()
                       ? crxdbHostApplicationServices()->workingDatabase() : NULL;
    if (!pDb) { WriteResp(seq, L"LISTENTS", L"ERR", L"no-working-db（图纸文档未就绪）"); return; }
    CRxDbBlockTable* pTable = NULL;
    if (pDb->getSymbolTable(pTable, CRxDb::kForRead) != CDraft::eOk || !pTable)
    { WriteResp(seq, L"LISTENTS", L"ERR", L"block-table-not-openable"); return; }
    CRxDbBlockTableRecord* pRec = NULL;
    CDraft::ErrorStatus es = pTable->getAt(CRXDB_MODEL_SPACE, pRec, CRxDb::kForRead);
    pTable->close();
    if (es != CDraft::eOk || !pRec)
    { WriteResp(seq, L"LISTENTS", L"ERR", L"model-space-not-openable"); return; }
    CRxDbBlockTableRecordIterator* pItr = NULL;
    if (pRec->newIterator(pItr, true) != CDraft::eOk || !pItr)
    { pRec->close(); WriteResp(seq, L"LISTENTS", L"ERR", L"iterator-failed"); return; }

    int total = 0, openFail = 0, imgRef = 0;
    std::vector<std::pair<std::wstring, int> > tally;   // 类名 x 计数（种类有限，线性查找）
    const CRxClass* pImgDesc = CRxDbImageRef::desc();   // rxInit 未跑时可能为 NULL，下面判空
    for (; !pItr->done(); pItr->step())
    {
        ++total;
        CRxDbEntity* pEnt = NULL;
        if (pItr->getEntity(pEnt, CRxDb::kForRead, false) != CDraft::eOk || !pEnt) { ++openFail; continue; }
        std::wstring cls = L"<null>";
        if (pEnt->isA() && pEnt->isA()->name()) cls = pEnt->isA()->name();
        if (pImgDesc && pEnt->isKindOf(pImgDesc)) ++imgRef;
        bool found = false;
        for (size_t i = 0; i < tally.size(); ++i)
            if (tally[i].first == cls) { ++tally[i].second; found = true; break; }
        if (!found) tally.push_back(std::make_pair(cls, 1));
        pEnt->close();   // 读模式 open 后必须 close（与 CountModelSpaceEntities 同纪律）
    }
    delete pItr;
    pRec->close();

    std::wstring ents;
    for (size_t i = 0; i < tally.size(); ++i)
    {
        if (i) ents += L"; ";
        ents += tally[i].first + L" x" + std::to_wstring(tally[i].second);
    }
    std::wstring msg = L"total=" + std::to_wstring(total) +
                       L"|openFail=" + std::to_wstring(openFail) +
                       L"|imageRef=" + std::to_wstring(imgRef) +
                       L"|descReady=" + (pImgDesc ? L"1" : L"0") +
                       L"|ents=" + ents;
    Log("listents: %hs", W2U8(msg).c_str());
    WriteResp(seq, L"LISTENTS", L"OK", msg);
}

//---------------------------------------------------------------------
// DELETE_QR（移植自 caxa3dqrcode_bridge 11-15：「能够直接识别放置点上的图片,选中删除么」
//   → 可行、已实机验证）：arg="x,y"（图纸坐标系目标插入点，C# 由定位规则/兜底值算出）。
//   两趟纪律：第一趟只读（kForRead + getGeomExtents 判命中，收集 entityId），
//   第二趟 kForWrite open → erase(true)。绝不先删后看、不盲删。
//   命中判定：目标点 ±1mm 扩边落在实体包围盒内（插入点 = 图片锚点，通常即包围盒左下角）。
//   getGeomExtents 是 CRxDbEntity 基类虚函数（dbmain.h:462），CRxDbImageRef 空壳类不影响；
//   CRxDbExtents::minPoint/maxPoint（dbmain.h:120/121）→ CRxGePoint3d public x/y。
//   deleted=0 = 正常首插不算错；extents 读不到 = 跳过不盲删（如实回报）。
//   ★ 坑 45：erase() 返回值不可信（回 err 但实体实际已删）——本回包只作取证，
//     C# 侧不拿 deleted/eraseFail 做成功判据，成败仍由 QR_STATE delta + SAVE 裁定。
//---------------------------------------------------------------------
static void DoDeleteQr(int seq, const std::wstring& arg)
{
    double tx = 0.0, ty = 0.0;
    {
        size_t c = arg.find(L',');
        if (c == std::wstring::npos)
        { WriteResp(seq, L"DELETE_QR", L"ERR", L"bad-arg: need \"x,y\""); return; }
        tx = _wtof(arg.substr(0, c).c_str());
        ty = _wtof(arg.substr(c + 1).c_str());
    }
    const double kTol = 1.0;   // mm 容差（扩边）

    CRxDbDatabase* pDb = crxdbHostApplicationServices()
                       ? crxdbHostApplicationServices()->workingDatabase() : NULL;
    if (!pDb) { WriteResp(seq, L"DELETE_QR", L"ERR", L"no-working-db（图纸文档未就绪）"); return; }
    CRxDbBlockTable* pTable = NULL;
    if (pDb->getSymbolTable(pTable, CRxDb::kForRead) != CDraft::eOk || !pTable)
    { WriteResp(seq, L"DELETE_QR", L"ERR", L"block-table-not-openable"); return; }
    CRxDbBlockTableRecord* pRec = NULL;
    CDraft::ErrorStatus es = pTable->getAt(CRXDB_MODEL_SPACE, pRec, CRxDb::kForRead);
    pTable->close();
    if (es != CDraft::eOk || !pRec)
    { WriteResp(seq, L"DELETE_QR", L"ERR", L"model-space-not-openable"); return; }
    CRxDbBlockTableRecordIterator* pItr = NULL;
    if (pRec->newIterator(pItr, true) != CDraft::eOk || !pItr)
    { pRec->close(); WriteResp(seq, L"DELETE_QR", L"ERR", L"iterator-failed"); return; }

    const CRxClass* pImgDesc = CRxDbImageRef::desc();
    std::vector<CRxDbObjectId> hits;
    int imgRef = 0, extentsFail = 0;
    std::wstring detail;
    for (; !pItr->done(); pItr->step())
    {
        CRxDbEntity* pEnt = NULL;
        if (pItr->getEntity(pEnt, CRxDb::kForRead, false) != CDraft::eOk || !pEnt) continue;
        if (pImgDesc && pEnt->isKindOf(pImgDesc))
        {
            ++imgRef;
            CRxDbExtents ext;
            CDraft::ErrorStatus es2 = pEnt->getGeomExtents(ext);
            bool hit = false;
            if (es2 == CDraft::eOk)
            {
                CRxGePoint3d mn = ext.minPoint(), mx = ext.maxPoint();
                Log("delete_qr: image min=(%.3f,%.3f) max=(%.3f,%.3f) target=(%.3f,%.3f)",
                    mn.x, mn.y, mx.x, mx.y, tx, ty);
                if (mx.x >= mn.x && mx.y >= mn.y &&
                    tx >= mn.x - kTol && tx <= mx.x + kTol &&
                    ty >= mn.y - kTol && ty <= mx.y + kTol)
                    hit = true;
            }
            else ++extentsFail;
            if (hit)
            {
                CRxDbObjectId id;
                if (pItr->getEntityId(id) == CDraft::eOk) hits.push_back(id);
                if (detail.size()) detail += L"; ";
                detail += L"id@" + std::to_wstring(hits.size());
            }
        }
        pEnt->close();   // 与 DoListEnts 同纪律：读模式 open 后必须 close
    }
    delete pItr;
    pRec->close();

    int deleted = 0, eraseFail = 0, openErr = 0, eraseErr = 0;
    for (size_t i = 0; i < hits.size(); ++i)
    {
        CRxDbEntity* pEnt = NULL;
        CDraft::ErrorStatus esOpen = crxdbOpenCRxDbEntity(pEnt, hits[i], CRxDb::kForWrite);
        if (esOpen != CDraft::eOk || !pEnt)
        {
            ++eraseFail; ++openErr;
            Log("delete_qr: erase open-for-write FAILED err=%d（0=成功以外的错误码取证）", (int)esOpen);
            continue;
        }
        CDraft::ErrorStatus esE = pEnt->erase(true);
        if (esE == CDraft::eOk) ++deleted;
        else
        {
            ++eraseFail; ++eraseErr;
            Log("delete_qr: erase FAILED err=%d（锁定层/只读/句柄态取证）", (int)esE);
        }
        pEnt->close();
    }
    Log("delete_qr: target=(%.3f,%.3f) imageRef=%d extentsFail=%d deleted=%d eraseFail=%d openErr=%d eraseErr=%d",
        tx, ty, imgRef, extentsFail, deleted, eraseFail, openErr, eraseErr);

    WriteResp(seq, L"DELETE_QR", L"OK",
        L"deleted=" + std::to_wstring(deleted) +
        L"|imageRef=" + std::to_wstring(imgRef) +
        L"|extentsFail=" + std::to_wstring(extentsFail) +
        L"|eraseFail=" + std::to_wstring(eraseFail) +
        L"|openErr=" + std::to_wstring(openErr) +
        L"|eraseErr=" + std::to_wstring(eraseErr) +
        L"|target=" + arg +
        L"|authority=geomExtentsContainment" + (detail.size() ? L"|hit=" + detail : L""));
}

static void DoQrState(int seq, const std::wstring& /*arg*/)
{
    // native 模式：实体由宿主命令异步生成，QR_STATE 必须实时重数，不能只读缓存。
    {
        int now = CountModelSpaceEntities();
        if (now >= 0)
        {
            g_qrAfter = now;
            g_qrDelta = (g_qrBefore >= 0) ? (now - g_qrBefore) : -1;
        }
        if (g_qrDelta > 0) InterlockedExchange(&g_qrPending, 0);
    }
    std::wstring msg = L"mode=" + std::wstring(g_qrNativeMode ? L"native" : L"legacy") +
                       L" before=" + std::to_wstring(g_qrBefore) +
                       L" after=" + std::to_wstring(g_qrAfter) +
                       L" delta=" + std::to_wstring(g_qrDelta) +
                       L" pending=" + std::to_wstring((long)InterlockedCompareExchange(&g_qrPending, 0, 0));
    WriteResp(seq, L"QR_STATE", L"OK", msg);
}

/// <summary>在 RegCmds 表里挑宿主命令名；查不到也按 Commands.xml 全名投（宿主命令不是自注册空条目）。</summary>
static const wchar_t* PickHostCmd(const wchar_t* const* cands, int n, const wchar_t* fallback, const char* tag)
{
    CRxEdCommandStack* pStack = crxedRegCmds;
    if (pStack)
        for (int i = 0; i < n; ++i)
        {
            CRxEdCommand* p = pStack->lookupCmd(cands[i], true, true, false);
            if (p) { Log("%s: native cmd lookup HIT %ls", tag, cands[i]); return cands[i]; }
        }
    Log("%s: native cmd lookup MISS（宿主命令不在 RegCmds 表，按 Commands.xml 全名投递）", tag);
    return fallback;
}

static void NativeDispatch(int seq, const wchar_t* action, const wchar_t* const* cands, int nCands,
                           const wchar_t* fallback, const char* tag)
{
    if (!crxDocManager || !crxDocManager->mdiActiveDocument())
    {
        WriteResp(seq, action, L"ERR", L"no-active-drawing-doc（未投递）");
        return;
    }
    int before = CountModelSpaceEntities();
    if (before < 0)
    {
        WriteResp(seq, action, L"ERR", L"db-not-ready（模型空间不可枚举，未投递）");
        return;
    }
    const wchar_t* cmd = PickHostCmd(cands, nCands, fallback, tag);
    g_qrBefore = before; g_qrAfter = before; g_qrDelta = 0;
    g_qrNativeMode = true;
    InterlockedExchange(&g_qrPending, 1);

    // 回包必须先落盘：sendStringToExecute 会弹「插入图片」文件框并占住 UI 线程，
    // BridgeTick 在对话框关闭前不会返回；C# 只有先拿到回包才会进后台代点等待。
    WriteResp(seq, action, L"OK",
        L"mode=native|cmd=" + std::wstring(cmd) + L"|before=" + std::to_wstring(before) +
        L"|check=QR_STATE|authority=modelSpaceCountDelta");
    Log("qr: native dispatched cmd=%ls before=%d（等 C# 后台线程代点宿主对话框）", cmd, before);

    std::wstring full = std::wstring(cmd) + L" ";
    CDraft::ErrorStatus es = crxDocManager->sendStringToExecute(
        crxDocManager->mdiActiveDocument(), full.c_str(), true, false, true);
    Log("qr: native sendStringToExecute es=%d", (int)es);
    if (es != CDraft::eOk)
        Log("qr: native send es!=eOk（可能 unknownCommand，看 _crx.log 前后是否真弹过目标对话框）");
}

static void DoBitmapNative(int seq, const std::wstring& /*arg*/)
{
    const wchar_t* cands[] = { L"CX_DFT_InsertBitmap", L"InsertBitmap" };
    NativeDispatch(seq, L"BITMAP_NATIVE", cands, 2, L"CX_DFT_InsertBitmap", "bmp");
}

//---------------------------------------------------------------------
// 命令表：C# 只能激活表内 verb；表外一律 ERR。needsConfirm/needsToken 语义照抄样板。
//---------------------------------------------------------------------
typedef void (*CmdHandler)(int seq, const std::wstring& arg);

//---------------------------------------------------------------------
// 「转换成 dxf/dwg/pdf」：CONVERT_DXF —— arg = <exb 全路径>|<输出全路径>|<eZExportDrawingType 整型>
//   走图纸环境自己的导出 API（IZDrawingDoc::TDExportControl → IZTDExportControl::Export），
//   把图纸转成 dxf/dwg/pdf（CAXA 图纸模块原生"输出/转换"通道）。
//   ★ PDF（type=19）先走专用管理器 IZDrawingDoc::TDPDFExportManager → ExportDrawing，失败再回落 TDExportControl。
//   ★ 全程 COM 后期绑定（GetIDsOfNames + Invoke），不依赖任何 Draft C++ 头文件/UUID；判成功以输出文件存在为准（C# 侧复核）。
//   （移植自 caxa3dButtonBridge DoConvertDxf，适配本桥 WriteResp/Log 约定；纯 API、无 UI）
//---------------------------------------------------------------------
static const IID kIID_IZDrawingDoc =
    {0xA7C6ECCB, 0xD616, 0x45F4, {0x8E, 0x79, 0xAF, 0x5D, 0xE6, 0x3D, 0x38, 0x95}};
static const IID kIID_IZTDExportControl =
    {0x179B1F88, 0x9FF3, 0x4B40, {0xA4, 0xA7, 0xCA, 0xC4, 0xAD, 0x19, 0x47, 0x7E}};

static std::wstring NormPath(const std::wstring& raw)
{
    std::wstring s = raw;
    size_t b = 0;
    while (b < s.size() && (s[b] == L' ' || s[b] == L'\t')) ++b;
    size_t e = s.size();
    while (e > b && (s[e-1]==L' '||s[e-1]==L'\t'||s[e-1]==L'\r'||s[e-1]==L'\n')) --e;
    s = s.substr(b, e-b);
    for (size_t i = 0; i < s.size(); ++i) { if (s[i]==L'/') s[i]=L'\\'; s[i]=(wchar_t)towlower(s[i]); }
    return s;
}
static std::wstring BaseNameOfC(const std::wstring& p)
{
    size_t k = p.find_last_of(L"\\/");
    return (k == std::wstring::npos) ? p : p.substr(k+1);
}
static void PumpMessages(int sleepMs)
{
    MSG m;
    while (PeekMessage(&m, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessage(&m); }
    if (sleepMs > 0) Sleep(sleepMs);
}
static std::wstring HrText(HRESULT h)
{
    wchar_t b[24];
    swprintf(b, 24, L"0x%08X", (unsigned long)h);
    return std::wstring(b);
}
static IDispatch* DispGetObject(IDispatch* p, const wchar_t* name, std::wstring& err)
{
    if (!p) { err = L"文档 IDispatch 为空"; return NULL; }
    LPOLESTR n[1]; n[0] = const_cast<LPOLESTR>(name);
    DISPID id = DISPID_UNKNOWN;
    HRESULT hr = p->GetIDsOfNames(IID_NULL, n, 1, LOCALE_USER_DEFAULT, &id);
    if (FAILED(hr)) { err = std::wstring(L"GetIDsOfNames(") + name + L") hr=" + HrText(hr); return NULL; }
    VARIANT v; VariantInit(&v);
    DISPPARAMS dp; memset(&dp, 0, sizeof(dp));
    hr = p->Invoke(id, IID_NULL, LOCALE_USER_DEFAULT, DISPATCH_PROPERTYGET, &dp, &v, NULL, NULL);
    if (FAILED(hr) || v.vt != VT_DISPATCH || v.pdispVal == NULL)
    {
        int vt = (int)v.vt;
        if (v.vt != VT_EMPTY) VariantClear(&v);
        err = std::wstring(L"取属性 ") + name + L" hr=" + HrText(hr) + L" vt=" + std::to_wstring(vt);
        return NULL;
    }
    IDispatch* r = v.pdispVal; v.vt = VT_EMPTY;
    return r;
}
static bool DispCallBstr(IDispatch* p, const wchar_t* name, const std::wstring& s, std::wstring& err)
{
    if (!p) { err = L"接口对象为空"; return false; }
    LPOLESTR n[1]; n[0] = const_cast<LPOLESTR>(name);
    DISPID id = DISPID_UNKNOWN;
    HRESULT hr = p->GetIDsOfNames(IID_NULL, n, 1, LOCALE_USER_DEFAULT, &id);
    if (FAILED(hr)) { err = std::wstring(L"GetIDsOfNames(") + name + L") hr=" + HrText(hr); return false; }
    VARIANT a; VariantInit(&a);
    a.vt = VT_BSTR; a.bstrVal = ::SysAllocString(s.c_str());
    DISPPARAMS dp; memset(&dp, 0, sizeof(dp));
    dp.cArgs = 1; dp.rgvarg = &a;
    hr = p->Invoke(id, IID_NULL, LOCALE_USER_DEFAULT, DISPATCH_METHOD, &dp, NULL, NULL, NULL);
    VariantClear(&a);
    if (FAILED(hr)) { err = std::wstring(L"调用 ") + name + L" hr=" + HrText(hr); return false; }
    return true;
}
static bool DraftExport(CRxApDocument* pDoc, const std::wstring& outp, int type, std::wstring& err)
{
    if (!pDoc) { err = L"目标文档为空"; return false; }
    IDispatch* pDisp = pDoc->GetIDispatch(true);
    if (!pDisp) { err = L"GetIDispatch(true) 返回空"; return false; }
    IDispatch* pTEC = NULL;
    HRESULT hr = E_FAIL;
    {
        std::wstring e1; pTEC = DispGetObject(pDisp, L"TDExportControl", e1);
        if (pTEC) Log("exportA: got TDExportControl by name");
        else Log("exportA: name dispatch failed -> try QI + slot 15");
    }
    if (!pTEC)
    {
        void* pDocI = NULL;
        hr = pDisp->QueryInterface(kIID_IZDrawingDoc, &pDocI);
        if (SUCCEEDED(hr) && pDocI)
        {
            typedef HRESULT (STDMETHODCALLTYPE *PFN_GetTEC)(void*, IDispatch**);
            PFN_GetTEC pfn = (PFN_GetTEC)(*(void***)pDocI)[15];
            IDispatch* pOut = NULL;
            hr = pfn(pDocI, &pOut);
            ((IUnknown*)pDocI)->Release();
            if (SUCCEEDED(hr) && pOut) pTEC = pOut;
            Log("exportB: QI(IZDrawingDoc)+slot15 hr=%hs", W2U8(HrText(hr)).c_str());
        }
        else Log("exportB: QI(IZDrawingDoc) hr=%hs", W2U8(HrText(hr)).c_str());
    }
    if (!pTEC) { pDisp->Release(); if (err.empty()) err = L"两条通道都取不到 TDExportControl"; return false; }
    void* pTecI = NULL;
    if (SUCCEEDED(pTEC->QueryInterface(kIID_IZTDExportControl, &pTecI)) && pTecI)
    {
        typedef HRESULT (STDMETHODCALLTYPE *PFN_Export)(void*, BSTR, long);
        PFN_Export pexp = (PFN_Export)(*(void***)pTecI)[7];
        BSTR b = ::SysAllocString(outp.c_str());
        hr = pexp(pTecI, b, (long)type);
        ::SysFreeString(b);
        ((IUnknown*)pTecI)->Release();
    }
    else
    {
        Log("exportC: QI(IZTDExportControl) failed, fallback to Export by name");
        DISPID disExp = DISPID_UNKNOWN;
        LPOLESTR en[1]; en[0] = const_cast<LPOLESTR>(L"Export");
        hr = pTEC->GetIDsOfNames(IID_NULL, en, 1, LOCALE_USER_DEFAULT, &disExp);
        if (SUCCEEDED(hr))
        {
            VARIANT args[2]; VariantInit(&args[0]); VariantInit(&args[1]);
            args[0].vt = VT_I4; args[0].lVal = type;
            args[1].vt = VT_BSTR; args[1].bstrVal = ::SysAllocString(outp.c_str());
            DISPPARAMS dp; memset(&dp, 0, sizeof(dp));
            dp.cArgs = 2; dp.rgvarg = args;
            hr = pTEC->Invoke(disExp, IID_NULL, LOCALE_USER_DEFAULT, DISPATCH_METHOD, &dp, NULL, NULL, NULL);
            VariantClear(&args[0]); VariantClear(&args[1]);
        }
        else Log("exportC: GetIDsOfNames(Export) hr=%hs", W2U8(HrText(hr)).c_str());
    }
    pTEC->Release(); pDisp->Release();
    Log("exportD: Export(type=%d) hr=%hs fileExists=%d", type, W2U8(HrText(hr)).c_str(), FileExists(outp.c_str())?1:0);
    if (FAILED(hr)) { err = L"Export hr=" + HrText(hr); return false; }
    return true;
}
static CRxApDocument* FindDocByPath(const std::wstring& wantNorm)
{
    if (!crxDocManager) return NULL;
    CRxApDocument* found = NULL;
    CRxApDocumentIterator* it = crxDocManager->newAcApDocumentIterator();
    if (it)
    {
        for (; !it->done(); it->step())
        {
            CRxApDocument* d = it->document();
            if (!d) continue;
            const CxCHAR* fn = d->fileName();
            if (!fn || !fn[0]) continue;
            if (NormPath(std::wstring(fn)) == wantNorm) { found = d; break; }
        }
        delete it;
    }
    return found;
}
static void DoConvertDxf(int seq, const std::wstring& arg)
{
    size_t p1 = arg.find(L'|');
    size_t p2 = (p1 == std::wstring::npos) ? std::wstring::npos : arg.find(L'|', p1 + 1);
    if (p1 == std::wstring::npos || p2 == std::wstring::npos)
    {
        WriteResp(seq, L"CONVERT_DXF", L"ERR", L"参数格式错误（应为 exb路径|输出路径|type）");
        return;
    }
    std::wstring exb  = arg.substr(0, p1);
    std::wstring outp = arg.substr(p1 + 1, p2 - p1 - 1);
    std::wstring typS = arg.substr(p2 + 1);
    if (exb.empty() || outp.empty())
    {
        WriteResp(seq, L"CONVERT_DXF", L"ERR", L"exb 路径/输出路径为空，已拒绝");
        return;
    }
    int type = _wtoi(typS.c_str());
    if (type < 0)
    {
        WriteResp(seq, L"CONVERT_DXF", L"ERR", L"type 解析失败：" + typS);
        return;
    }
    if (!crxDocManager)
    {
        WriteResp(seq, L"CONVERT_DXF", L"ERR", L"图纸文档管理器不可用（不在图纸环境？）");
        return;
    }
    std::wstring want = NormPath(exb);
    CRxApDocument* pTarget = FindDocByPath(want);
    if (!pTarget)
    {
        WriteResp(seq, L"CONVERT_DXF", L"ERR",
                  L"未找到目标图纸（应已由 C# 打开）：" + exb);
        Log("convert: 目标不在册 want=%hs", W2U8(want).c_str());
        return;
    }
    if (!pTarget->isQuiescent())
        Log("convert: %hs 不空闲，仍尝试 Export", W2U8(BaseNameOfC(want)).c_str());
    CRxApDocument* pActive = crxDocManager->mdiActiveDocument();
    if (pActive != pTarget)
        crxDocManager->activateDocument(pTarget, false);
    if (type == 19)
    {
        IDispatch* pDoc = pTarget->GetIDispatch(true);
        if (pDoc)
        {
            std::wstring err;
            IDispatch* pPdf = DispGetObject(pDoc, L"TDPDFExportManager", err);
            if (pPdf)
            {
                bool ok = DispCallBstr(pPdf, L"ExportDrawing", outp, err);
                pPdf->Release(); pDoc->Release();
                if (ok)
                {
                    WriteResp(seq, L"CONVERT_DXF", L"OK", L"PDF 导出完成（TDPDFExportManager.ExportDrawing）→ " + outp);
                    Log("convert: %hs -> %hs PDF OK", W2U8(want).c_str(), W2U8(outp).c_str());
                    return;
                }
            }
            else pDoc->Release();
            Log("convert: PDF TDPDFExportManager failed -> fallback TDExportControl type=19");
        }
    }
    std::wstring err;
    if (DraftExport(pTarget, outp, type, err))
    {
        WriteResp(seq, L"CONVERT_DXF", L"OK", L"导出完成（TDExportControl.Export type=" + std::to_wstring(type) + L"）→ " + outp);
        Log("convert: %hs -> %hs type=%d OK", W2U8(want).c_str(), W2U8(outp).c_str(), type);
    }
    else
    {
        WriteResp(seq, L"CONVERT_DXF", L"ERR", L"导出通道失败：" + err);
        Log("convert: %hs -> %hs type=%d 失败 %hs", W2U8(want).c_str(), W2U8(outp).c_str(), type, W2U8(err).c_str());
    }
}

//---------------------------------------------------------------------
// 「打开 exb」：arg = <exb 全路径>（移植自 caxa3dexbtracker ExbTrackerBridge DoOpenExb，
//                适配本桥 WriteResp(seq, action, status, msg) 约定）
//   ★★ 图纸环境侧打开 exb —— 替代 caxa3d(Scene) 侧 IZBaseApp.OpenFile2：
//     Scene 侧对业务 exb 是退役路线（Invalid file format 假失败 + 内嵌 CRxView3D 是
//     AV 触发面；实机 11-12：OpenFile2 每张卡 25~28s、图纸进了 Draft 但 Scene 永远
//     「未进册」）。已在本项目立项：C# 不得对业务 exb 调任何 caxa3d 侧 COM，
//     本命令是「打开 exb」的唯一入口。
//   已在册 → 只激活；不在册 → 三通道直至文档在册：
//     A crxDocManager->appContextOpenDocument（官方样例写法；★ 绝不包
//       executeInApplicationContext —— 图纸环境空闲态本身就是一个命令，
//       「应用上下文永远回不来」，回调 0 次命中 = 旧版 ret=-1 的真正根因）；
//     B 键入通道 sendStringToExecute CX_open_Impl "<路径>"（兄弟工程实证唯一能把
//       命令真正跑起来的通道）；
//     C crxedCommand 原生命令兜底（编辑器忙时被静默丢弃，仅最后一搏）。
//   判成功只认「文档真的在册」，不认任何返回码。成功后切活动文档 + 1.5s 沉降拍
//   （isQuiescent 结构性恒 0，8s 每次烧满 → 11-11 参考工程定案收紧为 1.5s；
//   真正的同步判据是后续 SAVE 的落盘 mtime）。
//---------------------------------------------------------------------
static CRxApDocument* WaitDocInRegistry(const std::wstring& wantNorm, int budgetMs)
{
    DWORD start = GetTickCount();
    while (GetTickCount() - start < (DWORD)budgetMs)
    {
        PumpMessages(100);
        CRxApDocument* p = FindDocByPath(wantNorm);
        if (p) return p;
    }
    return NULL;
}

/// 诊断：把当前在册文档全路径打出来，区分「真没打开」与「打开了但路径匹配不上」。
static void LogOpenDocs(const wchar_t* tag)
{
    if (!crxDocManager) return;
    CRxApDocumentIterator* it = crxDocManager->newAcApDocumentIterator();
    if (!it) return;
    int n = 0;
    for (; !it->done(); it->step())
    {
        CRxApDocument* d = it->document();
        if (!d) continue;
        const CxCHAR* fn = d->fileName();
        Log("openexb: %ls 在册[%d]=%ls", tag, n++, (fn && fn[0]) ? fn : L"(空)");
    }
    delete it;
    Log("openexb: %ls 在册文档数=%d", tag, n);
}

static void DoOpenExb(int seq, const std::wstring& arg)
{
    if (arg.empty())
    {
        WriteResp(seq, L"OPEN_EXB", L"ERR", L"参数错误：缺 exb 全路径");
        return;
    }
    if (!crxDocManager)
    {
        WriteResp(seq, L"OPEN_EXB", L"ERR", L"图纸文档管理器不可用（不在图纸环境？）");
        return;
    }

    std::wstring want = NormPath(arg);
    CRxApDocument* pTarget = FindDocByPath(want);

    if (!pTarget)
    {
        // 通道 A：文档管理器直接打开（官方样例 CRXOpenDocFile 写法，不包 app-context）。
        int retA = (int)crxDocManager->appContextOpenDocument(arg.c_str());
        Log("openexb: [A] appContextOpenDocument ret=%d path=%ls", retA, W2U8(arg).c_str());
        pTarget = WaitDocInRegistry(want, 7000);
        Log("openexb: [A] 等 7s 后 %ls", pTarget ? L"文档已在册" : L"文档未在册");

        // 通道 B：键入通道直投 CX_open_Impl <路径>。
        if (!pTarget)
        {
            CRxApDocument* pActive = crxDocManager->mdiActiveDocument();
            if (!pActive)
            {
                // 没有「活动」文档但可能有在册文档（预热图纸关掉后常见）：取第一张当键入目标
                CRxApDocumentIterator* it = crxDocManager->newAcApDocumentIterator();
                if (it) { if (!it->done()) pActive = it->document(); delete it; }
            }
            if (pActive)
            {
                std::wstring cmd = L"CX_open_Impl \"" + arg + L"\" ";
                CDraft::ErrorStatus es = crxDocManager->sendStringToExecute(
                    pActive, cmd.c_str(), true, false, true);
                Log("openexb: [B] sendStringToExecute status=%d inputPending=%d cmd=%ls",
                    (int)es, crxDocManager->inputPending(pActive), W2U8(cmd).c_str());
                pTarget = WaitDocInRegistry(want, 7000);
            }
            else Log("openexb: [B] 跳过（mdiActiveDocument=NULL，无键入目标）");
            Log("openexb: [B] 等 7s 后 %ls", pTarget ? L"文档已在册" : L"文档未在册");
        }

        // 通道 C：crxedCommand 原生命令兜底（可写副本：宿主可能往 RTSTR 缓冲区写回）。
        if (!pTarget)
        {
            std::vector<wchar_t> pathBuf(arg.begin(), arg.end());
            pathBuf.push_back(L'\0');
            int st = crxedCommand(RTSTR, L"CX_open_Impl", RTSTR, pathBuf.data(), RTNONE);
            Log("openexb: [C] crxedCommand CX_open_Impl ret=%d", st);
            pTarget = WaitDocInRegistry(want, 6000);
            Log("openexb: [C] 等 6s 后 %ls", pTarget ? L"文档已在册" : L"文档未在册");
        }

        if (!pTarget)
        {
            LogOpenDocs(L"三通道均未命中");
            WriteResp(seq, L"OPEN_EXB", L"ERR",
                      (std::wstring(L"图纸侧打开 exb 失败（A 文档管理器 / B 键入通道 / C 原生命令，共 20s，") +
                       L"文档始终未在册；A ret=" + std::to_wstring(retA) + L"）：" + arg).c_str());
            return;
        }
        LogOpenDocs(L"打开成功");
    }
    else
    {
        Log("openexb: 目标已在册，跳过打开 → %ls", W2U8(BaseNameOfC(want)).c_str());
    }

    // 激活（后续 SAVE / 导出都作用于「当前文档」）
    CRxApDocument* pActive = crxDocManager->mdiActiveDocument();
    if (pActive != pTarget)
    {
        CDraft::ErrorStatus as = crxDocManager->activateDocument(pTarget, false);
        Log("openexb: 切换活动图纸 → %ls status=%d", W2U8(BaseNameOfC(want)).c_str(), (int)as);
        for (int i = 0; i < 5; ++i) PumpMessages(30);
    }

    // 沉降拍（见节首注释：不靠 isQuiescent 判完成，只给打开一个 1.5s 沉降拍）
    DWORD start = GetTickCount();
    while (GetTickCount() - start < 1500 && !pTarget->isQuiescent()) PumpMessages(120);

    WriteResp(seq, L"OPEN_EXB", L"OK", L"图纸环境侧已打开 → " + BaseNameOfC(want));
    Log("openexb: %ls OK (quiescent=%d) doc=%ls", W2U8(BaseNameOfC(want)).c_str(),
        pTarget->isQuiescent() ? 1 : 0, W2U8(want).c_str());
}

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
    { L"CONVERT_DXF", true,  true,  DoConvertDxf,
                                            L"图纸环境内把目标 exb 经 TDExportControl.Export 转 dxf/dwg/pdf（arg=exb路径|输出路径|eZExportDrawingType；无 UI；★ 2027 实机 GetIDispatch 恒 NULL 已判死，仅留作规则总纲 29④ 拍板候选通道）" },
    { L"OPEN_EXB",    true,  true,  DoOpenExb,
                                            L"图纸环境侧打开/激活 exb（arg=exb全路径；A 文档管理器/B 键入 CX_open_Impl/C 原生命令三通道；替代 Scene 侧 OpenFile2 对业务 exb 的退役路线）" },
    { L"GETSCALE",    false, false, DoGetScale,   L"读当前图纸比例与图幅宽高（PaperTable.CurrentPaper），供二维码比例补偿/定位规则" },
    { L"BITMAP_NATIVE", true, true,  DoBitmapNative,
                                            L"投宿主 CX_DFT_InsertBitmap 插图（回包先落盘再投出；判据=QR_STATE delta）" },
    { L"QR_STATE",    false, false, DoQrState,    L"模型空间实体计数增量（BITMAP_NATIVE 后 delta>0 = 插图成功唯一权威判据）" },
    { L"LISTENTS",    false, false, DoListEnts,   L"只读 dump 模型空间类名分布 + imageRef 计数（先删后附判定取证）" },
    { L"DELETE_QR",   true,  true,  DoDeleteQr,   L"arg=\"x,y\"：按图片实体包围盒命中目标点 ±1mm 精确删除旧二维码（先只读收集再 erase）" },
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
                  L"（可用：PING / CLOSEDOC / CLOSERAW / SAVE / EXEC_NATIVE / TITLEBLOCK_3DSET / TB_ABORT / CONVERT_DXF / OPEN_EXB / GETSCALE / BITMAP_NATIVE / QR_STATE / LISTENTS / DELETE_QR）");
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
