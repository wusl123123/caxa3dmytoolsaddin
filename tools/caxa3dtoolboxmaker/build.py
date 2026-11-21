# -*- coding: utf-8 -*-
"""caxa3dtoolboxmaker 编译脚本：源码转 UTF-8 BOM + 编译 GUI exe 与 selftest console exe。"""
import os, sys, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
SKILL = r"C:\Users\Administrator\.workbuddy\skills\csharp-winforms-no-vs\scripts\build_cs.py"
SRC = os.path.join(HERE, "src", "caxa3dtoolboxmaker.cs")
DIST = os.path.join(HERE, "dist")
os.makedirs(DIST, exist_ok=True)

# 1) 源码统一 UTF-8 with BOM（csc 无 BOM 会按 GBK 读，中文乱码）
raw = open(SRC, "rb").read()
if not raw.startswith(b"\xef\xbb\xbf"):
    open(SRC, "wb").write(b"\xef\xbb\xbf" + raw)
    print("[OK] 源码已加 UTF-8 BOM")
else:
    print("[OK] 源码已是 UTF-8 BOM")

WINDOWSBASE = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll"
REFS = [WINDOWSBASE]

def run(args):
    cmd = [sys.executable, SKILL] + args
    p = subprocess.run(cmd)
    return p.returncode

# 2) GUI 主程序（winexe，入口 Program）
rc1 = run([SRC, "-o", os.path.join(DIST, "caxa3dtoolboxmaker.exe"),
           "--target", "winexe", "--main", "Caxa3dToolboxMaker.Program",
           "--ref", WINDOWSBASE])
# 3) 自检控制台程序（exe，入口 SelftestMain，共用同一份源码）
rc2 = run([SRC, "-o", os.path.join(DIST, "caxa3dtoolboxmaker_selftest.exe"),
           "--target", "exe", "--main", "Caxa3dToolboxMaker.SelftestMain",
           "--ref", WINDOWSBASE])

print("RC_GUI=%d RC_SELFTEST=%d" % (rc1, rc2))
sys.exit(rc1 or rc2)
