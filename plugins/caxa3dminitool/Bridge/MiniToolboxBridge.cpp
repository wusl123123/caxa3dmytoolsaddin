//=====================================================================
// MiniToolboxBridge.cpp — CAXA 3D 图纸环境（Draft）CRX 桥接模块（caxa3dminitool 专属）
//
// ★ 来源：以 caxa3dexbtracker\Bridge\ExbTrackerBridge.cpp 为蓝本复制。
//   临时文件名全部换成 caxa3dminitool_sub_ 前缀（request/response/kill/marker/log），
//   模块名/窗口类/入口类改 MiniToolboxBridge —— 两个插件各自的桥与 seq、令牌完全独立，
//   同机同进程共存互不干扰（同名文件桥会互相抢请求、seq 互相顶掉，故必须分名）。
//
// ★★★ 定位铁律（2026-11-04 13:30 用户定案，不得再漂移）★★★
//   本 CRX **只做「C# ↔ 图纸环境」的联系通道**，不做任何业务决策、不自主执行任何命令。
//   · 加载（自动加载，登记 CxAutorun.cx）只做「准备工作」：
//       写 marker 探针 → 清空残留请求/响应 → seq 归零 → 生成本次会话令牌 → 挂桥定时器。
//     **加载时绝不执行任何命令**（哪怕 %TEMP% 里躺着上会话带 confirm=yes 的删除请求）。
//   · 运行期只被动等 C# 指令，两种形态（用户原话：C# 直接注入 CRX 认可的命令，
//     或激活 CRX 内准备好的命令）：
//       ① 激活 CRX 内**准备好的命令**：action 命中下方 kCmdTable 里的具名命令；
//       ② 直接**注入 CRX 认可的命令**：action=EXEC_NATIVE 且 arg 以白名单前缀开头
//          （默认只放行 CX_DFT_ 族的 CAXA 原生图纸命令），由本端转投宿主。
//   · 破坏性命令（删除）额外要求：confirm=yes **且** token 等于本次加载生成的会话令牌。
//     令牌每次加载都变 ⇒ 上会话残留请求即使没被清掉也 Activate 不了，宿命性地"不自动执行"。
//
// ── 文件桥协议（%TEMP%）──────────────────────────────────────────────
//   请求 request.txt : seq=5 / action=<命令名> / confirm=yes|no / token=<会话令牌> / arg=<参数>
//   响应 response.txt: seq=5 / status=OK|ERR|SCAN|PONG / model=.. / papers=a|b|c / deleted=N /
//                      token=<会话令牌> / message=..
//   · 请求用「写临时文件→原子替换」由 C# 侧完成；本端整文件读、整文件写（坑 76：不按字节偏移）。
//   · seq 单调递增；本端只处理 seq > 已处理 的请求，过期即丢弃，天然幂等。
//   · 本端读走请求后立即删除请求文件，避免重复处理。
//
// ── 删除「除模型外所有布局」的实现（2026-11-04 13:30 换路）────────────
//   术语对齐：CAXA 电子图板里"布局"= "图幅"(Paper)；"模型"也**在 PaperTable 里**，
//   中文版内部名是英文小写 `model`（坑 28，最严重的一次误删事故）。
//   ★ 不再走原生命令 CX_DFT_DeletePaper：实机（crx.log 13:27:53）投出去后目标图幅
//     一直不消失，6 秒超时中止 —— 该命令是**交互式模态命令**（等用户选图幅/确认），
//     不是可编程接口。改用 CRX 数据层直接删：PaperTable(kForWrite) → getAt(name, rec, kForWrite)
//     → rec->erase() → rec->close()，删前先把当前图幅切回模型（避免删当前页），
//     每张删完用 has() 回查。全程同步、无 UI、无对话框、无超时赌运气。
//
// ── 安全开关 ────────────────────────────────────────────────────────
//   %TEMP%\caxa3dminitool_sub_crx.kill 存在即停用桥轮询（运维兜底）。
//
// ── 加载方式（2026-11-04 实机定案）──────────────────────────────────
//   ★ **自动加载**：登记进 Modules\CxAutorun.cx，与两个产线项目
//     （caxa3ddiffrename / caxa3dCXDFTViewUpdate）同机制、同骨架。
//   ★ 但 **CRX 内部不得出现任何 UI / 命令注册代码**（Ribbon / addCommand /
//     RegisterWindowMessage / afxdllx.h）：2026-11-04 12:54 那次启动期原生闪退的
//     真正根因就是这些代码，不是 CxAutorun 登记本身（11-04 13:20 定案）。
//   ★ 骨架对齐产线稳定的 caxa3ddiffrename-crx：v143 工具集、无 afxdllx.h、
//     100ms 取件节拍、marker 就绪探针（marker 写入时间 >= 本进程启动时间）。
//=====================================================================

#include "StdAfx.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <vector>
#include <string>
#include <sstream>

