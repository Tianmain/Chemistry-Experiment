using System;
using UnityEngine;

public partial class LayerGridPainter
{
    /// <summary>
    /// 实时更新网格状态（跟踪物体移动）
    /// </summary>
    private void UpdateGridFromObjects()
    {
        // 更新网格起点位置（跟随脚本所在物体移动）
        m_originX = transform.position.x - gridWidth * 0.5f;
        m_originY = transform.position.y - gridHeight * 0.5f;

        bool changed = false;

        if (m_obstacleLayerMask != 0)
        {
            // 一次物理查询获取网格区域内所有障碍物碰撞器
            Vector2 center = new Vector2(m_originX + gridWidth * 0.5f, m_originY + gridHeight * 0.5f);
            Vector2 boxSize = new Vector2(gridWidth + cellSize, gridHeight + cellSize);
            int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_colliderBuffer, m_obstacleLayerMask);

            for (int col = 0; col < m_columns; col++)
            {
                for (int row = 0; row < m_rows; row++)
                {
                    if (IsBoundaryCell(col, row)) continue;

                    Vector2 worldPos = GetWorldPosition(col, row);
                    bool hasObstacle = false;
                    for (int i = 0; i < count; i++)
                    {
                        var c = m_colliderBuffer[i];
                        if (!c.isTrigger && c.OverlapPoint(worldPos))
                        {
                            hasObstacle = true;
                            break;
                        }
                    }

                    if (hasObstacle && m_grid[col, row] != CellState.Obstacle)
                    {
                        // 新障碍物出现 — 挤出水或气泡
                        if (m_grid[col, row] == CellState.Water || m_grid[col, row] == CellState.Bubble)
                        {
                            if (!TryMoveWaterNearby(col, row))
                                continue; // 找不到空位，跳过，不覆盖水/气泡
                        }
                        m_grid[col, row] = CellState.Obstacle;
                        m_liquidColorGrid[col, row] = Color.clear;
                        changed = true;
                    }
                    else if (!hasObstacle && m_grid[col, row] == CellState.Obstacle)
                    {
                        // 障碍物消失
                        m_grid[col, row] = CellState.Empty;
                        m_liquidColorGrid[col, row] = Color.clear;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
            m_isDirty = true;

        // 吸收碰到 absorbTag 物体的水
        AbsorbWaterAtTaggedObjects();
    }

    /// <summary>
    /// 吸收水：碰到 absorbTag 物体上方的水直接清空
    /// </summary>
    private void AbsorbWaterAtTaggedObjects()
    {
        if (string.IsNullOrEmpty(absorbTag)) return;

        Vector2 center = new Vector2(m_originX + gridWidth * 0.5f, m_originY + gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(gridWidth + cellSize, gridHeight + cellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_colliderBuffer);

        // 收集 absorbTag 的碰撞器到 buffer 前部
        int absorbCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (m_colliderBuffer[i] != null && m_colliderBuffer[i].tag == absorbTag)
            {
                if (i != absorbCount)
                    m_colliderBuffer[absorbCount] = m_colliderBuffer[i];
                absorbCount++;
            }
        }
        if (absorbCount == 0) return;

        // 逐格检测：在 Table 上的格及其正上方有水 → 吸收
        bool absorbed = false;
        for (int col = 1; col < m_columns - 1; col++)
        {
            for (int row = 1; row < m_rows - 1; row++)
            {
                Vector2 worldPos = GetWorldPosition(col, row);
                bool onTable = false;
                for (int i = 0; i < absorbCount; i++)
                {
                    if (m_colliderBuffer[i].OverlapPoint(worldPos))
                    {
                        onTable = true;
                        break;
                    }
                }
                if (!onTable) continue;

                // Table 格本身有水或气泡 → 吸收
                if (m_grid[col, row] == CellState.Water || m_grid[col, row] == CellState.Bubble)
                {
                    m_grid[col, row] = CellState.Empty;
                    m_liquidColorGrid[col, row] = Color.clear;
                    absorbed = true;
                }
                // Table 格正上方有水或气泡 → 吸收
                if (row + 1 < m_rows - 1 && (m_grid[col, row + 1] == CellState.Water || m_grid[col, row + 1] == CellState.Bubble))
                {
                    m_grid[col, row + 1] = CellState.Empty;
                    m_liquidColorGrid[col, row + 1] = Color.clear;
                    absorbed = true;
                }
            }
        }

        if (absorbed)
            m_isDirty = true;
    }

    /// <summary>
    /// 检查是否是边界单元格
    /// </summary>
    private bool IsBoundaryCell(int col, int row)
    {
        return col == 0 || col == m_columns - 1 || row == 0 || row == m_rows - 1;
    }

    /// <summary>
    /// 尝试把水或气泡移到附近的空位置，返回是否成功
    /// </summary>
    private bool TryMoveWaterNearby(int col, int row)
    {
        CellState originalState = m_grid[col, row];
        Color originalColor = m_liquidColorGrid[col, row];

        // 向上
        if (row < m_rows - 1 && m_grid[col, row + 1] == CellState.Empty)
        {
            m_grid[col, row + 1] = originalState;
            m_liquidColorGrid[col, row + 1] = originalColor;
            return true;
        }
        // 向左
        if (col > 0 && m_grid[col - 1, row] == CellState.Empty)
        {
            m_grid[col - 1, row] = originalState;
            m_liquidColorGrid[col - 1, row] = originalColor;
            return true;
        }
        // 向右
        if (col < m_columns - 1 && m_grid[col + 1, row] == CellState.Empty)
        {
            m_grid[col + 1, row] = originalState;
            m_liquidColorGrid[col + 1, row] = originalColor;
            return true;
        }
        // 向上左
        if (row < m_rows - 1 && col > 0 && m_grid[col - 1, row + 1] == CellState.Empty)
        {
            m_grid[col - 1, row + 1] = originalState;
            m_liquidColorGrid[col - 1, row + 1] = originalColor;
            return true;
        }
        // 向上右
        if (row < m_rows - 1 && col < m_columns - 1 && m_grid[col + 1, row + 1] == CellState.Empty)
        {
            m_grid[col + 1, row + 1] = originalState;
            m_liquidColorGrid[col + 1, row + 1] = originalColor;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 根据网格坐标获取世界坐标
    /// </summary>
    private Vector2 GetWorldPosition(int col, int row)
    {
        return new Vector2(
            m_originX + (col + 0.5f) * cellSize,
            m_originY + (row + 0.5f) * cellSize
        );
    }

    /// <summary>
    /// 处理水模拟主逻辑
    /// </summary>
    private void ProcessWaterSimulation()
    {
        if (m_grid == null || m_nextGrid == null) return;

        // 用预分配缓冲区，避免每帧 GC
        Array.Clear(m_moved, 0, m_moved.Length);
        // 复制当前网格到 nextGrid（Array.Copy 比嵌套循环更快）
        Array.Copy(m_grid, m_nextGrid, m_grid.Length);
        // 复制颜色网格
        if (m_liquidColorGrid != null && m_nextLiquidColorGrid != null)
        {
            Array.Copy(m_liquidColorGrid, m_nextLiquidColorGrid, m_liquidColorGrid.Length);
        }

        bool anyMoved = false;

        // --- 气泡上浮模拟（在水流之前处理）---
        // 气泡比水轻，会向上浮，穿过水体，到达水面后消失
        // 从顶部向下遍历，确保每步每个气泡最多移动一次
        if (enableBubbles)
        {
            // 根据气泡速度因子决定每步上浮几格（至少1格）
            int bubbleSteps = Mathf.Max(1, Mathf.RoundToInt(bubbleRiseSpeed));

            for (int step = 0; step < bubbleSteps; step++)
            {
                bool stepMoved = false;
                for (int row = m_rows - 2; row >= 1; row--)
                {
                    for (int col = 1; col < m_columns - 1; col++)
                    {
                        if (m_nextGrid[col, row] != CellState.Bubble) continue;

                        bool bubbleMoved = false;

                        // 1. 优先正上方：如果是水，气泡和水交换位置（气泡上浮，水下沉）
                        if (row < m_rows - 2 && !IsBoundaryCell(col, row + 1))
                        {
                            CellState above = m_nextGrid[col, row + 1];
                            if (above == CellState.Water)
                            {
                                // 气泡上浮：气泡到上方，水到下方
                                m_nextGrid[col, row + 1] = CellState.Bubble;
                                m_nextGrid[col, row] = CellState.Water;
                                // 水的颜色也跟随下沉
                                m_nextLiquidColorGrid[col, row] = m_nextLiquidColorGrid[col, row + 1];
                                m_nextLiquidColorGrid[col, row + 1] = Color.clear;
                                bubbleMoved = true;
                                stepMoved = true;
                                anyMoved = true;
                            }
                            else if (above == CellState.Empty)
                            {
                                // 正上方是空（到达水面或水面以上）→ 气泡消失
                                m_nextGrid[col, row] = CellState.Empty;
                                m_nextLiquidColorGrid[col, row] = Color.clear;
                                bubbleMoved = true;
                                stepMoved = true;
                                anyMoved = true;
                            }
                        }

                        if (bubbleMoved) continue;

                        // 2. 斜上方：左上方或右上方是水
                        bool canLeftUp = row < m_rows - 2 && col > 1
                            && m_nextGrid[col - 1, row + 1] == CellState.Water
                            && !IsBoundaryCell(col - 1, row + 1);
                        bool canRightUp = row < m_rows - 2 && col < m_columns - 2
                            && m_nextGrid[col + 1, row + 1] == CellState.Water
                            && !IsBoundaryCell(col + 1, row + 1);

                        if (canLeftUp && canRightUp)
                        {
                            bool goLeft = UnityEngine.Random.value < 0.5f;
                            int targetCol = goLeft ? col - 1 : col + 1;
                            m_nextGrid[targetCol, row + 1] = CellState.Bubble;
                            m_nextGrid[col, row] = CellState.Water;
                            m_nextLiquidColorGrid[col, row] = m_nextLiquidColorGrid[targetCol, row + 1];
                            m_nextLiquidColorGrid[targetCol, row + 1] = Color.clear;
                            stepMoved = true;
                            anyMoved = true;
                        }
                        else if (canLeftUp)
                        {
                            m_nextGrid[col - 1, row + 1] = CellState.Bubble;
                            m_nextGrid[col, row] = CellState.Water;
                            m_nextLiquidColorGrid[col, row] = m_nextLiquidColorGrid[col - 1, row + 1];
                            m_nextLiquidColorGrid[col - 1, row + 1] = Color.clear;
                            stepMoved = true;
                            anyMoved = true;
                        }
                        else if (canRightUp)
                        {
                            m_nextGrid[col + 1, row + 1] = CellState.Bubble;
                            m_nextGrid[col, row] = CellState.Water;
                            m_nextLiquidColorGrid[col, row] = m_nextLiquidColorGrid[col + 1, row + 1];
                            m_nextLiquidColorGrid[col + 1, row + 1] = Color.clear;
                            stepMoved = true;
                            anyMoved = true;
                        }
                        else
                        {
                            // 3. 水平方向：左右是水（气泡被上方物体挡住时横向漂移）
                            bool canLeft = col > 1 && m_nextGrid[col - 1, row] == CellState.Water
                                && !IsBoundaryCell(col - 1, row);
                            bool canRight = col < m_columns - 2 && m_nextGrid[col + 1, row] == CellState.Water
                                && !IsBoundaryCell(col + 1, row);

                            if (canLeft && canRight)
                            {
                                bool goLeft = UnityEngine.Random.value < 0.5f;
                                int targetCol = goLeft ? col - 1 : col + 1;
                                m_nextGrid[targetCol, row] = CellState.Bubble;
                                m_nextGrid[col, row] = CellState.Water;
                                m_nextLiquidColorGrid[col, row] = m_nextLiquidColorGrid[targetCol, row];
                                m_nextLiquidColorGrid[targetCol, row] = Color.clear;
                                stepMoved = true;
                                anyMoved = true;
                            }
                            else if (canLeft)
                            {
                                m_nextGrid[col - 1, row] = CellState.Bubble;
                                m_nextGrid[col, row] = CellState.Water;
                                m_nextLiquidColorGrid[col, row] = m_nextLiquidColorGrid[col - 1, row];
                                m_nextLiquidColorGrid[col - 1, row] = Color.clear;
                                stepMoved = true;
                                anyMoved = true;
                            }
                            else if (canRight)
                            {
                                m_nextGrid[col + 1, row] = CellState.Bubble;
                                m_nextGrid[col, row] = CellState.Water;
                                m_nextLiquidColorGrid[col, row] = m_nextLiquidColorGrid[col + 1, row];
                                m_nextLiquidColorGrid[col + 1, row] = Color.clear;
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

        for (int row = 0; row < m_rows; row++)
        {
            for (int col = 0; col < m_columns; col++)
            {
                if (m_grid[col, row] != CellState.Water) continue;
                if (m_moved[col, row]) continue;

                bool hasMoved = false;

                if (row > 0 && m_nextGrid[col, row - 1] == CellState.Empty && !IsBoundaryCell(col, row - 1))
                {
                    m_nextGrid[col, row] = CellState.Empty;
                    m_nextGrid[col, row - 1] = CellState.Water;
                    m_nextLiquidColorGrid[col, row - 1] = m_liquidColorGrid[col, row];
                    m_moved[col, row - 1] = true;
                    hasMoved = true;
                    anyMoved = true;
                }
                else if (row > 0)
                {
                    bool canLeft = col > 0 && m_nextGrid[col - 1, row - 1] == CellState.Empty && !IsBoundaryCell(col - 1, row - 1);
                    bool canRight = col < m_columns - 1 && m_nextGrid[col + 1, row - 1] == CellState.Empty && !IsBoundaryCell(col + 1, row - 1);

                    if (canLeft && canRight)
                    {
                        bool goLeft = UnityEngine.Random.value < 0.5f;
                        int targetCol = goLeft ? col - 1 : col + 1;
                        m_nextGrid[col, row] = CellState.Empty;
                        m_nextGrid[targetCol, row - 1] = CellState.Water;
                        m_nextLiquidColorGrid[targetCol, row - 1] = m_liquidColorGrid[col, row];
                        m_moved[targetCol, row - 1] = true;
                        hasMoved = true;
                        anyMoved = true;
                    }
                    else if (canLeft)
                    {
                        m_nextGrid[col, row] = CellState.Empty;
                        m_nextGrid[col - 1, row - 1] = CellState.Water;
                        m_nextLiquidColorGrid[col - 1, row - 1] = m_liquidColorGrid[col, row];
                        m_moved[col - 1, row - 1] = true;
                        hasMoved = true;
                        anyMoved = true;
                    }
                    else if (canRight)
                    {
                        m_nextGrid[col, row] = CellState.Empty;
                        m_nextGrid[col + 1, row - 1] = CellState.Water;
                        m_nextLiquidColorGrid[col + 1, row - 1] = m_liquidColorGrid[col, row];
                        m_moved[col + 1, row - 1] = true;
                        hasMoved = true;
                        anyMoved = true;
                    }
                }

                if (!hasMoved)
                {
                    bool canLeft = col > 0 && m_nextGrid[col - 1, row] == CellState.Empty && !IsBoundaryCell(col - 1, row);
                    bool canRight = col < m_columns - 1 && m_nextGrid[col + 1, row] == CellState.Empty && !IsBoundaryCell(col + 1, row);

                    if (canLeft && canRight)
                    {
                        // 优先流向可以继续下落的方向
                        bool leftCanFall = row > 0 && m_nextGrid[col - 1, row - 1] == CellState.Empty && !IsBoundaryCell(col - 1, row - 1);
                        bool rightCanFall = row > 0 && m_nextGrid[col + 1, row - 1] == CellState.Empty && !IsBoundaryCell(col + 1, row - 1);

                        bool goLeft;
                        if (leftCanFall != rightCanFall)
                            goLeft = leftCanFall;
                        else
                            goLeft = UnityEngine.Random.value < 0.5f;

                        int targetCol = goLeft ? col - 1 : col + 1;
                        m_nextGrid[col, row] = CellState.Empty;
                        m_nextGrid[targetCol, row] = CellState.Water;
                        m_nextLiquidColorGrid[targetCol, row] = m_liquidColorGrid[col, row];
                        m_moved[targetCol, row] = true;
                        anyMoved = true;
                    }
                    else if (canLeft)
                    {
                        m_nextGrid[col, row] = CellState.Empty;
                        m_nextGrid[col - 1, row] = CellState.Water;
                        m_nextLiquidColorGrid[col - 1, row] = m_liquidColorGrid[col, row];
                        m_moved[col - 1, row] = true;
                        anyMoved = true;
                    }
                    else if (canRight)
                    {
                        m_nextGrid[col, row] = CellState.Empty;
                        m_nextGrid[col + 1, row] = CellState.Water;
                        m_nextLiquidColorGrid[col + 1, row] = m_liquidColorGrid[col, row];
                        m_moved[col + 1, row] = true;
                        anyMoved = true;
                    }
                }
            }
        }

        // --- 间隙填充 pass ---
        // 水cell一侧有空格、另一侧有水时，向空格方向滑动以紧挨
        // 不检查 m_moved：允许主循环移动过的水继续滑动填满间隙
        // Pass 1: 左→右（向右填间隙）
        for (int row = 1; row < m_rows - 1; row++)
        {
            for (int col = 1; col < m_columns - 2; col++)
            {
                if (m_nextGrid[col, row] != CellState.Water) continue;
                if (m_nextGrid[col + 1, row] != CellState.Empty || IsBoundaryCell(col + 1, row)) continue;
                // 目标格下方必须有支撑（否则应该往下掉，不是水平填）
                if (m_nextGrid[col + 1, row - 1] == CellState.Empty && !IsBoundaryCell(col + 1, row - 1)) continue;
                // 右侧远处必须有水（这是间隙，不是边缘扩散）
                bool hasWaterRight = false;
                for (int c = col + 2; c < m_columns - 1; c++)
                {
                    if (m_nextGrid[c, row] == CellState.Water) { hasWaterRight = true; break; }
                }
                if (!hasWaterRight) continue;

                m_nextGrid[col, row] = CellState.Empty;
                m_nextGrid[col + 1, row] = CellState.Water;
                m_nextLiquidColorGrid[col + 1, row] = m_nextLiquidColorGrid[col, row];
                anyMoved = true;
            }
        }
        // Pass 2: 右→左（向左填间隙）
        for (int row = 1; row < m_rows - 1; row++)
        {
            for (int col = m_columns - 2; col > 1; col--)
            {
                if (m_nextGrid[col, row] != CellState.Water) continue;
                if (m_nextGrid[col - 1, row] != CellState.Empty || IsBoundaryCell(col - 1, row)) continue;
                if (m_nextGrid[col - 1, row - 1] == CellState.Empty && !IsBoundaryCell(col - 1, row - 1)) continue;
                bool hasWaterLeft = false;
                for (int c = col - 2; c >= 1; c--)
                {
                    if (m_nextGrid[c, row] == CellState.Water) { hasWaterLeft = true; break; }
                }
                if (!hasWaterLeft) continue;

                m_nextGrid[col, row] = CellState.Empty;
                m_nextGrid[col - 1, row] = CellState.Water;
                m_nextLiquidColorGrid[col - 1, row] = m_nextLiquidColorGrid[col, row];
                anyMoved = true;
            }
        }

        // --- 液体混合扩散 pass ---
        // 相邻不同颜色的水格子逐渐混合颜色（扩散）
        if (mixingDiffusionRate > 0f && mixingColorThreshold >= 0f)
            DiffuseColors();

        // 只有水实际移动了才交换和标记 dirty
        if (anyMoved || m_colorDiffused)
        {
            var temp = m_grid;
            m_grid = m_nextGrid;
            m_nextGrid = temp;

            var tempColor = m_liquidColorGrid;
            m_liquidColorGrid = m_nextLiquidColorGrid;
            m_nextLiquidColorGrid = tempColor;

            m_isDirty = true;
        }
        m_colorDiffused = false;
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

        for (int row = 1; row < m_rows - 1; row++)
        {
            for (int col = 1; col < m_columns - 1; col++)
            {
                if (m_nextGrid[col, row] != CellState.Water) continue;

                Color srcColor = m_nextLiquidColorGrid[col, row];
                float rSum = 0f, gSum = 0f, bSum = 0f, aSum = 0f;
                int neighborCount = 0;

                for (int d = 0; d < 4; d++)
                {
                    int nc = col + dcol[d];
                    int nr = row + drow[d];
                    if (nc <= 0 || nc >= m_columns - 1 || nr <= 0 || nr >= m_rows - 1) continue;
                    if (m_nextGrid[nc, nr] != CellState.Water) continue;

                    Color neighborColor = m_nextLiquidColorGrid[nc, nr];
                    // 颜色差异检测
                    float diff = Mathf.Abs(srcColor.r - neighborColor.r)
                               + Mathf.Abs(srcColor.g - neighborColor.g)
                               + Mathf.Abs(srcColor.b - neighborColor.b);
                    if (diff > mixingColorThreshold)
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

                    m_nextLiquidColorGrid[col, row].r = Mathf.Lerp(srcColor.r, avgR, mixingDiffusionRate);
                    m_nextLiquidColorGrid[col, row].g = Mathf.Lerp(srcColor.g, avgG, mixingDiffusionRate);
                    m_nextLiquidColorGrid[col, row].b = Mathf.Lerp(srcColor.b, avgB, mixingDiffusionRate);
                    m_nextLiquidColorGrid[col, row].a = Mathf.Lerp(srcColor.a, avgA, mixingDiffusionRate);
                    m_colorDiffused = true;
                }
            }
        }
    }

    // 标记本次模拟是否有颜色扩散变化
    private bool m_colorDiffused;
}
