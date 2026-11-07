// caxa3dBatchMasterView — CAXA 3D 图纸环境 CRX 桥接模块（caxa3dBatchMaster「刷新工程图」专属）
// StdAfx.h 预编译头（桥接型 CRX 样板写法：
//   afxwin.h 必须在 windows.h 之前；不引 afxdllx.h——MFC 扩展模块结构由
//   CRxExtensionModule.h 的 AC_IMPLEMENT_EXTENSION_MODULE 自行提供）。
#pragma once

#pragma pack(push, 8)
#pragma warning(disable: 4786 4996)

#define STRICT
#ifndef VC_EXTRALEAN
#define VC_EXTRALEAN
#endif
#ifndef WINVER
#define WINVER 0x0601
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#ifndef _WIN32_IE
#define _WIN32_IE 0x0800
#endif

#include <map>
#include <vector>
#include <string>

// ---- MFC（规则第 11 节第 10 条：afxwin.h 必须在 windows.h 之前）----
#include <afxwin.h>
#include <afxext.h>

// ---- CRT ----
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- CRX SDK ----
#pragma comment(lib, "Crx.lib")
#pragma comment(lib, "CrxDb.lib")
#pragma comment(lib, "CrxEdApi.lib")
#pragma comment(lib, "CrxGe.lib")
#pragma comment(lib, "CrxGi.lib")
#pragma comment(lib, "CrxUi.lib")

#include "CRxDefine.h"
#include "rxdict.h"          // crxSysRegistry（crxEntryPoint.h 依赖）
#include "crxcmd.h"
#include "crxEntryPoint.h"
#include "CRxExtensionModule.h"

#pragma pack(pop)
