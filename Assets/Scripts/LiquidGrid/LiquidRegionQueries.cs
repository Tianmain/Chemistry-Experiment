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
    /// 把指定碰撞器区域内的「空格子」填为液体（不覆盖杯壁/已有液体）
    /// </summary>
    private void FillCellsInColliders(Collider2D[] cols, Color color)
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
}
