using UnityEngine;

/// <summary>
/// 水模拟子系统：每 tick 推进网格中的水/气泡流动、间隙填充与颜色扩散混合。
/// 直接读写 LiquidGrid 的双缓冲（Cells / NextCells 及对应颜色），通过缓冲交换提交结果。
/// 模拟选项（enableBubbles / bubbleRiseSpeed / mixingDiffusionRate / mixingColorThreshold）从协调器读取。
/// </summary>
public class WaterSimulator
{
    private readonly LayerGridPainter m_owner;
    private readonly LiquidGrid m_grid;

    public WaterSimulator(LayerGridPainter owner)
    {
        m_owner = owner;
        m_grid = owner.gridData;
    }

    /// <summary>
    /// 判断该水格是否处于「被锁定」状态（当前被拖拽容器覆盖）。
    /// 拖拽期间水模拟跳过这些格子，使杯内液体随容器刚性移动、不参与流动/上浮/混合，
    /// 而场景其余部分的水照常模拟（满足「只暂停本容器物理、不暂停所有物理」的需求）。
    /// </summary>
    private bool IsCellLocked(int col, int row)
    {
        var coverage = m_owner.m_draggedCoverage;
        if (coverage == null) return false;
        int idx = col + row * m_grid.Columns;
        return idx >= 0 && idx < coverage.Length && coverage[idx];
    }

    /// <summary>
    /// 气泡上浮时的交换目标是否可达：目标格必须是水且未被锁定
    /// （锁定水属于被拖拽容器，不能被气泡上浮带动，否则会“解冻”杯内液体）。
    /// </summary>
    private bool CanBubbleSwapInto(int col, int row)
    {
        return col >= 0 && col < m_grid.Columns
            && row >= 0 && row < m_grid.Rows
            && m_grid.NextCells[col, row] == CellState.Water
            && !IsCellLocked(col, row);
    }