#include "crxcmd.h"        // （保留：CRX 命令表基础头，间接被其它头依赖）
#include "crxdocman.h"     // crxDocManager / sendStringToExecute
#include "crxentrypoint.h"
#include "dbmain.h"        // CRxDbDatabase / getPaperTable / erase / close / CDraft
#include "dbsymtb.h"       // CRxDbPaperTable / CRxDbPaperTableRecord
#include "crxdb.h"         // CRxDb::OpenMode
#include "crxstrc.h"       // CDraft::eOk
#include "crxChar.h"       // CxCHAR

AC_IMPLEMENT_EXTENSION_MODULE(MiniToolboxBridgeDLL)

//---------------------------------------------------------------------
// 常量
//---------------------------------------------------------------------
// 文件桥：请求 / 响应 / 安全开关 / 就绪探针
static const wchar_t* kReqFile    = L"caxa3dminitool_sub_request.txt";
static const wchar_t* kRespFile   = L"caxa3dminitool_sub_response.txt";
static const wchar_t* kKillFile   = L"caxa3dminitool_sub_crx.kill";
static const wchar_t* kMarkerFile = L"caxa3dminitool_sub_crx.marker";

// 删除命令（仅作为 EXEC_NATIVE 白名单里的一个可注入命令，不再用于自动删图幅）
static const wchar_t* kDeletePaperCmd = L"CX_DFT_DeletePaper ";

static const UINT_PTR kBridgeTimerId = 0xE2;
static const UINT     kBridgeTimerMs = 100;   // 与 diffrename 一致的命令取件节拍

// EXEC_NATIVE 白名单：C# 可注入的宿主命令前缀（"CRX 认可的命令"）。
// 只放行 CAXA 图纸命令族（CX_DFT_ / CX_），避免 C# 侧任意字符串把宿主搞崩。
// 要放宽必须同时评估"任意命令注入"的破坏面，不要图省事写成空前缀。
static const wchar_t* kNativeCmdPrefixes[] = { L"CX_DFT_", L"CX_" };

//---------------------------------------------------------------------
// 全局状态
//---------------------------------------------------------------------
static HWND  g_hDispWnd      = NULL;
static bool  g_logReady      = false;
static CRITICAL_SECTION g_logCS;
static HANDLE g_hLog = INVALID_HANDLE_VALUE;

// 文件桥
static int   g_lastSeq  = 0;     // 已处理的最大请求 seq（幂等）
static bool  g_killSeen = false; // 已因安全开关停过桥

// ★ 本次加载的会话令牌：`<pid>-<加载 tick>`。每次加载都不同。
//   破坏性命令必须回填该令牌，否则一律拒绝 —— 这是"不自动执行命令"的硬保证：
//   上会话残留的 confirm=yes 请求即使没被清掉，令牌也对不上。
static std::wstring g_token;

//---------------------------------------------------------------------
// 日志：%TEMP%\caxa3dminitool_sub_crx.log
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
    TempPath(path, MAX_PATH, L"caxa3dminitool_sub_crx.log");
    g_hLog = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                         NULL, OPEN_ALWAYS, 0, NULL);
}

