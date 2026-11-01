# Create L(X) x W(Y) x H(Z) block at origin (0,0,0) in the running CAXA 3D via ICAPI vtable calls.
# Dimensions are passed in METERS via -Lx -Ly -Lz (default 0.01 = 10 mm).
# All comments/strings are English on purpose: PS 5.1 misparses UTF-8 files without BOM.
#
# Design notes (hard-won lessons):
#  - NO Marshal.Release anywhere: releasing the server proxies crashed this child
#    with 0xC0000005 AFTER a successful block creation. Process teardown hands
#    the cleanup to RPC disconnect handling.
#  - The result marker file is written IMMEDIATELY after CreateBlock returns,
#    so the parent tool detects success even if this child dies at teardown.
#  - CreatePart + block on the FRESH part is the only proven-reliable path;
#    FM QI on pre-existing parts is unreliable from other processes.
param([double]$Lx = 0.01, [double]$Ly = 0.01, [double]$Lz = 0.01)
$ErrorActionPreference = 'Continue'

$markerFile = Join-Path $env:TEMP 'CaxaCreateCube_marker.txt'
$logFile    = Join-Path $env:TEMP 'CaxaCreateCube_result.log'
Set-Content -Path $markerFile -Value 'RUNNING' -ErrorAction SilentlyContinue

function Log($m) {
    $line = "[CC] " + $m
    Write-Host $line
    Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
}

