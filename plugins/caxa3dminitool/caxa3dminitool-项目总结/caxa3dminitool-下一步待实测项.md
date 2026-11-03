# caxa3dminitool-下一步待实测项

## 待验证 / 下一步

- 首次实机验证点：选中拉伸特征后点「增料除料切换」，看日志是否出现「重建后回读一致」，并观察模型几何是否真的发生变化。
- 若 `IZFeature` QI 在实机上失败，日志会留下 `FindFeatureByElement` 的 trace，可按 trace 结果决定是否改用 `IZRayTestResult.HitFeature` 通道。