static void Log(const char* fmt, ...)
{
    if (!g_logReady || g_hLog == INVALID_HANDLE_VALUE) return;
    char body[512];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(body, sizeof(body), _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[640];
    _snprintf_s(line, sizeof(line), _TRUNCATE, "%02d:%02d:%02d.%03d [tid %lu] %s\r\n",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                GetCurrentThreadId(), body);

    EnterCriticalSection(&g_logCS);
    DWORD written = 0;
    WriteFile(g_hLog, line, (DWORD)strlen(line), &written, NULL);
    LeaveCriticalSection(&g_logCS);
}

//---------------------------------------------------------------------
// 【已整段删除】Ribbon 按钮 / 命令注册 / RegisterWindowMessage 唤起 C# / 懒注册定时器
//
// 2026-11-04 实机结论：这些"加载即自主干活"的逻辑是**启动期原生闪退的高危面**。
// 本模块改为随 CAXADraft 自动加载（登记 CxAutorun.cx）后，加载时机正好是图纸模块
// 初始化阶段 —— 此刻去建 Ribbon / addCommand / 注册窗口消息，正是上午 12:54 闪退
// （crx.log 连 On_kInitAppMsg 都没写出来）的现场。
// 于是全部移除，与两个产线项目保持同构：加载后唯一行为 = 挂 message-only 窗口 +
// 文件桥定时器，被动等 C# 请求。**本文件不得再出现任何 UI / 命令注册代码。**
//---------------------------------------------------------------------

//=====================================================================
// 文件桥：读写请求/响应
//=====================================================================

static bool FileExists(const wchar_t* path)
{
    DWORD a = GetFileAttributesW(path);
    return (a != INVALID_FILE_ATTRIBUTES) && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

static bool ReadUtf8File(const wchar_t* path, std::wstring& out)
{
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
            int wlen = MultiByteToWideChar(CP_UTF8, 0, (char*)&buf[0], (int)rd, NULL, 0);
            if (wlen > 0)
            {
                out.resize(wlen);
                MultiByteToWideChar(CP_UTF8, 0, (char*)&buf[0], (int)rd, &out[0], wlen);
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
    return true;
}

/// <summary>把响应整文件写回（覆盖式），UTF-8 无 BOM。</summary>
static void WriteResp(int seq, const wchar_t* status, const wchar_t* model,
                     const wchar_t* papers, int deleted, const wchar_t* msg)
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, kRespFile);

    std::wstring s;
    wchar_t num[32];
    _snwprintf_s(num, 32, _TRUNCATE, L"%010d", seq);
    s += L"seq=";     s += num; s += L"\r\n";
    s += L"status=";  s += (status ? status : L""); s += L"\r\n";
    s += L"model=";   s += (model  ? model  : L""); s += L"\r\n";
    s += L"papers=";  s += (papers ? papers : L""); s += L"\r\n";
    _snwprintf_s(num, 32, _TRUNCATE, L"%d", deleted);
    s += L"deleted="; s += num; s += L"\r\n";
    s += L"token=";   s += g_token; s += L"\r\n";
    s += L"message="; s += (msg    ? msg    : L""); s += L"\r\n";

    WriteUtf8File(path, s);
    Log("resp: seq=%010d status=%ls deleted=%d papers=%ls", seq, status ? status : L"", deleted, papers ? papers : L"");
}

static bool ParseRequest(const std::wstring& text, int& seq, std::wstring& action,
                         bool& confirm, std::wstring& token, std::wstring& arg)
{
    seq = -1; action.clear(); confirm = false; token.clear(); arg.clear();
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
        if (key == L"seq")          seq     = _wtoi(val.c_str());
        else if (key == L"action")  action  = val;
        else if (key == L"confirm") confirm = (val == L"yes");
        else if (key == L"token")   token   = val;
        else if (key == L"arg")     arg     = val;
    }
    return seq > 0 && !action.empty();
}

//---------------------------------------------------------------------
// 「模型」图幅判定 —— 本工程最要命的一处（2026-11-04 实机踩坑）
//
// 实机取证（crx.log 13:12:54）：中文版 CAXA 3D 2027 图纸文档的 PaperTable 里，
//   「模型」这一项的**内部名是英文小写 `model`**，不是中文「模型」。
//   早期版本按中文「模型」比较来做过滤 ⇒ 过滤完全失效 ⇒ `model` 被当成普通图幅
//   切为当前图幅并投 `CX_DFT_DeletePaper` ⇒ 模型空间被摧毁、图纸环境窗口消失。
//   故此处改为**多重判据 + 大小写不敏感**，任一命中即视为模型、绝不删除。
//---------------------------------------------------------------------
static bool IsModelPaperName(const std::wstring& raw)
{
    std::wstring s = raw;
    while (!s.empty() && (s[0] == L'*' || s[0] == L' ')) s.erase(0, 1);   // 去掉 *Model_Space 的前导 '*'
    while (!s.empty() && s[s.size() - 1] == L' ') s.erase(s.size() - 1);
    if (s.empty()) return true;                                           // 空名一律当模型，宁可不删

    std::wstring low;
    low.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) low.push_back((wchar_t)towlower(s[i]));

    if (low == L"model")            return true;   // ★ 实机取证：中文版内部名就是 model
    if (low == L"model_space")      return true;   // *Model_Space 去掉 '*' 后
    if (low == L"模型")              return true;   // 万一某版本用中文内部名
    if (low.find(L"model_space") == 0) return true;
    if (low.find(L"模型空间") == 0)     return true;
    return false;
}

/// <summary>
/// 取活动图纸文档 PaperTable 的全部图幅名，并分离出「模型」项。
/// </summary>
/// <param name="names">全部图幅名（按 PaperTable 索引顺序）</param>
/// <param name="modelName">识别到的模型图幅内部名；识别不到则为空</param>
/// <param name="modelCount">被判定为模型的项数（正常应恰为 1）</param>
static bool EnumPaperNames(std::vector<std::wstring>& names,
                           std::wstring& modelName,
                           int& modelCount)
{
    names.clear();
    modelName.clear();
    modelCount = 0;

    if (!crxDocManager || !crxDocManager->mdiActiveDocument()) return false;
    CRxDbDatabase* pDb = crxDocManager->mdiActiveDocument()->database();
    if (!pDb) return false;
    CRxDbPaperTable* pPt = NULL;
    if (pDb->getPaperTable(pPt, CRxDb::kForRead) != CDraft::eOk || !pPt) return false;

    int n = pPt->getPaperCount();
    for (int i = 0; i < n; ++i)
    {
        CxCHAR* pName = NULL;
        if (pPt->getPaperNameByIndex(i, pName) == CDraft::eOk && pName && pName[0])
        {
            std::wstring s(pName);
            names.push_back(s);
            if (IsModelPaperName(s)) { ++modelCount; if (modelName.empty()) modelName = s; }
        }
    }
    pPt->close();
    return true;
}

