// caxa3dapicapture.cpp — CAXA 3D 2027 图纸环境 CRX 事件桥接
//
// 作用：在图纸环境内挂 CRxEditorReactor（命令事件）+ CRxDbDatabaseReactor（对象事件），
// 每个回调追加一行到 %TEMP%\caxa3d_crx_events.log，由 Add-in DLL 读取并展示到 UI。
//
// 日志格式（每行，ASCII）：
//   HH:mm:ss | SOURCE.eventName | arg1 | arg2
//
// 部署：编译输出 .crx → 复制到 C:\Program Files\CAXA\CAXA 3D\2027\bin\CAXADraft\Bin\
//       CAXA 3D 启动时自动加载该目录下所有 .crx 模块。

#include "StdAfx.h"

#include <set>

#include "dbapserv.h"
#include "dbid.h"
#include "dbmain.h"
#include "dbents.h"
#include "crxed.h"
#include "crxedcds.h"
#include "cdscodes.h"
#include "crxentrypoint.h"
#include "rxevent.h"

AC_IMPLEMENT_EXTENSION_MODULE(Caxa3dApiCaptureDLL)

//-----------------------------------------------------------------------------
// 共享状态
//-----------------------------------------------------------------------------
static CRxEditorReactor*     g_pEditorReactor = NULL;
static CRxDbDatabaseReactor* g_pDbReactor     = NULL;
static CRxDbDatabase*        g_pWatchedDb     = NULL;
static CRITICAL_SECTION      g_logCS;
static bool                  g_initialized     = false;
static HANDLE                g_hLogFile        = INVALID_HANDLE_VALUE;

//-----------------------------------------------------------------------------
// 工具：打开/创建日志文件（追加模式，Windows API 避免 CRT 冲突）
//-----------------------------------------------------------------------------
static void OpenLogFile()
{
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"TEMP", path, MAX_PATH);
    if (n == 0) wcscpy_s(path, MAX_PATH, L".");
    wchar_t* last = wcsrchr(path, L'\\');
    if (last && *(last + 1) == 0) path[last - path + 1] = 0;
    wcscat_s(path, MAX_PATH, L"\\caxa3d_crx_events.log");
    g_hLogFile = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             NULL, OPEN_ALWAYS, 0, NULL);
}

//-----------------------------------------------------------------------------
// 工具：追加一行（CHAR* → WriteFile）
//-----------------------------------------------------------------------------
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
// 工具：CxCHAR (wchar_t) → CStringA
// 命名刻意避开 MFC/ATL 宏名（W2A 是 ATL 宏！）
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
// 工具：CRxDbObject → 可读字符串
//-----------------------------------------------------------------------------
static CStringA DbObjDescribe(const CRxDbObject* obj)
{
    if (!obj) return CStringA("(null)");
    CRxClass* cls = obj->isA();
    CStringA clsName = CxCharToMbs(cls ? cls->name() : NULL);
    if (clsName.IsEmpty()) clsName = CStringA("Unknown");
    CAXA::IntDbId oldId = obj->objectId().asOldId();
    CStringA s;
    s.Format("%s Id=%lld", clsName, (long long)oldId);
    return s;
}

//-----------------------------------------------------------------------------
// 命令前后数据库快照：DB reactor 在内嵌图纸环境不触发，
// 改为在命令开始时记下当前空间的对象集合，命令结束时比对新增对象。
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

