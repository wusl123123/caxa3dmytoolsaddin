//=====================================================================
// TitleBlockBridge.inl —— 「标题栏设默认」CRX 侧能力（机械移植自
//   caxa3dmytoolsaddin\plugins\caxa3dcodenameextraction\caxa3dcodenameextraction-crx\
//   caxa3dcodenameextraction.cpp 的 TITLEBLOCK_3DSET / TB_ABORT 机制）
//
// 适配本工程协议（与参考的三点差异，只换壳不换意）：
//   ① 参考用 "CMD:VERB" 单行文本请求 + AppendLog 判据；本工程用 key=value 请求 +
//      必落恰好一条 WriteResp 回包 + Log() 判据。故两个 verb 的 handler 立即回 OK，
//      真正的判定结果异步落在日志里（REQ.tb-result / REQ.tb-giveup / REQ.tb-done /
//      REQ.tb-aborted），C# 侧 CrxBridge.LogCount 做子串匹配，语义完全对齐。
//   ② 参考日志格式 "HH:mm:ss | REQ.tb-result | ..."；本工程是
//      "HH:MM:SS.mmm [tid N] REQ.tb-result | ..."。标记串不变 ⇒ C# 无需改。
//   ③ 参考的 kPendingTitleBlock 是共享枚举；本工程收尾状态机用 g_job，
//      故标题栏单用 g_tbPending（0/1），互不干扰。
//
// 判据链（严格照搬，不许简化）：
//   4 通道轮换派发 CX_DFT_TITLEBLOCK_3DSET（chan3 键入通道优先 ×3，
//   chan1/0/2 crxedCommand 兜底）→ 命令结束后比对标题栏信息集合
//   （CRxDbPaperTableRecord::getTitleInfo / CRxAttCollInfoObject）前后 diff
//   → 有变化 = REQ.tb-result（成功）；试满 6 轮或 10s 总截止 = REQ.tb-giveup（失败）。
//   「设置3D信息」模态框弹着时暂停看门狗与重复派发，等 C# 的 Dlg3DInfo 自动填表点确定。
//=====================================================================

#ifndef CAXA3DBATCHMASTERVIEW_TITLEBLOCK_INL
#define CAXA3DBATCHMASTERVIEW_TITLEBLOCK_INL

// 标题栏专属头（与参考 CRX 同一套 CAXA 3D 2027 CRX SDK，两个 vcxproj 的 Inc/Lib 完全一致）
#include "dbapserv.h"          // crxdbHostApplicationServices
#include "dbid.h"
#include "dbents.h"
#include "cdscodes.h"          // RTSTR / RTENAME / RTNONE
#include "crxedcds.h"          // crxedCommand / crxedSSAdd / crxedSSFree / crxedSSLength
#include "dbsymtb.h"           // CRxDbPaperTableRecord / CRxAttCollInfoObject
#include "CrxDbBomGlobals.h"   // GetCrxDbTitleBlk

static const wchar_t* kTbCmd = L"CX_DFT_TITLEBLOCK_3DSET";

// 时序预算（逐字照搬参考；总截止与 C# 侧 10s 等待对齐）
static const DWORD kTbRetryMs      = 1000;
static const DWORD kTbRetryFastMs  = 1200;
static const DWORD kTbRunTimeoutMs = 5000;
static const DWORD kTbDeadlineMs   = 10000;
static const int   kTbMaxRuns      = 6;

// ---- 标题栏派发状态 ----
static volatile bool g_tbWanted    = false;   // 已收到请求、尚未确认成功
static volatile bool g_tbTrying    = false;
static volatile bool g_tbRunActive = false;
static volatile bool g_tbEnded     = false;   // reactor 置位：本轮命令已结束
static volatile int  g_tbPending   = 0;      // 本轮待判定（替代参考的 kPendingTitleBlock）
static DWORD         g_tbLastTry   = 0;
static DWORD         g_tbQueuedAt  = 0;
static volatile int  g_tbTries     = 0;
static volatile int  g_tbRuns      = 0;
static volatile int  g_tbChan      = -1;
static crx_name      g_tbSet;
static volatile bool g_tbSetValid  = false;
static std::map<std::string, std::string> g_tbPre, g_tbPost;
static int           g_tbChanged   = 0;

// CxCHAR*（Unicode 工程即 wchar_t*）→ UTF-8，供标题栏属性名/值落日志与 map 键
static std::string X2U8(const CxCHAR* w)
{
    if (!w || !*w) return std::string();
    return W2U8(std::wstring((const wchar_t*)w));
}