/// <summary>扫描：仅回 papers/model，不删。papers 只列**可删**的非模型布局。</summary>
static void DoScan(int seq, const std::wstring& /*arg*/)
{
    std::vector<std::wstring> names;
    std::wstring modelName;
    int modelCount = 0;
    if (!EnumPaperNames(names, modelName, modelCount))
    {
        WriteResp(seq, L"ERR", L"", L"", 0,
                  L"无活动图纸文档（请先打开/预热图纸环境，并确保有前台图纸）");
        return;
    }

    std::wstring joined;
    for (size_t i = 0; i < names.size(); ++i)
    {
        if (IsModelPaperName(names[i])) continue;      // 模型项不列为可删目标
        if (!joined.empty()) joined += L"|";
        joined += names[i];
    }
    Log("scan: total=%d model=%d(%ls) deletable=%ls",
        (int)names.size(), modelCount,
        modelName.empty() ? L"<未识别>" : modelName.c_str(),
        joined.empty() ? L"<none>" : joined.c_str());

    WriteResp(seq, L"SCAN",
              modelName.empty() ? L"模型" : modelName.c_str(),
              joined.c_str(), 0, L"");
}

//---------------------------------------------------------------------
// ErrorStatus 助记名（只为一件事：日志里出现 335 时不再被当成"失败"）
// 枚举定义见 CRX\Inc\crxstrc.h。本表只收录图幅删除链路上真会遇到的取值。
//---------------------------------------------------------------------
static const wchar_t* EsName(CDraft::ErrorStatus es)
{
    switch ((int)es)
    {
    case   0: return L"eOk";
    case  80: return L"eWasErased";
    case 105: return L"eCannotBeErasedByCaller";
    case 322: return L"eDatabaseObjectsOpen";
    case 335: return L"eInvalidContext";        // ★ 实测常态：删成功也返回它
    case 342: return L"eDelDoesNotExist";
    case 343: return L"eDelIsModelSpace";
    case 344: return L"eDelLastLayout";
    case 345: return L"eDelUnableToSetCurrent";
    case 346: return L"eDelUnableToFind";
    default:  return L"?";
    }
}