// 命令结束：比对快照，把新增/删除对象写进日志
// 说明：CRxDbObject::isModified() 在 CAXA 的 CrxDb.lib 里没有导出符号，
//       "修改现有对象"类命令（如 CX_DFT_ChangeLinks）无法靠脏标记识别，只能看到 +0 -0。
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
                    if (!added.IsEmpty()) added += ", ";
                    added += DbObjDescribe(pEnt);
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
// 选中对象记录：只改现有对象、不新增对象的命令（如 CX_DFT_ChangeLinks）
// 靠快照看不到任何变化，这里读取当前选择集（pickfirst）来定位被操作对象。
// 注意：crxedSSGetFirst 在 crxedcds.h 里声明了，但 CrxEdApi.lib 并未导出该符号
//       （链接期 LNK2019），所以改用确实导出的 crxedSSGet + "I"（pickfirst）模式，
//       该模式只读不提示、不修改用户选择集。
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

        if (!detail.IsEmpty()) detail += ", ";
        detail += DbObjDescribe(pEnt);
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
// 挂 reactor（幂等）
// 注意：CAXA 3D 内嵌图纸环境里 On_kLoadDwgMsg 实际不触发，
//       所以必须在 On_kInitAppMsg 及编辑器回调里补挂。
//-----------------------------------------------------------------------------
static void EnsureReactorsAttached(const char* why)
{
    if (g_pEditorReactor)
    {
        static bool editorAttached = false;
        if (!editorAttached)
        {
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
    }

    CRxDbDatabase* pDb = crxdbHostApplicationServices()->workingDatabase();
    if (pDb && g_pDbReactor && g_pWatchedDb != pDb)
    {
        if (g_pWatchedDb) { try { g_pWatchedDb->removeReactor(g_pDbReactor); } catch (...) {} }
        try {
            pDb->addReactor(g_pDbReactor);
            g_pWatchedDb = pDb;
            CStringA a; a.Format("db @%s", why);
            AppendLog("MOD", "attach", a);
        } catch (...) {
            CStringA a; a.Format("db FAILED @%s", why);
            AppendLog("MOD", "attach", a);
        }
    }
}

//-----------------------------------------------------------------------------
// CRxEditorReactor —— 命令事件
//-----------------------------------------------------------------------------
class CEditorReactor : public CRxEditorReactor
{
public:
    CEditorReactor() {}
    virtual void commandWillStart(const CxCHAR* cmdStr) override
    {
        // 第一次收到命令时补挂 DB reactor（此时文档数据库一定已就绪）
        EnsureReactorsAttached("cmdStart");
        CStringA cmd = CxCharToMbs(cmdStr);
        AppendLog("EDITOR", "commandWillStart", cmd);
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
        // 命令结束优先读选择集：只改现有对象不改数量的命令（如 CX_DFT_ChangeLinks）
        // 只能靠这里定位被操作对象
        ReportSelection("end");
        if (g_snapValid && cmd == g_snapCmd) ReportSnapshotDelta(cmd, "ended");
    }
    virtual void commandCancelled(const CxCHAR* cmdStr) override
    {
        CStringA cmd = CxCharToMbs(cmdStr);
        AppendLog("EDITOR", "commandCancelled", cmd);
        ReportSelection("cancel");
        if (g_snapValid && cmd == g_snapCmd) ReportSnapshotDelta(cmd, "cancelled");
    }
    virtual void pickfirstModified() override
    { AppendLog("EDITOR", "pickfirstModified"); }
    virtual void commandFailed(const CxCHAR* cmdStr) override
    {
        CStringA cmd = CxCharToMbs(cmdStr);
        AppendLog("EDITOR", "commandFailed", cmd);
        if (g_snapValid && cmd == g_snapCmd) ReportSnapshotDelta(cmd, "failed");
    }
    virtual void unknownCommand(const CxCHAR* cmdStr, CRxDbVoidPtrArray* al) override
    { AppendLog("EDITOR", "unknownCommand", CxCharToMbs(cmdStr)); }
    virtual void viewChanged() override
    {
        EnsureReactorsAttached("viewChanged");
        AppendLog("EDITOR", "viewChanged");
    }
};

//-----------------------------------------------------------------------------
// CRxDbDatabaseReactor —— 对象级事件
// CAXA::Boolean 默认是 int（cdraft.h 定义），不是 bool
//-----------------------------------------------------------------------------
class CDbReactor : public CRxDbDatabaseReactor
{
public:
    CDbReactor() {}
    virtual void objectAppended(const CRxDbDatabase* dwg, const CRxDbObject* dbObj) override
    { AppendLog("DB", "objectAppended", DbObjDescribe(dbObj)); }
    virtual void objectUnAppended(const CRxDbDatabase* dwg, const CRxDbObject* dbObj) override
    { AppendLog("DB", "objectUnAppended", DbObjDescribe(dbObj)); }
    virtual void objectReAppended(const CRxDbDatabase* dwg, const CRxDbObject* dbObj) override
    { AppendLog("DB", "objectReAppended", DbObjDescribe(dbObj)); }
    virtual void objectOpenedForModify(const CRxDbDatabase* dwg, const CRxDbObject* dbObj) override
    { AppendLog("DB", "objectOpenedForModify", DbObjDescribe(dbObj)); }
    virtual void objectModified(const CRxDbDatabase* dwg, const CRxDbObject* dbObj) override
    { AppendLog("DB", "objectModified", DbObjDescribe(dbObj)); }
    virtual void objectErased(const CRxDbDatabase* dwg, const CRxDbObject* dbObj,
                              CAXA::Boolean pErased) override
    { AppendLog("DB", pErased ? "objectErased" : "objectUnErased", DbObjDescribe(dbObj)); }
};

//-----------------------------------------------------------------------------
// 模块入口
//-----------------------------------------------------------------------------
class CCaxa3dApiCaptureApp : public AcRxArxApp
{
public:
    CCaxa3dApiCaptureApp() : AcRxArxApp() {}

    virtual AcRx::AppRetCode On_kInitAppMsg(void* pkt) override
    {
        // 最早期的加载探针：不依赖 MFC / CriticalSection / 任何全局状态。
        // 只要模块被 CAXA 加载，这里就会留下标记文件；用于区分「没加载」与「加载后崩溃」。
        {
            wchar_t markPath[MAX_PATH];
            DWORD mn = GetEnvironmentVariableW(L"TEMP", markPath, MAX_PATH);
            if (mn == 0) wcscpy_s(markPath, MAX_PATH, L".");
            wcscat_s(markPath, MAX_PATH, L"\\caxa3d_crx_loaded.marker");
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

        // 清空旧日志
        wchar_t path[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"TEMP", path, MAX_PATH);
        if (n == 0) wcscpy_s(path, MAX_PATH, L".");
        wchar_t* last = wcsrchr(path, L'\\');
        if (last && *(last + 1) == 0) path[last - path + 1] = 0;
        wcscat_s(path, MAX_PATH, L"\\caxa3d_crx_events.log");
        DeleteFileW(path);
        OpenLogFile();

        AppendLog("MOD", "On_kInitAppMsg", "CRX module loaded");
        g_pEditorReactor = new CEditorReactor();
        g_pDbReactor     = new CDbReactor();
        EnsureReactorsAttached("init");   // 内嵌图纸环境里 On_kLoadDwgMsg 不触发，这里先挂
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
        if (g_pWatchedDb && g_pDbReactor) { try { g_pWatchedDb->removeReactor(g_pDbReactor); } catch (...) {} }
        delete g_pDbReactor;     g_pDbReactor     = NULL;
        delete g_pEditorReactor; g_pEditorReactor = NULL;
        g_pWatchedDb = NULL;
        if (g_hLogFile != INVALID_HANDLE_VALUE) { CloseHandle(g_hLogFile); g_hLogFile = INVALID_HANDLE_VALUE; }
        g_initialized = false;
        DeleteCriticalSection(&g_logCS);
        return AcRxArxApp::On_kUnloadAppMsg(pkt);
    }

    virtual void RegisterServerComponents() override {}
};

IMPLEMENT_ARX_ENTRYPOINT(CCaxa3dApiCaptureApp)
