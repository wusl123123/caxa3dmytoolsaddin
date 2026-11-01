// caxa3dCXDFTViewUpdate — CAXA 3D 2027 图纸环境 CRX 命令执行桥
// StdAfx.h 预编译头（对齐参考项目 caxa3dapicapture + 官方 Chapter01_HelloWorld）
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

// ObjectARX/OMF headers need this
#include <map>
#include <vector>
#include <string>

// ---- MFC（必须放在 Windows headers 之前；afxwin.h 间接 include windows.h）----
#include <afxwin.h>       // MFC core and standard components
#include <afxext.h>       // MFC extensions

// ---- CRT ----
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- CRX SDK（pragma comment 让 CRT 自动链接，无需 vcxproj 额外配置）----
#pragma comment(lib, "Crx.lib")
#pragma comment(lib, "CrxDb.lib")
#pragma comment(lib, "CrxEdApi.lib")
#pragma comment(lib, "CrxGe.lib")
#pragma comment(lib, "CrxGi.lib")

#include "CRxDefine.h"      // CRX 全局宏 + crxSysRegistry / crxedEditor 等
#include "rxdict.h"         // CRxDictionary
#include "crxcmd.h"         // CRxCommand（reactor 需要）
#include "crxEntryPoint.h"  // IMPLEMENT_ARX_ENTRYPOINT 宏
#include "CRxExtensionModule.h" // AC_IMPLEMENT_EXTENSION_MODULE

#pragma pack(pop)