$src = @'
using System;
using System.Runtime.InteropServices;
public class CaxaCube {
    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRawDispatch {
        [PreserveSig] int GetTypeInfoCount(out int pc);
        [PreserveSig] int GetTypeInfo(int i, int l, out IntPtr t);
        [PreserveSig] int GetIDsOfNames(ref Guid r, IntPtr n, int c, int l, IntPtr m);
        [PreserveSig] int Invoke(int m, ref Guid r, int l, ushort f, IntPtr dp, IntPtr res, IntPtr ex, IntPtr ae);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int FnGetObj(IntPtr self, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int FnGetVariant(IntPtr self, IntPtr pVar);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int FnCreateBlock(IntPtr self, [MarshalAs(UnmanagedType.Struct)] object pt1, [MarshalAs(UnmanagedType.Struct)] object pt2, out IntPtr ppFeat);

    [DllImport("oleaut32.dll")] static extern int SafeArrayAccessData(IntPtr psa, out IntPtr d);
    [DllImport("oleaut32.dll")] static extern int SafeArrayUnaccessData(IntPtr psa);
    [DllImport("oleaut32.dll")] static extern int SafeArrayGetLBound(IntPtr psa, uint dim, out int lo);
    [DllImport("oleaut32.dll")] static extern int SafeArrayGetUBound(IntPtr psa, uint dim, out int ub);
    [DllImport("oleaut32.dll")] static extern int SafeArrayDestroy(IntPtr psa);
    [DllImport("kernel32.dll")] static extern int SetErrorMode(int mode);

    public static void SuppressCrashDialogs() {
        // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
        SetErrorMode(0x8003);
    }

    public static IntPtr QI(object obj, string g) {
        IntPtr pUnk = Marshal.GetIUnknownForObject(obj);
        Guid iid = new Guid(g);
        IntPtr ppv;
        int hr = Marshal.QueryInterface(pUnk, ref iid, out ppv);
        Marshal.Release(pUnk);
        if (hr != 0) throw new Exception("QI " + g + " hr=0x" + hr.ToString("X"));
        return ppv;
    }
    public static int TryQI(IntPtr p, string g, out IntPtr ppv) {
        Guid iid = new Guid(g);
        return Marshal.QueryInterface(p, ref iid, out ppv);
    }
    static IntPtr Slot(IntPtr p, int s) { return Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), s * 8); }
    public static IntPtr CallGetObj(IntPtr p, int s, string n) {
        FnGetObj f = (FnGetObj)Marshal.GetDelegateForFunctionPointer(Slot(p, s), typeof(FnGetObj));
        IntPtr o; int hr = f(p, out o);
        if (hr != 0) throw new Exception(n + " hr=0x" + hr.ToString("X"));
        return o;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int FnCreateNewDoc(IntPtr self, int eType, int vbInteractive, int vbUseDefault,
        [MarshalAs(UnmanagedType.BStr)] string templateName, int vbVisible, out IntPtr ppDoc);
    public static IntPtr CreateNewDoc(IntPtr pBase) {
        FnCreateNewDoc f = (FnCreateNewDoc)Marshal.GetDelegateForFunctionPointer(Slot(pBase, 66), typeof(FnCreateNewDoc));
        IntPtr o; int hr = f(pBase, 1, 0, -1, string.Empty, -1, out o);
        if (hr != 0) throw new Exception("CreateNewDoc hr=0x" + hr.ToString("X"));
        return o;
    }
    public static IntPtr CallGetObjOrNull(IntPtr p, int s) {
        FnGetObj f = (FnGetObj)Marshal.GetDelegateForFunctionPointer(Slot(p, s), typeof(FnGetObj));
        IntPtr o; int hr = f(p, out o);
        if (hr != 0) throw new Exception("getobj slot " + s + " hr=0x" + hr.ToString("X"));
        return o;
    }
    public static int CreatePart(IntPtr pScene, int slot) {
        FnGetObj f = (FnGetObj)Marshal.GetDelegateForFunctionPointer(Slot(pScene, slot), typeof(FnGetObj));
        IntPtr o;
        return f(pScene, out o);
    }
    public static IntPtr[] Children(IntPtr pScene) {
        FnGetVariant f = (FnGetVariant)Marshal.GetDelegateForFunctionPointer(Slot(pScene, 7), typeof(FnGetVariant));
        IntPtr pVar = Marshal.AllocCoTaskMem(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(pVar, i, 0);
        int hr = f(pScene, pVar);
        if (hr != 0) throw new Exception("GetChildElements hr=0x" + hr.ToString("X"));
        short vt = Marshal.ReadInt16(pVar);
        if ((vt & 0x2000) == 0) return new IntPtr[0];
        IntPtr psa = Marshal.ReadIntPtr(pVar, 8);
        int lo, ub; SafeArrayGetLBound(psa, 1, out lo); SafeArrayGetUBound(psa, 1, out ub);
        IntPtr d; SafeArrayAccessData(psa, out d);
        IntPtr[] a = new IntPtr[ub - lo + 1];
        for (int i = 0; i < a.Length; i++) a[i] = Marshal.ReadIntPtr(d, i * 8);
        SafeArrayUnaccessData(psa);
        SafeArrayDestroy(psa);
        Marshal.FreeCoTaskMem(pVar);
        return a;
    }
    public static int CreateBlock(IntPtr pFm, int slot, double[] pt1, double[] pt2) {
        FnCreateBlock f = (FnCreateBlock)Marshal.GetDelegateForFunctionPointer(Slot(pFm, slot), typeof(FnCreateBlock));
        IntPtr o;
        return f(pFm, pt1, pt2, out o);
    }
    public static object GetZ(object app) {
        Guid iid = Guid.Empty;
        IntPtr pDp = Marshal.AllocCoTaskMem(32);
        for (int i = 0; i < 32; i++) Marshal.WriteByte(pDp, i, 0);
        IntPtr pRes = Marshal.AllocCoTaskMem(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(pRes, i, 0);
        IntPtr pEx = Marshal.AllocCoTaskMem(80);
        for (int i = 0; i < 80; i++) Marshal.WriteByte(pEx, i, 0);
        IntPtr pAE = Marshal.AllocCoTaskMem(4);
        int hr = ((IRawDispatch)app).Invoke(0x2A, ref iid, 0, 2, pDp, pRes, pEx, pAE);
        if (hr != 0) throw new Exception("ZIronCADApp hr=0x" + hr.ToString("X"));
        IntPtr po = Marshal.ReadIntPtr(pRes, 8);
        object o = Marshal.GetObjectForIUnknown(po);
        Marshal.Release(po);
        return o;
    }
}
'@
Add-Type -TypeDefinition $src
[CaxaCube]::SuppressCrashDialogs()

$G_BaseApp = 'A5B9AF7D-228B-4ED1-BA42-D51318F7EA20'
$G_Scene   = '3845C6DA-89D6-43F7-8B10-4BE36E0E758B'
$G_Part    = 'F20F2411-05C4-4CA6-B2B4-5DB29531EDD7'
$G_FeatMgr = '26D70B9B-291B-4727-9DA1-E2D7EEC1FA27'

try {
    # Only attach to a foreground instance with a visible main window;
    # background leftover processes (no window) are not valid targets.
    $visible = Get-Process -Name IRONCAD -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }
    if (-not $visible) { throw "no IRONCAD process with a main window" }

    $app = [System.Runtime.InteropServices.Marshal]::GetActiveObject("IRONCAD.Application")
    Log "app ok"
    $z = [CaxaCube]::GetZ($app)
    $pBase = [CaxaCube]::QI($z, $G_BaseApp)
    $pDoc = [CaxaCube]::CallGetObjOrNull($pBase, 11)
    if ($pDoc -eq [IntPtr]::Zero) {
        Log "no active doc, creating new scene doc"
        $pDoc = [CaxaCube]::CreateNewDoc($pBase)
        Log ("CreateNewDoc ptr=0x" + $pDoc.ToString("X"))
    }
    $pScene = [IntPtr]::Zero
    $hrS = [CaxaCube]::TryQI($pDoc, $G_Scene, [ref]$pScene)
    Log ("scene hr=0x" + $hrS.ToString("X"))

    $hrCP = [CaxaCube]::CreatePart($pScene, 9)
    Log ("CreatePart hr=0x" + $hrCP.ToString("X"))

    $kids = [CaxaCube]::Children($pScene)
    Log ("children=" + $kids.Length)

    $done = $false
    for ($i = $kids.Length - 1; $i -ge 0; $i--) {
        if ($done) { break }
        $e = $kids[$i]
        if ($e -eq [IntPtr]::Zero) { continue }
        $pp = [IntPtr]::Zero
        $hrP = [CaxaCube]::TryQI($e, $G_Part, [ref]$pp)
        if ($hrP -ne 0) {
            Log ("child[" + $i + "] IZPart hr=0x" + $hrP.ToString("X"))
        } else {
            $pf = [IntPtr]::Zero
            $hrF = [CaxaCube]::TryQI($pp, $G_FeatMgr, [ref]$pf)
            if ($hrF -ne 0) {
                Log ("part[" + $i + "] FM hr=0x" + $hrF.ToString("X"))
            } else {
                $pt1 = [double[]]@(0.0, 0.0, 0.0)
                $pt2 = [double[]]@($Lx, $Ly, $Lz)
                $hrB = [CaxaCube]::CreateBlock($pf, 19, $pt1, $pt2)
                Log ("part[" + $i + "] CreateBlock hr=0x" + $hrB.ToString("X"))
                if ($hrB -eq 0) { $done = $true }
            }
        }
    }

    # Marker is the single source of truth for the parent tool.
    # Write it NOW, before anything else can fail or crash.
    if ($done) {
        Set-Content -Path $markerFile -Value 'BLOCK CREATED' -ErrorAction Stop
        Write-Host "BLOCK CREATED"
    } else {
        Set-Content -Path $markerFile -Value 'BLOCK FAILED' -ErrorAction Stop
        Write-Host "BLOCK FAILED"
    }
    Log "DONE"
} catch {
    Log ("ERROR: " + $_.Exception.Message)
    Set-Content -Path $markerFile -Value ('ERROR: ' + $_.Exception.Message) -ErrorAction SilentlyContinue
    exit 1
}