//---------------------------------------------------------------------
// 删除「除模型外的所有图幅」——CRX 数据层直接删（不经原生命令、不弹框、不入交互态）
//
// 2026-11-04 13:27 实机（crx.log）：
//   scan: total=2 model=1(model) deletable=布局1
//   delete: 已切到「布局1」并投删除命令
//   delete: 超时中止（卡在「布局1」）     ← CX_DFT_DeletePaper 投了但图幅没消失
// 结论：CX_DFT_DeletePaper 是**交互式模态命令**（等用户选图幅/确认），不是可编程删除接口；
//   继续用 sendStringToExecute 只能靠超时赌，永远删不掉。
// 改法：PaperTable(getAt → CRxDbPaperTableRecord) → record->erase()，同步生效。
//   删前先把当前图幅切回模型 —— 避免删除"当前页"引发宿主重排/提示。
//
// ★ 2026-11-04 13:40 实机（crx.log）—— 上一版"判失败"是误报，此处是本函数的核心口径：
//   delete: 「布局1」erase 未生效 status=335 stillThere=0
//   delete: 「布局2」erase 未生效 status=335 stillThere=0
//   紧接着 scan: total=3 → scan: total=1 model=1(model) deletable=<none>
//   ⇒ 图幅**确实删掉了**，只是 erase() 的返回值 335（= CDraft::eInvalidContext，
//     crxstrc.h:258）不可信：它反映的是"当前不在可删布局的上下文里"，
//     而不是"没删"。数据层 erase 的语义就是"标记删除"，标记完就生效。
//   ⇒ 判据改为**只看副作用**：删完用同一个 PaperTable 句柄 has(name) 回查，
//     查不到 = 成功；返回码只进日志与诊断，不参与成败判定
//     （与规则"判成功只看权威副作用信号，不看 API 返回值"一致）。
//---------------------------------------------------------------------
static void DoDeleteAllPapers(int seq, const std::wstring& /*arg*/)
{
    std::vector<std::wstring> names;
    std::wstring modelName;
    int modelCount = 0;
    if (!EnumPaperNames(names, modelName, modelCount))
    {
        WriteResp(seq, L"ERR", L"", L"", 0,
                  L"无活动图纸文档（请先打开/预热图纸环境，并确保有前台图纸）");
        return;
    }

    // ── 安全闸 1：模型项必须被识别出来，否则说明判据失效，一律不删 ──
    if (modelCount == 0)
    {
        std::wstring why = L"未能识别出模型图幅（PaperTable 共 " +
                           std::to_wstring(names.size()) +
                           L" 项），为避免误删模型空间，已拒绝执行。请把 %TEMP%\\caxa3dminitool_sub_crx.log 发给开发者。";
        WriteResp(seq, L"ERR", L"", L"", 0, why.c_str());
        Log("delete: ABORT 未识别到模型图幅（total=%d），拒绝删除", (int)names.size());
        return;
    }

    std::vector<std::wstring> queue;
    for (size_t i = 0; i < names.size(); ++i)
    {
        if (IsModelPaperName(names[i])) continue;
        queue.push_back(names[i]);
    }

    // ── 安全闸 2：待删数必须严格小于总图幅数（至少保留模型那一项）──
    if (queue.size() >= names.size())
    {
        std::wstring why = L"安全校验未通过：待删 " + std::to_wstring(queue.size()) +
                           L" 项 >= 总图幅 " + std::to_wstring(names.size()) +
                           L" 项，可能误含模型，已拒绝执行。";
        WriteResp(seq, L"ERR", L"", L"", 0, why.c_str());
        Log("delete: ABORT 待删>=总数（%d>=%d），拒绝删除",
            (int)queue.size(), (int)names.size());
        return;
    }

    if (queue.empty())
    {
        WriteResp(seq, L"OK", modelName.c_str(), L"", 0, L"除模型外没有其它布局，无需删除");
        Log("delete: 无需删除（除模型外无图幅）");
        return;
    }

    CRxApDocument* pDoc = crxDocManager->mdiActiveDocument();
    CRxDbDatabase* pDb  = pDoc ? pDoc->database() : NULL;
    if (!pDb)
    {
        WriteResp(seq, L"ERR", L"", L"", 0, L"无法获取图纸数据库");
        return;
    }

    std::wstring dbg;
    for (size_t i = 0; i < queue.size(); ++i) { if (i) dbg += L"|"; dbg += queue[i]; }
    Log("delete: 启动（数据层 erase），待删 %d 个图幅 [%ls]，已跳过模型「%ls」",
        (int)queue.size(), dbg.c_str(), modelName.c_str());

    // 图幅表**打开一次用完整个删除轮次**（官方样例 CRxDbLayerTable 删记录即此法：
    //   表 kForRead → getAt 记录 kForWrite → erase → close），比每张重开表少一次
    //   打开/关闭，也保证 has() 回查与删除发生在同一个表句柄上，读到的是最新状态。
    CRxDbPaperTable* pPt = NULL;
    if (pDb->getPaperTable(pPt, CRxDb::kForWrite) != CDraft::eOk || !pPt)
    {
        WriteResp(seq, L"ERR", L"", L"", 0, L"打开图幅表（PaperTable kForWrite）失败");
        Log("delete: 打开图幅表失败");
        return;
    }
    int beforeCount = pPt->getPaperCount();

    // ① 先把当前图幅切回模型，避免删"当前页"（回归：不切会让图幅停留在待删页上）
    {
        CRxDbObjectId modelId;
        if (pPt->getAt((const CxCHAR*)modelName.c_str(), modelId, false) == CDraft::eOk)
        {
            CDraft::ErrorStatus esCur = pPt->setCurrentPaperId(modelId);
            Log("delete: 当前图幅切回模型「%ls」 status=%d(%ls)",
                modelName.c_str(), (int)esCur, EsName(esCur));
        }
        else
        {
            Log("delete: 取模型图幅 id 失败，未切当前图幅（继续删，仅删非模型项）");
        }
    }

    // ② 逐张删（每张删前再校验一次模型判据 = 安全闸 3）
    //    ★ 成败只看 has() 回查：erase 返回 335(eInvalidContext) 但图幅已消失是实测常态。
    int done = 0;
    std::wstring failed;
    for (size_t i = 0; i < queue.size(); ++i)
    {
        const std::wstring& name = queue[i];
        if (IsModelPaperName(name))
        {
            Log("delete: SKIP「%ls」——判定为模型图幅，绝不删除", name.c_str());
            continue;
        }

        if (!pPt->has((const CxCHAR*)name.c_str()))
        {
            // 队列是删前扫出来的，此刻已不存在 ⇒ 视为已达成目标（幂等重入/别处已删）
            ++done;
            Log("delete: 「%ls」删前已不存在，计为已删除", name.c_str());
            continue;
        }

        CRxDbPaperTableRecord* pRec = NULL;
        CDraft::ErrorStatus esOpen = pPt->getAt((const CxCHAR*)name.c_str(), pRec,
                                                CRxDb::kForWrite, false);
        if (esOpen != CDraft::eOk || !pRec)
        {
            failed += (failed.empty() ? L"" : L"、");
            failed += name + L"（打开记录失败 " + std::to_wstring((int)esOpen) + L"）";
            Log("delete: 「%ls」getAt 失败 status=%d(%ls)", name.c_str(), (int)esOpen, EsName(esOpen));
            continue;
        }

        CDraft::ErrorStatus esErase = pRec->erase();
        pRec->close();

        bool stillThere = pPt->has((const CxCHAR*)name.c_str());
        if (!stillThere)
        {
            ++done;
            Log("delete: 「%ls」已删除（erase=%d(%ls)，判据=回查 has=0）",
                name.c_str(), (int)esErase, EsName(esErase));
        }
        else
        {
            failed += (failed.empty() ? L"" : L"、");
            failed += name + L"（erase=" + std::to_wstring((int)esErase) + L"，记录仍在）";
            Log("delete: 「%ls」erase 后记录仍在 erase=%d(%ls) —— 真失败",
                name.c_str(), (int)esErase, EsName(esErase));
        }
    }

    int afterCount = pPt->getPaperCount();
    pPt->close();
    Log("delete: 图幅数 %d -> %d（表句柄内计数）", beforeCount, afterCount);

    // ③ 重新开表回查剩余非模型图幅（换句柄，排除本会话缓存）
    std::vector<std::wstring> after;
    std::wstring modelAfter;
    int modelAfterCount = 0;
    std::wstring remain;
    if (EnumPaperNames(after, modelAfter, modelAfterCount))
    {
        for (size_t i = 0; i < after.size(); ++i)
        {
            if (IsModelPaperName(after[i])) continue;
            if (!remain.empty()) remain += L"|";
            remain += after[i];
        }
    }

    // ③ 的收尾：remain 里若还有东西，说明"回查仍在"，必须并入失败，不许报成功
    if (!remain.empty())
    {
        if (!failed.empty()) failed += L"、";
        failed += L"回查仍存在：" + remain;
    }

    std::wstring msg;
    if (failed.empty())
        msg = L"除模型外已无布局";          // 条数由 C# 侧 "已删除 N 个" 呈现，此处不再重复
    else
        msg = L"已删除 " + std::to_wstring(done) + L" 个；未成功：" + failed;

    WriteResp(seq, failed.empty() ? L"OK" : L"ERR",
              modelName.c_str(), remain.c_str(), done, msg.c_str());
    Log("delete: 结束 done=%d failed=%ls remain=%ls",
        done, failed.empty() ? L"<none>" : failed.c_str(),
        remain.empty() ? L"<none>" : remain.c_str());
}

