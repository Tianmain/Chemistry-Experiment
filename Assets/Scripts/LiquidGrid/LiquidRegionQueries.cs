using UnityEngine;

/// <summary>
/// 液体区域查询与灌装子系统：统计某区域内水格/容量格数量，以及运行时向容器注入液体。
/// 只读/写 LiquidGrid，灌装默认色取自协调器 waterColor。
/// </summary>
public class LiquidRegionQueries
{
    private readonly LayerGridPainter m_owner;
    private readonly LiquidGrid m_grid;

    public LiquidRegionQueries(LayerGridPainter owner)
    {
        m_owner = owner;
        m_grid = owner.gridData;
    }

    /// <summary>
    /// 统计指定碰撞体区域内的水格总数
    /// </summary>
    public int GetWaterCountInRegion(Collider2D[] regionColliders)
    {
        if (m_grid.Cells == null || regionColliders == null || regionColliders.Length == 0) return 0;

        int count = 0;
        for (int row = 0; row < m_grid.Rows; row++)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                if (m_grid.Cells[col, row] != CellState.Water) continue;

                m_grid.SetTempToCell(col, row);

                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(m_grid.TempPoint))
                    {
                        count++;
                        break;
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// 统计指定碰撞体区域内的「容量格」总数（非障碍物格），用于把水格数换算成满杯容量。
    /// 与 GetWaterCountInRegion 的区别：这里不要求格子里真有水，只统计几何上能装水的格子数。
    /// </summary>
    public int GetCellCountInRegion(Collider2D[] regionColliders)
    {
        if (m_grid.Cells == null || regionColliders == null || regionColliders.Length == 0) return 0;

        int count = 0;
        for (int row = 0; row < m_grid.Rows; row++)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                if (m_grid.Cells[col, row] == CellState.Obstacle) continue;

                m_grid.SetTempToCell(col, row);

                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(m_grid.TempPoint))
                    {
                        count++;
                        break;
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// 运行时把液体加进某个容器的 LiquidRegion（例如点击烧杯装水）。
    /// 只填充区域内当前为空的格子（不覆盖杯壁/已有液体），水随后由模拟自然沉降。
    /// 颜色优先用 LiquidSource 的颜色；无 LiquidSource 时退回默认水色。
    /// </summary>
    /// <param name="container">容器根物体（应含 LiquidRegion 子物体与 LiquidSource）</param>
    /// <param name="color">液体颜色；为 null 时使用 LiquidSource 的颜色，否则用默认水色</param>
    public void FillContainer(GameObject container, Color? color = null)
    {
        if (container == null || m_grid.Cells == null) return;

        LiquidSource src = container.GetComponentInChildren<LiquidSource>();
        // 空容器（试剂/类型为 "none"）：此处可装液体但当前无液体，运行时也不灌水
        if (src != null && src.IsEmpty()) return;
        Collider2D[] cols = null;
        Color fillColor;
        if (src != null)
        {
            fillColor = color.HasValue ? color.Value : src.GetEffectiveColor();
            cols = (src.regionColliders != null && src.regionColliders.Length > 0)
                ? src.regionColliders
                : src.GetComponentsInChildren<Collider2D>();
        }
        else
        {
            Transform region = container.transform.Find("LiquidRegion");
            if (region == null) return;
            fillColor = color.HasValue ? color.Value : m_owner.waterColor;
            cols = region.GetComponents<Collider2D>();
        }
        if (cols == null || cols.Length == 0) return;

        FillCellsInColliders(cols, fillColor);
    }

    /// <summary>
    /// 把指定碰撞器区域内的「空格子」填为液体（不覆盖杯壁/已有液体）。
    /// 设为 internal 以便 LayerGridPainter 的 FillRegionWithLiquid 跨类调用（同程序集可见）。
    /// </summary>
    internal void FillCellsInColliders(Collider2D[] cols, Color color)
    {
        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.Cells[col, row] != CellState.Empty) continue;
                Vector2 wp = m_grid.GetWorldPosition(col, row);
                foreach (var c in cols)
                {
                    if (c != null && c.OverlapPoint(wp))
                    {
                        m_grid.Cells[col, row] = CellState.Water;
                        m_grid.LiquidColors[col, row] = color;
                        break;
                    }
                }
            }
        }
        m_owner.m_isDirty = true;
    }

    /// <summary>
    /// 同步空容器（"none"）的类型，并处理「倒空后回到 none」：
    ///   - 第一遍（none → 类型）：对仍是 none 的容器，若其区域内已流入水，
    ///     则按这些水格的平均颜色在场景既有的非 none 液体源里找最接近的一种，
    ///     把该容器标记为「持有对应液体类型」（liquidType / 颜色 / 试剂引用一并改写）。
    ///     使 LiquidVolumeUI 的标签从 "none" 变为该液体名（「倒进一部分液体 → 从 none 变成该液体 Type」）。
    ///   - 第二遍（类型 → none）：对已是类型（非 none）的容器，若其区域内已无任何水，
    ///     视为被倒空，恢复为空容器（isEmptyContainer=true、liquidType="none"），
    ///     标签随即回到 "none"（「有液体的器皿如果倒空了，显示 none」）。
    /// 两遍都只依据水格是否存在/颜色判定，无需改动水模拟。
    /// </summary>
    public void SyncEmptyContainerTypes()
    {
        if (m_grid.Cells == null || m_owner.m_cachedLiquidSources == null) return;

        // 候选类型：场景中既有的、非 none 的 LiquidSource（提供类型名 + 颜色）
        var candidates = new System.Collections.Generic.List<LiquidSource>();
        foreach (var s in m_owner.m_cachedLiquidSources)
        {
            if (s != null && !s.IsEmpty()) candidates.Add(s);
        }

        // ===== 第一遍：none 容器进了水 → 自动归类为该液体类型 =====
        if (candidates.Count > 0)
        {
            // 归类门槛：平均色与候选的最近距离平方需小于此值，否则视为「无可匹配液体」，保持 none。
            // 用 RGB 欧氏距离的平方，0.05 ≈ 单通道约 0.22 的差异，足以区分明显不同的液体，又不误拒同色。
            const float kMaxSqDist = 0.05f;
            const int kFillThreshold = 2;   // 需连续 2 个 tick 都有水才首次归类，避免零星溅洒误判

            foreach (var src in m_owner.m_cachedLiquidSources)
            {
                if (src == null || !src.IsEmpty()) continue;
                if (!RegionHasWater(src)) { src.fillTicks = 0; continue; }   // 还没稳定进水，保持 none

                src.fillTicks++;
                if (src.fillTicks < kFillThreshold) continue;   // 防抖：需连续进水若干 tick 才归类

                Color avg = AverageWaterColorInRegion(src);
                LiquidSource best = null;
                float bestD = float.MaxValue;
                foreach (var cand in candidates)
                {
                    Color ec = cand.GetEffectiveColor();
                    float d = (ec.r - avg.r) * (ec.r - avg.r)
                            + (ec.g - avg.g) * (ec.g - avg.g)
                            + (ec.b - avg.b) * (ec.b - avg.b);
                    if (d < bestD) { bestD = d; best = cand; }
                }
                if (best == null || bestD > kMaxSqDist)
                {
                    // 没有足够接近的候选：保持 none（不明确归类到错误的液体）
                    continue;
                }

                // 写入容器类型（与匹配到的源保持一致）
                src.isEmptyContainer = false;
                src.liquidType = best.liquidType;
                src.reagentData = best.reagentData;
                src.liquidColor = best.GetEffectiveColor();
                src.useReagentColor = (best.reagentData != null) ? best.useReagentColor : false;
                src.fillTicks = 0;   // 已归类，重置（此后为非 none，不再进第一遍）
                m_owner.m_isDirty = true;
            }
        }

        // ===== 第二遍：已持有液体类型的容器被倒空（区域内持续无水）→ 恢复为 none =====
        // 防抖：需连续若干 tick 都无水才回退，避免倒出/晃动瞬间的水位波动导致标签在 none 与类型间乱跳。
        const int kEmptyThreshold = 8;   // updateInterval=0.05s → 约 0.4s 持续无水才判为倒空
        foreach (var src in m_owner.m_cachedLiquidSources)
        {
            if (src == null || src.IsEmpty()) continue;
            if (RegionHasWater(src)) { src.emptyTicks = 0; continue; }   // 还有水，保持当前类型

            src.emptyTicks++;
            if (src.emptyTicks < kEmptyThreshold) continue;   // 防抖：仅持续无水才回退 none

            // 倒空：恢复为空容器（none）
            src.isEmptyContainer = true;
            src.liquidType = LiquidSource.EMPTY_MARKER;
            src.useReagentColor = false;
            src.emptyTicks = 0;
            m_owner.m_isDirty = true;
        }
    }

        /// <summary>
        /// 把指定区域内所有水格的颜色向 targetColor 靠拢（t 为插值系数 0~1）。
        /// 用于固体溶解时让液体「变浓/变色」而不增加体积。
        /// </summary>
        public void TintWaterInRegion(Collider2D[] regionColliders, Color targetColor, float t)
        {
            if (m_grid.Cells == null || regionColliders == null || regionColliders.Length == 0) return;
            t = Mathf.Clamp01(t);
            for (int row = 1; row < m_grid.Rows - 1; row++)
            {
                for (int col = 1; col < m_grid.Columns - 1; col++)
                {
                    if (m_grid.Cells[col, row] != CellState.Water) continue;
                    m_grid.SetTempToCell(col, row);
                    foreach (var c in regionColliders)
                    {
                        if (c != null && c.OverlapPoint(m_grid.TempPoint))
                        {
                            m_grid.LiquidColors[col, row] = Color.Lerp(m_grid.LiquidColors[col, row], targetColor, t);
                            break;
                        }
                    }
                }
            }
            m_owner.m_isDirty = true;
        }

        /// <summary>
        /// 该液体源区域内是否还存在任意水格
        /// </summary>
    private bool RegionHasWater(LiquidSource src)
    {
        Collider2D[] cols = (src.regionColliders != null && src.regionColliders.Length > 0)
            ? src.regionColliders
            : src.GetComponentsInChildren<Collider2D>();
        if (cols == null || cols.Length == 0) return false;

        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.Cells[col, row] != CellState.Water) continue;
                m_grid.SetTempToCell(col, row);
                foreach (var c in cols)
                {
                    if (c != null && c.OverlapPoint(m_grid.TempPoint))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 该液体源区域内所有水格的平均颜色
    /// </summary>
    private Color AverageWaterColorInRegion(LiquidSource src)
    {
        Collider2D[] cols = (src.regionColliders != null && src.regionColliders.Length > 0)
            ? src.regionColliders
            : src.GetComponentsInChildren<Collider2D>();
        if (cols == null || cols.Length == 0) return Color.clear;

        float rSum = 0f, gSum = 0f, bSum = 0f, aSum = 0f;
        int n = 0;
        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.Cells[col, row] != CellState.Water) continue;
                m_grid.SetTempToCell(col, row);
                bool inside = false;
                foreach (var c in cols)
                {
                    if (c != null && c.OverlapPoint(m_grid.TempPoint)) { inside = true; break; }
                }
                if (!inside) continue;

                Color cc = m_grid.LiquidColors[col, row];
                rSum += cc.r; gSum += cc.g; bSum += cc.b; aSum += cc.a;
                n++;
            }
        }
        if (n == 0) return Color.clear;
        return new Color(rSum / n, gSum / n, bSum / n, aSum / n);
    }
}
