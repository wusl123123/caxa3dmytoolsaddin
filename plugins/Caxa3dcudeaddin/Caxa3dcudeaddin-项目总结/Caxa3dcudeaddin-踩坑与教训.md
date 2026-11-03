# Caxa3dcudeaddin-踩坑与教训

## 三、踩坑

1. **必须在 InitSelf 线程预创建窗口句柄**：推迟到功能区 OnClick 才 `CreateHandle()`，窗口创建/激活的跨窗口消息会与正同步等待回调返回的 CAXA 主线程互等，直接死锁（2026-10-22 实测：点按钮后 CAXA 无响应，日志停在 OnClick 之前）。
2. **`CreatePart` 可能“报错但已建件”**：服务器端返回 `0x80010105`，实际零件已生成。不能只看 hr，需重新枚举场景零件、从末尾回收新零件后建块。
3. **子进程兜底不能阻塞等待**：进程内等待子进程时若不泵消息，CAXA 主线程被堵，子进程的 COM 回调随之死锁。
4. **指针生命周期**：`CreatePart` / `CreateBlock` 返回的指针只用于判断 hr，随即 `Release`；场景/零件指针在 `finally` 中释放。
5. **迁入集合体后不做 COM 入口**：删除 `[Guid]` / `[ProgId]` / 类级 `[ComVisible(true)]`，程序集级 `ComVisible(false)`，插件 DLL 不内嵌 SxS 清单。
6. **进程内会话不释放 RCW**：`CaxaSession.Dispose()` 只释放接口指针，CAXA 对象归宿主所有。