//---------------------------------------------------------------------
// 准备好的具名命令之外的两个"通道级"命令
//---------------------------------------------------------------------
/// <summary>握手：C# 借此确认「桥活着、且是本次加载的实例」，并拿到会话令牌。</summary>
static void DoPing(int seq, const std::wstring& /*arg*/)
{
    std::wstring msg = L"bridge alive; token=" + g_token +
                       L"; cmds=PING,SCAN_LAYOUTS,DELETE_LAYOUTS_EXCEPT_MODEL,EXEC_NATIVE";
    WriteResp(seq, L"PONG", L"", L"", 0, msg.c_str());
    Log("ping: token=%ls", g_token.c_str());
}

/// <summary>
/// 注入一条「CRX 认可的命令」：把 CAXA 原生图纸命令串转投宿主命令表。
/// 只放行白名单前缀（见 kNativeCmdPrefixes），其余一律拒绝 —— 避免任意字符串把宿主搞崩。
/// 注意：sendStringToExecute 是异步投递，本命令只保证"已投出"，不保证"已执行完"。
/// </summary>
static void DoExecNative(int seq, const std::wstring& arg)
{
    if (arg.empty())
    {
        WriteResp(seq, L"ERR", L"", L"", 0, L"EXEC_NATIVE 缺少 arg（要注入的宿主命令）");
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
        WriteResp(seq, L"ERR", L"", L"", 0,
                  L"命令未被 CRX 认可（不在白名单前缀内），已拒绝注入");
        Log("exec: REJECT argc=%d（白名单外）", (int)arg.size());
        return;
    }

    if (!crxDocManager || !crxDocManager->mdiActiveDocument())
    {
        WriteResp(seq, L"ERR", L"", L"", 0, L"无活动图纸文档，无法注入命令");
        return;
    }
    crxDocManager->sendStringToExecute(crxDocManager->mdiActiveDocument(),
                                       arg.c_str(), true, false, true);
    std::wstring msg = L"已投递命令：" + arg;
    WriteResp(seq, L"OK", L"", L"", 0, msg.c_str());
    Log("exec: 已投递「%ls」", arg.c_str());
}

