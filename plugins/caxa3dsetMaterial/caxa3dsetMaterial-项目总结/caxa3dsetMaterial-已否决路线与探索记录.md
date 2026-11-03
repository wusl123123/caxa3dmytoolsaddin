# caxa3dsetMaterial-已否决路线与探索记录

## 已否决清单

- ~~Addin 入口类使用 `ComVisible(false)`~~：导致 clrClass 缺失、Add-in 静默不加载（2026-10-28 实证）。永久改为入口类 `ComVisible(true)` + 程序集级 `false`。
- ~~COM RCW 对象用 `GetType().GetMethods()` 看接口方法~~：RCW 返回 `__ComObject`，反射只看到基类方法（ToString/GetType 等），接口方法全部不可见。**直接用 managed 接口方法**（编译期就有），永远别对 RCW 用反射。
- ~~`IZAssembly.GetAllLeafParts(eZPartType, Int32)` 取叶子零件~~：managed 签名返回 `Object`，运行时封送成 **`int[]`（元素 Id）**，无 SAFEARRAY VT_VARIANT 映射，废弃。改用 `IZAssembly.GetChildrenZArray()` + `IZElement.GetChildrenZArray()` 递归穿透，ZArray 用 `Get(i, out var)` 解包不受 SAFEARRAY 类型影响。
- ~~restore.ps1 用 `*.bak` 整份覆盖回滚 config/manifest~~：这两个文件是所有 Add-in 共享的，整份回滚会抹掉其他插件的登记。永久改为"手术式"XML 节点移除 + 正则兜底。

