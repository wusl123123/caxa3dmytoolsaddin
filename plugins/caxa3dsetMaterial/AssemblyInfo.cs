using System.Runtime.InteropServices;

// 规则：程序集内所有公开类型不得污染 CAXA 3D 的内嵌 COM 清单。
// 已上线插件 caxa3dcodenameextraction 同样使用 ComVisible(false)，CAXA 加载正常。
[assembly: ComVisible(false)]