// 「设置3D信息」对话框是否正弹着（与 CRX 同进程：图纸环境内嵌在 IronCAD.exe）
static BOOL CALLBACK TbFind3DDlgProc(HWND h, LPARAM lp)
{
    wchar_t cls[64] = {0};
    GetClassNameW(h, cls, 64);
    if (wcscmp(cls, L"#32770") != 0) return TRUE;
    wchar_t txt[256] = {0};
    GetWindowTextW(h, txt, 256);
    if (wcsstr(txt, L"设置3D信息")) { *(HWND*)lp = h; return FALSE; }
    return TRUE;
}
static HWND TbFind3DDlg()
{
    HWND found = NULL;
    EnumWindows(TbFind3DDlgProc, (LPARAM)&found);
    return found;
}

// ---- 标题栏信息快照（官方 CRX 入口，免对话框）----
static bool TbOpenPaper(CRxDbPaperTable*& pTable, CRxDbPaperTableRecord*& pPaper)
{
    pTable = NULL; pPaper = NULL;
    CRxDbDatabase* pDb = crxdbHostApplicationServices()->workingDatabase();
    if (!pDb) { Log("TB: no-db workingDatabase 为空"); return false; }
    if (pDb->getPaperTable(pTable, CRxDb::kForRead) != CDraft::eOk || !pTable)
    { Log("TB: no-papertable"); return false; }
    CRxDbObjectId paperId = pTable->CurrentPaperId();
    if (paperId.isNull()) { Log("TB: no-currentpaper"); pTable->close(); pTable = NULL; return false; }
    if (crxdbOpenObject(pPaper, paperId, CRxDb::kForRead) != CDraft::eOk || !pPaper)
    { Log("TB: open-paper-failed"); pTable->close(); pTable = NULL; return false; }
    return true;
}
static void TbClosePaper(CRxDbPaperTable*& pTable, CRxDbPaperTableRecord*& pPaper)
{
    if (pPaper) { pPaper->close(); pPaper = NULL; }
    if (pTable) { pTable->close(); pTable = NULL; }
}
static bool TbReadInfo(std::map<std::string, std::string>& out)
{
    out.clear();
    CRxDbPaperTable* pTable = NULL; CRxDbPaperTableRecord* pPaper = NULL;
    if (!TbOpenPaper(pTable, pPaper)) return false;
    CRxAttCollInfoObject* pInfo = NULL;
    pPaper->getTitleInfo(&pInfo);
    bool had = (pInfo != NULL);
    int n = had ? pInfo->getAttributesCount() : 0;
    for (int i = 0; i < n; ++i)
    {
        CxCHAR* pName = NULL;
        pInfo->getAttributeName(pName, i);
        if (!pName) continue;
        CxCHAR* pVal = NULL;
        pInfo->getAttributeValue(pVal, pName);
        out[X2U8(pName)] = pVal ? X2U8(pVal) : std::string();
    }
    if (pInfo) pPaper->ReleaseAttCollInfoObject(pInfo);
    TbClosePaper(pTable, pPaper);
    return had;
}
static void TbDiffInfo(const char* phase)
{
    int changed = 0, added = 0;
    for (std::map<std::string, std::string>::const_iterator it = g_tbPre.begin(); it != g_tbPre.end(); ++it)
    {
        std::map<std::string, std::string>::const_iterator jt = g_tbPost.find(it->first);
        if (jt == g_tbPost.end() || jt->second == it->second) continue;
        changed++;
        Log("TB: diff-item %hs %hs: %hs -> %hs", phase, it->first.c_str(),
            it->second.empty() ? "<empty>" : it->second.c_str(),
            jt->second.empty() ? "<empty>" : jt->second.c_str());
    }
    for (std::map<std::string, std::string>::const_iterator jt = g_tbPost.begin(); jt != g_tbPost.end(); ++jt)
    {
        if (g_tbPre.find(jt->first) != g_tbPre.end()) continue;
        added++;
        Log("TB: diff-item %hs +%hs = %hs", phase, jt->first.c_str(), jt->second.c_str());
    }
    g_tbChanged = changed + added;
    Log("TB: diff %hs pre=%d post=%d changed=%d added=%d", phase,
        (int)g_tbPre.size(), (int)g_tbPost.size(), changed, added);
}