    /// <summary>
    /// 处理水模拟主逻辑
    /// </summary>
    public void ProcessWaterSimulation()
    {
        if (m_grid.Cells == null || m_grid.NextCells == null) return;

        // 用预分配缓冲区，避免每帧 GC
        System.Array.Clear(m_grid.Moved, 0, m_grid.Moved.Length);
        // 复制当前网格到 nextGrid（Array.Copy 比嵌套循环更快）
        m_grid.CopyCurrentToNext();

        bool anyMoved = false;

        // --- 气泡上浮模拟（在水流之前处理）---
        // 气泡比水轻，会向上浮，穿过水体，到达水面后消失
        // 从顶部向下遍历，确保每步每个气泡最多移动一次
        if (m_owner.enableBubbles)
        {
            // 根据气泡速度因子决定每步上浮几格（至少1格）
            int bubbleSteps = Mathf.Max(1, Mathf.RoundToInt(m_owner.bubbleRiseSpeed));

            for (int step = 0; step < bubbleSteps; step++)
            {
                bool stepMoved = false;
                for (int row = m_grid.Rows - 2; row >= 1; row--)
                {
                    for (int col = 1; col < m_grid.Columns - 1; col++)
                    {
                        if (m_grid.NextCells[col, row] != CellState.Bubble) continue;
                    if (IsCellLocked(col, row)) continue; // 杯内气泡锁定：不浮动、不上浮

                        bool bubbleMoved = false;

                        // 1. 优先正上方：如果是水，气泡和水交换位置（气泡上浮，水下沉）
                        if (row < m_grid.Rows - 2 && !m_grid.IsBoundaryCell(col, row + 1))
                        {
                            CellState above = m_grid.NextCells[col, row + 1];
                            if (CanBubbleSwapInto(col, row + 1))
                            {
                                // 气泡上浮：气泡到上方，水到下方
                                m_grid.NextCells[col, row + 1] = CellState.Bubble;
                                m_grid.NextCells[col, row] = CellState.Water;
                                // 水的颜色也跟随下沉
                                m_grid.NextLiquidColors[col, row] = m_grid.NextLiquidColors[col, row + 1];
                                m_grid.NextLiquidColors[col, row + 1] = Color.clear;
                                bubbleMoved = true;
                                stepMoved = true;
                                anyMoved = true;
                            }
                            else if (above == CellState.Empty)
                            {
                                // 正上方是空（到达水面或水面以上）→ 气泡消失
                                m_grid.NextCells[col, row] = CellState.Empty;
                                m_grid.NextLiquidColors[col, row] = Color.clear;
                                bubbleMoved = true;
                                stepMoved = true;
                                anyMoved = true;
                            }
                        }

                        if (bubbleMoved) continue;

                        // 2. 斜上方：左上方或右上方是水
                        bool canLeftUp = row < m_grid.Rows - 2 && col > 1
                            && CanBubbleSwapInto(col - 1, row + 1);
                        bool canRightUp = row < m_grid.Rows - 2 && col < m_grid.Columns - 2
                            && CanBubbleSwapInto(col + 1, row + 1);

                        if (canLeftUp && canRightUp)
                        {
                            bool goLeft = UnityEngine.Random.value < 0.5f;
                            int targetCol = goLeft ? col - 1 : col + 1;
                            m_grid.NextCells[targetCol, row + 1] = CellState.Bubble;
                            m_grid.NextCells[col, row] = CellState.Water;
                            m_grid.NextLiquidColors[col, row] = m_grid.NextLiquidColors[targetCol, row + 1];
                            m_grid.NextLiquidColors[targetCol, row + 1] = Color.clear;
                            stepMoved = true;
                            anyMoved = true;
                        }
                        else if (canLeftUp)
                        {
                            m_grid.NextCells[col - 1, row + 1] = CellState.Bubble;
                            m_grid.NextCells[col, row] = CellState.Water;
                            m_grid.NextLiquidColors[col, row] = m_grid.NextLiquidColors[col - 1, row + 1];
                            m_grid.NextLiquidColors[col - 1, row + 1] = Color.clear;
                            stepMoved = true;
                            anyMoved = true;
                        }
                        else if (canRightUp)
                        {
                            m_grid.NextCells[col + 1, row + 1] = CellState.Bubble;
                            m_grid.NextCells[col, row] = CellState.Water;
                            m_grid.NextLiquidColors[col, row] = m_grid.NextLiquidColors[col + 1, row + 1];
                            m_grid.NextLiquidColors[col + 1, row + 1] = Color.clear;
                            stepMoved = true;
                            anyMoved = true;
                        }
                        else
                        {
                            // 3. 水平方向：左右是水（气泡被上方物体挡住时横向漂移）
                            bool canLeft = col > 1 && CanBubbleSwapInto(col - 1, row);
                            bool canRight = col < m_grid.Columns - 2 && CanBubbleSwapInto(col + 1, row);

                            if (canLeft && canRight)
                            {
                                bool goLeft = UnityEngine.Random.value < 0.5f;
                                int targetCol = goLeft ? col - 1 : col + 1;
                                m_grid.NextCells[targetCol, row] = CellState.Bubble;
                                m_grid.NextCells[col, row] = CellState.Water;
                                m_grid.NextLiquidColors[col, row] = m_grid.NextLiquidColors[targetCol, row];
                                m_grid.NextLiquidColors[targetCol, row] = Color.clear;
                                stepMoved = true;
                                anyMoved = true;
                            }
                            else if (canLeft)
                            {
                                m_grid.NextCells[col - 1, row] = CellState.Bubble;
                                m_grid.NextCells[col, row] = CellState.Water;
                                m_grid.NextLiquidColors[col, row] = m_grid.NextLiquidColors[col - 1, row];
                                m_grid.NextLiquidColors[col - 1, row] = Color.clear;
                                stepMoved = true;
                                anyMoved = true;
                            }
                            else if (canRight)
                            {
                                m_grid.NextCells[col + 1, row] = CellState.Bubble;
                                m_grid.NextCells[col, row] = CellState.Water;
                                m_grid.NextLiquidColors[col, row] = m_grid.NextLiquidColors[col + 1, row];
                                m_grid.NextLiquidColors[col + 1, row] = Color.clear;
                                stepMoved = true;
                                anyMoved = true;
                            }
                        }
                    }
                }
                // 如果这一步没有任何气泡移动，提前退出（后面的步也不会有变化）
                if (!stepMoved) break;
            }
        }

        for (int row = 0; row < m_grid.Rows; row++)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                if (m_grid.Cells[col, row] != CellState.Water) continue;
                if (IsCellLocked(col, row)) continue; // 被拖拽容器内的水：冻结，不流动
                if (m_grid.Moved[col, row]) continue;

                bool hasMoved = false;

                if (row > 0 && m_grid.NextCells[col, row - 1] == CellState.Empty && !m_grid.IsBoundaryCell(col, row - 1))
                {
                    m_grid.NextCells[col, row] = CellState.Empty;
                    m_grid.NextCells[col, row - 1] = CellState.Water;
                    m_grid.NextLiquidColors[col, row - 1] = m_grid.LiquidColors[col, row];
                    m_grid.Moved[col, row - 1] = true;
                    hasMoved = true;
                    anyMoved = true;
                }
                else if (row > 0)
                {
                    bool canLeft = col > 0 && m_grid.NextCells[col - 1, row - 1] == CellState.Empty && !m_grid.IsBoundaryCell(col - 1, row - 1);
                    bool canRight = col < m_grid.Columns - 1 && m_grid.NextCells[col + 1, row - 1] == CellState.Empty && !m_grid.IsBoundaryCell(col + 1, row - 1);

                    if (canLeft && canRight)
                    {
                        bool goLeft = UnityEngine.Random.value < 0.5f;
                        int targetCol = goLeft ? col - 1 : col + 1;
                        m_grid.NextCells[col, row] = CellState.Empty;
                        m_grid.NextCells[targetCol, row - 1] = CellState.Water;
                        m_grid.NextLiquidColors[targetCol, row - 1] = m_grid.LiquidColors[col, row];
                        m_grid.Moved[targetCol, row - 1] = true;
                        hasMoved = true;
                        anyMoved = true;
                    }
                    else if (canLeft)
                    {
                        m_grid.NextCells[col, row] = CellState.Empty;
                        m_grid.NextCells[col - 1, row - 1] = CellState.Water;
                        m_grid.NextLiquidColors[col - 1, row - 1] = m_grid.LiquidColors[col, row];
                        m_grid.Moved[col - 1, row - 1] = true;
                        hasMoved = true;
                        anyMoved = true;
                    }
                    else if (canRight)
                    {
                        m_grid.NextCells[col, row] = CellState.Empty;
                        m_grid.NextCells[col + 1, row - 1] = CellState.Water;
                        m_grid.NextLiquidColors[col + 1, row - 1] = m_grid.LiquidColors[col, row];
                        m_grid.Moved[col + 1, row - 1] = true;
                        hasMoved = true;
                        anyMoved = true;
                    }
                }

                if (!hasMoved)
                {
                    bool canLeft = col > 0 && m_grid.NextCells[col - 1, row] == CellState.Empty && !m_grid.IsBoundaryCell(col - 1, row);
                    bool canRight = col < m_grid.Columns - 1 && m_grid.NextCells[col + 1, row] == CellState.Empty && !m_grid.IsBoundaryCell(col + 1, row);

                    if (canLeft && canRight)
                    {
                        // 优先流向可以继续下落的方向
                        bool leftCanFall = row > 0 && m_grid.NextCells[col - 1, row - 1] == CellState.Empty && !m_grid.IsBoundaryCell(col - 1, row - 1);
                        bool rightCanFall = row > 0 && m_grid.NextCells[col + 1, row - 1] == CellState.Empty && !m_grid.IsBoundaryCell(col + 1, row - 1);

                        bool goLeft;
                        if (leftCanFall != rightCanFall)
                            goLeft = leftCanFall;
                        else
                            goLeft = UnityEngine.Random.value < 0.5f;

                        int targetCol = goLeft ? col - 1 : col + 1;
                        m_grid.NextCells[col, row] = CellState.Empty;
                        m_grid.NextCells[targetCol, row] = CellState.Water;
                        m_grid.NextLiquidColors[targetCol, row] = m_grid.LiquidColors[col, row];
                        m_grid.Moved[targetCol, row] = true;
                        anyMoved = true;
                    }
                    else if (canLeft)
                    {
                        m_grid.NextCells[col, row] = CellState.Empty;
                        m_grid.NextCells[col - 1, row] = CellState.Water;
                        m_grid.NextLiquidColors[col - 1, row] = m_grid.LiquidColors[col, row];
                        m_grid.Moved[col - 1, row] = true;
                        anyMoved = true;
                    }
                    else if (canRight)
                    {
                        m_grid.NextCells[col, row] = CellState.Empty;
                        m_grid.NextCells[col + 1, row] = CellState.Water;
                        m_grid.NextLiquidColors[col + 1, row] = m_grid.LiquidColors[col, row];
                        m_grid.Moved[col + 1, row] = true;
                        anyMoved = true;
                    }
                }
            }
        }

        // --- 间隙填充 pass ---
        // 水cell一侧有空格、另一侧有水时，向空格方向滑动以紧挨
        // 不检查 m_moved：允许主循环移动过的水继续滑动填满间隙
        // 用每行最左/最右水格列（O(1) 预计算）替代原先每行 O(width) 的远端扫描，整体从 O(n^2) 降到 O(cells)。
        ComputeWaterExtents();
        // Pass 1: 左→右（向右填间隙）
        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 2; col++)
            {
                if (m_grid.NextCells[col, row] != CellState.Water) continue;
                if (IsCellLocked(col, row)) continue; // 锁定水不参与间隙填充
                if (m_grid.NextCells[col + 1, row] != CellState.Empty || m_grid.IsBoundaryCell(col + 1, row)) continue;
                // 目标格下方必须有支撑（否则应该往下掉，不是水平填）
                if (m_grid.NextCells[col + 1, row - 1] == CellState.Empty && !m_grid.IsBoundaryCell(col + 1, row - 1)) continue;
                // 右侧远处必须有水（这是间隙，不是边缘扩散）
                if (m_grid.RowRightmostWater[row] <= col + 1) continue;

                m_grid.NextCells[col, row] = CellState.Empty;
                m_grid.NextCells[col + 1, row] = CellState.Water;
                m_grid.NextLiquidColors[col + 1, row] = m_grid.NextLiquidColors[col, row];
                anyMoved = true;
            }
        }
        // Pass 2: 右→左（向左填间隙）
        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = m_grid.Columns - 2; col > 1; col--)
            {
                if (m_grid.NextCells[col, row] != CellState.Water) continue;
                if (IsCellLocked(col, row)) continue; // 锁定水不参与间隙填充
                if (m_grid.NextCells[col - 1, row] != CellState.Empty || m_grid.IsBoundaryCell(col - 1, row)) continue;
                if (m_grid.NextCells[col - 1, row - 1] == CellState.Empty && !m_grid.IsBoundaryCell(col - 1, row - 1)) continue;
                // 左侧远处必须有水（这是间隙，不是边缘扩散）
                if (m_grid.RowLeftmostWater[row] < 0 || m_grid.RowLeftmostWater[row] >= col - 1) continue;

                m_grid.NextCells[col, row] = CellState.Empty;
                m_grid.NextCells[col - 1, row] = CellState.Water;
                m_grid.NextLiquidColors[col - 1, row] = m_grid.NextLiquidColors[col, row];
                anyMoved = true;
            }
        }

        // --- 液体混合扩散 pass ---
        // 相邻不同颜色的水格子逐渐混合颜色（扩散）
        if (m_owner.mixingDiffusionRate > 0f && m_owner.mixingColorThreshold >= 0f)
            DiffuseColors();

        // 只有水实际移动了才交换和标记 dirty
        if (anyMoved || m_grid.ColorDiffused)
        {
            m_grid.SwapSimulationBuffers();
            m_owner.m_isDirty = true;
        }
        m_grid.ColorDiffused = false;
    }

    /// <summary>
    /// 液体颜色扩散混合：相邻水格子颜色逐渐趋同
    /// 仅对颜色差异超过阈值的相邻水格子执行插值混合
    /// </summary>
    private void DiffuseColors()
    {
        // 预算方向偏移（右、左、上、下）
        int[] dcol = { 1, -1, 0, 0 };
        int[] drow = { 0, 0, 1, -1 };

        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.NextCells[col, row] != CellState.Water) continue;
                if (IsCellLocked(col, row)) continue; // 杯内水颜色冻结，不参与混合

                Color srcColor = m_grid.NextLiquidColors[col, row];
                float rSum = 0f, gSum = 0f, bSum = 0f, aSum = 0f;
                int neighborCount = 0;

                for (int d = 0; d < 4; d++)
                {
                    int nc = col + dcol[d];
                    int nr = row + drow[d];
                    if (nc <= 0 || nc >= m_grid.Columns - 1 || nr <= 0 || nr >= m_grid.Rows - 1) continue;
                    if (m_grid.NextCells[nc, nr] != CellState.Water) continue;
                    if (IsCellLocked(nc, nr)) continue; // 锁定邻居不参与混合，保持其颜色冻结

                    Color neighborColor = m_grid.NextLiquidColors[nc, nr];
                    // 颜色差异检测
                    float diff = Mathf.Abs(srcColor.r - neighborColor.r)
                               + Mathf.Abs(srcColor.g - neighborColor.g)
                               + Mathf.Abs(srcColor.b - neighborColor.b);
                    if (diff > m_owner.mixingColorThreshold)
                    {
                        rSum += neighborColor.r;
                        gSum += neighborColor.g;
                        bSum += neighborColor.b;
                        aSum += neighborColor.a;
                        neighborCount++;
                    }
                }

                if (neighborCount > 0)
                {
                    float avgR = rSum / neighborCount;
                    float avgG = gSum / neighborCount;
                    float avgB = bSum / neighborCount;
                    float avgA = aSum / neighborCount;

                    m_grid.NextLiquidColors[col, row].r = Mathf.Lerp(srcColor.r, avgR, m_owner.mixingDiffusionRate);
                    m_grid.NextLiquidColors[col, row].g = Mathf.Lerp(srcColor.g, avgG, m_owner.mixingDiffusionRate);
                    m_grid.NextLiquidColors[col, row].b = Mathf.Lerp(srcColor.b, avgB, m_owner.mixingDiffusionRate);
                    m_grid.NextLiquidColors[col, row].a = Mathf.Lerp(srcColor.a, avgA, m_owner.mixingDiffusionRate);
                    m_grid.ColorDiffused = true;
                }
            }
        }
    }

    /// <summary>
    /// 预计算每行最左/最右的水格列索引（基于本步开始时的 m_nextGrid 快照），
    /// 供间隙填充 pass 以 O(1) 判断「远端是否还有水」，避免逐格 O(width) 扫描。
    /// </summary>
    private void ComputeWaterExtents()
    {
        if (m_grid.RowRightmostWater == null || m_grid.RowRightmostWater.Length != m_grid.Rows)
            m_grid.RowRightmostWater = new int[m_grid.Rows];
        if (m_grid.RowLeftmostWater == null || m_grid.RowLeftmostWater.Length != m_grid.Rows)
            m_grid.RowLeftmostWater = new int[m_grid.Rows];

        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            m_grid.RowRightmostWater[row] = -1;
            m_grid.RowLeftmostWater[row] = -1;
        }

        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.NextCells[col, row] != CellState.Water) continue;
                if (m_grid.RowRightmostWater[row] < col) m_grid.RowRightmostWater[row] = col;
                if (m_grid.RowLeftmostWater[row] < 0 || m_grid.RowLeftmostWater[row] > col) m_grid.RowLeftmostWater[row] = col;
            }
        }
    }
}