//---------------------------------------------------------------------
// 命令表：C# 只能"激活"这里登记过的命令，或经 EXEC_NATIVE 注入白名单命令。
//   needsConfirm = true  → 必须 confirm=yes
//   needsToken   = true  → 必须带回本次加载的会话令牌（防上会话残留请求被自动执行）
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
    { L"PING",                        false, false, DoPing,            L"握手/探活，返回会话令牌" },
    { L"SCAN_LAYOUTS",                false, false, DoScan,            L"枚举图幅（只读，不删）" },
    { L"DELETE_LAYOUTS_EXCEPT_MODEL", true,  true,  DoDeleteAllPapers, L"删除除模型外的所有图幅" },
    { L"EXEC_NATIVE",                 true,  true,  DoExecNative,      L"注入一条白名单内的宿主命令" },
};

static const CmdEntry* FindCmd(const std::wstring& action)
{
    for (size_t i = 0; i < _countof(kCmdTable); ++i)
        if (action == kCmdTable[i].name) return &kCmdTable[i];
    return NULL;
}

/// <summary>文件桥 100ms 轮询：取一条请求 → 查命令表 → 校验 → 执行。不做任何自主行为。</summary>
static void BridgeTick()
{
    // 安全开关：存在即停桥轮询
    wchar_t killPath[MAX_PATH];
    TempPath(killPath, MAX_PATH, kKillFile);
    if (FileExists(killPath))
    {
        if (!g_killSeen)
        {
            g_killSeen = true;
            Log("bridge: 检测到安全开关 %ls，停止桥轮询", killPath);
            if (g_hDispWnd) KillTimer(g_hDispWnd, kBridgeTimerId);
        }
        return;
    }

    wchar_t reqPath[MAX_PATH];
    TempPath(reqPath, MAX_PATH, kReqFile);
    if (!FileExists(reqPath)) return;

    std::wstring content;
    if (!ReadUtf8File(reqPath, content)) { DeleteFileW(reqPath); return; }
    DeleteFileW(reqPath);   // 已消费，避免重复处理

    int seq = -1; std::wstring action, token, arg; bool confirm = false;
    if (!ParseRequest(content, seq, action, confirm, token, arg)) return;
    if (seq <= g_lastSeq) return;   // 过期/重复
    g_lastSeq = seq;

    // ① 命令必须在 CRX 的命令表里（C# 只能"激活"准备好的命令）
    const CmdEntry* pCmd = FindCmd(action);
    if (!pCmd)
    {
        WriteResp(seq, L"ERR", L"", L"", 0,
                  (L"CRX 不认可该命令：" + action + L"（可用：PING / SCAN_LAYOUTS / "
                   L"DELETE_LAYOUTS_EXCEPT_MODEL / EXEC_NATIVE）").c_str());
        Log("bridge: 未知/未登记命令「%ls」，已拒绝", action.c_str());
        return;
    }

    // ② 会话令牌：只认本次加载生成的令牌（上会话残留请求在这里被挡住）
    if (pCmd->needsToken && token != g_token)
    {
        WriteResp(seq, L"ERR", L"", L"", 0,
                  L"会话令牌不匹配，已拒绝执行（请重新握手；这可能是上次会话的残留请求）");
        Log("bridge: 令牌不匹配 action=%ls got=%ls want=%ls，已拒绝",
            action.c_str(), token.c_str(), g_token.c_str());
        return;
    }

    // ③ 破坏性命令双保险：C# 已弹框确认，本端再校验 confirm
    if (pCmd->needsConfirm && !confirm)
    {
        WriteResp(seq, L"ERR", L"", L"", 0,
                  L"破坏性命令未带确认标志，已拒绝（防止误删/伪造请求）");
        Log("bridge: %ls 未带 confirm，已拒绝", action.c_str());
        return;
    }

    Log("bridge: 执行命令 %ls（seq=%d）", action.c_str(), seq);
    pCmd->handler(seq, arg);
}

//---------------------------------------------------------------------
// 就绪探针（照抄 caxa3ddiffrename）：C# 只在 marker 写入时间 >= 本进程启动时间时
// 才认为桥真的活了，避免上次会话异常退出遗留的过期 marker 造成误判。
//---------------------------------------------------------------------
static void WriteMarker(bool on)
{
    wchar_t path[MAX_PATH];
    TempPath(path, MAX_PATH, kMarkerFile);
    if (!on) { DeleteFileW(path); return; }

    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { Log("marker: create failed err=%lu", GetLastError()); return; }
    const char* tag = "caxa3dminitool CRX command bridge ready\n";
    DWORD written = 0;
    WriteFile(h, tag, (DWORD)strlen(tag), &written, NULL);
    CloseHandle(h);
    Log("marker: ready");
}

//---------------------------------------------------------------------
// message-only 窗口：**只**挂文件桥定时器（无懒注册、无 Ribbon、无唤起消息）
//---------------------------------------------------------------------
static LRESULT CALLBACK DispWndProc(HWND hWnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_TIMER && wp == kBridgeTimerId) { BridgeTick(); return 0; }
    return DefWindowProcW(hWnd, msg, wp, lp);
}