// 标题栏实体 → 临时选择集（通道 0/1 的 RTENAME 输入；集合必须活到命令真正执行）
static bool TbBuildSet(crx_name ssOut)
{
    ssOut[0] = 0; ssOut[1] = 0;
    CRxDbObjectId tbId = GetCrxDbTitleBlk(NULL);
    if (tbId.isNull()) { Log("TB: tmp-set-noent GetCrxDbTitleBlk 返回空"); return false; }
    crx_name nm; nm[0] = 0; nm[1] = 0;
    if (crxdbGetAdsName(nm, tbId) != CDraft::eOk) return false;
    int st = crxedSSAdd(nm, NULL, ssOut);
    long len = 0;
    crxedSSLength(ssOut, &len);
    Log("TB: tmp-set crxedSSAdd ret=%d len=%ld", st, len);
    return len > 0;
}

// 通道 3：定时器里直接 sendStringToExecute（本工程 DoExecNative 同款，实机已验）
static void TbSendStringDirect()
{
    if (!crxDocManager || !crxDocManager->mdiActiveDocument())
    { Log("REQ.tb-sendstr | no-doc"); return; }
    CRxApDocument* pDoc = crxDocManager->mdiActiveDocument();
    std::wstring s = std::wstring(kTbCmd) + L" ";
    int before = crxDocManager->inputPending(pDoc);
    CDraft::ErrorStatus es = crxDocManager->sendStringToExecute(pDoc, s.c_str(), true, false, true);
    int after = crxDocManager->inputPending(pDoc);
    Log("REQ.tb-sendstr | send=%d pending=%d->%d cmd=%hs",
        (int)es, before, after, W2U8(s).c_str());
}

