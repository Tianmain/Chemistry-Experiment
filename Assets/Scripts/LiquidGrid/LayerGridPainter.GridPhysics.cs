// 本文件的方法已迁移到独立子系统，LayerGridPainter 不再是 partial 类。
// 原网格物理与模拟逻辑现位于：
//   - WaterSimulator.cs    （水/气泡流动、间隙填充、颜色扩散混合）
//   - ObjectGridTracker.cs （障碍重检、吸收洒落的水、容器内部跟踪、初始化）
// 该文件故意留空，仅作说明，避免与协调器 LayerGridPainter.cs（非 partial）产生重复定义。