static bool CreateDispatcherWindow()
{
    static bool s_classRegistered = false;
    const wchar_t* kClass = L"MiniToolboxBridgeDispWnd";

    // ★ 用**本模块**的 HINSTANCE，不用宿主 exe 的 GetModuleHandleW(NULL)。
    //   对齐两个产线项目（caxa3ddiffrename / caxa3dCXDFTViewUpdate 都写
    //   WndClass.hInstance = XxxDLL.ModuleResourceInstance()）：窗口过程在 DLL 里，
    //   类归属也应在 DLL，跨模块归属在宿主启动期是没必要的高危面。
    HINSTANCE hInst = MiniToolboxBridgeDLL.ModuleResourceInstance();
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

    g_hDispWnd = CreateWindowExW(0, kClass, L"MiniToolboxBridge", 0, 0, 0, 0, 0,
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
    Log("init: dispatcher window=%p", g_hDispWnd);
    SetTimer(g_hDispWnd, kBridgeTimerId, kBridgeTimerMs, NULL);
    Log("init: passive command bridge active (timer=%u ms, no UI/no commands)", kBridgeTimerMs);
}

static void StopBridge()
{
    if (g_hDispWnd) KillTimer(g_hDispWnd, kBridgeTimerId);
    if (g_hDispWnd) { DestroyWindow(g_hDispWnd); g_hDispWnd = NULL; }
}

//---------------------------------------------------------------------
// 自动加载专有风险：清空上次会话遗留的请求/响应 + 生成本次会话令牌
//
// CRX 随 CAXADraft 自动加载后，出现一个手动加载版本没有的风险：
// %TEMP% 里可能残留上一次会话（或 C# 上次点击）写入的 request.txt，
// 其中若带 confirm=yes 的 DELETE_LAYOUTS_EXCEPT_MODEL，本模块**一被加载就会立刻执行**
// ⇒ 用户什么都没点，布局就被删了。
// 两道防线：
//   ① 加载后、开桥前，无条件清空 request/response 并把 seq 归零（"不自动执行命令"）；
//   ② 每次加载重新生成会话令牌，破坏性命令必须回填该令牌 —— 即使 ① 被绕过，
//      上会话残留请求的令牌也对不上，照样拒绝。
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
// 模块入口
//   ★ 已改为随 CAXADraft 自动加载（登记 CxAutorun.cx，对齐 caxa3ddiffrename /
//     caxa3dCXDFTViewUpdate 两个产线项目）。因此本函数在**图纸模块初始化**阶段
//     就会被调用，必须做到：不建 Ribbon、不注册命令、不碰 UI、不弹框，
//     只开日志 + 写 marker + 挂 message-only 窗口定时器。
//---------------------------------------------------------------------
class CMiniToolboxBridgeApp : public AcRxArxApp
{
public:
    CMiniToolboxBridgeApp() : AcRxArxApp() {}

    virtual AcRx::AppRetCode On_kInitAppMsg(void* pkt) override
    {
        // ── ① 最早期探针（对齐 caxa3dCXDFTViewUpdate）──
        //   放在 AcRxArxApp::On_kInitAppMsg 与任何全局状态初始化之前，
        //   保证「CRX 到底有没有被自动加载」这件事一定能从 marker 看出来，
        //   即使后面某一步崩了，marker 也已经落地（上次闪退就是因为连日志都没写出）。
        WriteMarker(true);

        // ── ② 生成本次会话令牌 + 清残留请求（防自动加载后立即误删）──
        InitSessionToken();
        PurgeStaleRequests();

        AcRx::AppRetCode ret = AcRxArxApp::On_kInitAppMsg(pkt);

        InitializeCriticalSection(&g_logCS);
        g_logReady = true;
        LogOpen();
        Log("MOD: On_kInitAppMsg (auto-loaded passive bridge), exe pid=%lu",
            (unsigned long)GetCurrentProcessId());
        Log("init: stale request/response purged, lastSeq reset, token=%ls", g_token.c_str());
        StartBridge();
        return ret;
    }

    virtual AcRx::AppRetCode On_kUnloadAppMsg(void* pkt) override
    {
        Log("MOD: On_kUnloadAppMsg");
        StopBridge();
        WriteMarker(false);
        g_logReady = false;
        if (g_hLog != INVALID_HANDLE_VALUE) { CloseHandle(g_hLog); g_hLog = INVALID_HANDLE_VALUE; }
        DeleteCriticalSection(&g_logCS);
        return AcRxArxApp::On_kUnloadAppMsg(pkt);
    }

    virtual void RegisterServerComponents() override {}
};

IMPLEMENT_ARX_ENTRYPOINT(CMiniToolboxBridgeApp)