// ---- 看门狗：每 100ms 由 BridgeTick 推进（①单轮超时 ②diff 判定 ③总截止 ④通道轮换 ⑤无文档停止）----
static void TitleBlockTick()
{
    if (!g_tbWanted) return;

    HWND tbDlg = TbFind3DDlg();
    static bool s_dlgLogged = false;
    if (tbDlg)
    {
        if (!s_dlgLogged)
        {
            s_dlgLogged = true;
            Log("REQ.tb-dialog | 检测到「设置3D信息」弹窗 hwnd=0x%p：暂停单轮看门狗与重复派发，等对话框确认后按 TB.diff 判定", (void*)tbDlg);
        }
    }
    else s_dlgLogged = false;

    // ① 单轮看门狗（弹窗开着时命令本来就不会结束，不判超时）
    if (g_tbRunActive && !tbDlg && (GetTickCount() - g_tbLastTry) >= kTbRunTimeoutMs)
    {
        g_tbRunActive = false;
        g_tbEnded = true;
        Log("REQ.tb-run-timeout | 命令长时间未结束 -> 强制结束本轮并做信息比对");
    }

    // ② 本轮结束 → 用「标题栏信息集合是否真的变化」判定成功
    if (g_tbEnded)
    {
        g_tbEnded = false;
        g_tbRunActive = false;
        g_tbPending = 0;
        g_tbPost.clear();
        TbReadInfo(g_tbPost);
        TbDiffInfo("post");
        g_tbRuns++;
        Log("REQ.tb-done | run=%d try=%d chan=%d pre=%d post=%d changed=%d",
            g_tbRuns, g_tbTries, g_tbChan, (int)g_tbPre.size(), (int)g_tbPost.size(), g_tbChanged);

        bool deadlineHit = ((GetTickCount() - g_tbQueuedAt) >= kTbDeadlineMs);
        if (g_tbChanged > 0)
        {
            g_tbWanted = false; g_tbTrying = false;
            Log("REQ.tb-result | 标题栏信息已变化 -> 判定为设置成功");
        }
        else if (g_tbRuns >= kTbMaxRuns || deadlineHit)
        {
            g_tbWanted = false; g_tbTrying = false;
            Log("REQ.tb-giveup | 通道轮换已试完，标题栏信息无任何变化 -> 判定失败");
        }
        else
        {
            g_tbTrying = true; g_tbLastTry = GetTickCount();
            Log("REQ.tb-nochange | 命令已执行但标题栏信息无变化 -> 换通道重试");
        }
    }

    // ③ 总截止兜底（编辑器长时间忙、命令始终未启动）
    if (g_tbWanted && !g_tbRunActive && (GetTickCount() - g_tbQueuedAt) >= kTbDeadlineMs)
    {
        g_tbWanted = false; g_tbTrying = false; g_tbPending = 0;
        Log("REQ.tb-giveup | 总截止时间已达（编辑器长时间忙/命令始终未启动）-> 判定失败");
    }

    // ④ 4 通道轮换派发（chan3 键入通道优先，参考实证前三条 crxedCommand 通道 runs=0 只作兜底）
    if (g_tbWanted && !g_tbRunActive && !tbDlg)
    {
        DWORD now = GetTickCount();
        DWORD retryMs = (g_tbChan == 3 && g_tbTries <= 3) ? kTbRetryFastMs : kTbRetryMs;
        if (!g_tbTrying || (now - g_tbLastTry) >= retryMs)
        {
            if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
            bool firstTry = (g_tbTries == 0);
            g_tbTrying = true; g_tbLastTry = now; g_tbTries++;
            if (firstTry)
            {
                g_tbPre.clear();
                TbReadInfo(g_tbPre);     // 「前」快照 = 成功判定基线
                g_tbPost.clear();
                g_tbChanged = 0;
                Log("TB: pre attrs=%d", (int)g_tbPre.size());
            }
            g_tbPending = 1;
            g_tbEnded = false;

            static const int kChanOrder[6] = { 3, 3, 3, 1, 0, 2 };
            g_tbChan = kChanOrder[(g_tbTries - 1) % 6];
            bool needSet = (g_tbChan == 0 || g_tbChan == 1);
            bool setOk = needSet ? TbBuildSet(g_tbSet) : false;
            if (setOk) g_tbSetValid = true;
            if (needSet && !setOk) g_tbChan = 2;

            int st = -1;
            const char* desc = "";
            if (g_tbChan == 0)
            {
                st = crxedCommand(RTSTR, kTbCmd, RTENAME, g_tbSet, RTNONE);
                desc = "crxedCommand + RTENAME(titleblk)";
            }
            else if (g_tbChan == 1)
            {
                st = crxedCommand(RTSTR, kTbCmd, RTENAME, g_tbSet, RTSTR, L"default", RTNONE);
                desc = "crxedCommand + RTENAME(titleblk) + default";
            }
            else if (g_tbChan == 2)
            {
                st = crxedCommand(RTSTR, kTbCmd, RTNONE);
                desc = "crxedCommand bare";
            }
            else
            {
                TbSendStringDirect();
                st = 0;
                desc = "sendStringToExecute direct";
            }
            Log("REQ.tb-dispatch-try | chan=%d try=%d runs=%d ret=%d setOk=%d next=%dms %hs",
                g_tbChan, g_tbTries, g_tbRuns, st, setOk ? 1 : 0, (int)retryMs, desc);
        }
    }

    // ⑤ 文档没了就别再轮换（否则命令会被排队到「下一个打开的文档」上抢跑）
    if (g_tbWanted && (!crxDocManager || crxDocManager->mdiActiveDocument() == NULL))
    {
        g_tbWanted = false; g_tbTrying = false; g_tbRunActive = false; g_tbPending = 0;
        if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
        Log("REQ.tb-stop-nodoc | 当前没有活动图纸文档 -> 立即停止标题栏派发（不判失败，交 C# 侧收尾）");
    }
}

// ---- 两个 verb handler（本工程协议：立即落一条回包；判定结果异步落日志）----
static void DoTitleBlock3DSet(int seq, const std::wstring& /*arg*/)
{
    if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
    g_tbWanted    = true;
    g_tbTrying    = false;
    g_tbRunActive = false;
    g_tbTries     = 0;
    g_tbRuns      = 0;
    g_tbChan      = -1;
    g_tbChanged   = 0;
    g_tbEnded     = false;
    g_tbLastTry   = 0;
    g_tbQueuedAt  = GetTickCount();
    g_tbPending   = 1;
    Log("REQ.tb-queued | 标题栏 3D 信息设置已入队：4 通道轮换，以标题栏信息变化为成功判据（seq=%d）", seq);
    WriteResp(seq, L"TITLEBLOCK_3DSET", L"OK", L"tb-queued");
}

static void DoTbAbort(int seq, const std::wstring& /*arg*/)
{
    g_tbWanted    = false;
    g_tbTrying    = false;
    g_tbRunActive = false;
    g_tbEnded     = false;
    g_tbPending   = 0;
    if (g_tbSetValid) { crxedSSFree(g_tbSet); g_tbSetValid = false; }
    Log("REQ.tb-aborted | 收到停止请求 -> 已终止标题栏 3D 信息派发与判定（不保存、不关图）");
    WriteResp(seq, L"TB_ABORT", L"OK", L"tb-aborted");
}

#endif
