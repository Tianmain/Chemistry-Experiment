using UnityEngine;

/// <summary>
/// 气泡子系统：模拟蒸发——从容器底部均匀移除一格水，并在附近生成气泡上浮（沸腾效果）。
/// 持有气泡候选位置与生成缓冲区，直接读写 LiquidGrid。
/// 选项（enableBubbles / bubbleSpawnChance）从协调器读取。
/// </summary>
public class BubbleSystem
{
    private readonly LayerGridPainter m_owner;
    private readonly LiquidGrid m_grid;

    // 预分配的随机水格索引缓冲区（避免每帧 GC；默认 1024，远超单区域水格数，无需动态扩容）
    private int[] m_randomWaterBuffer = new int[1024];

    // 预分配的气泡候选位置数组（同一行左右两侧优先，确保气泡从最底层开始上浮）
    // 分组 0：同一行（左、右）—— 最优先，气泡从最底层开始上浮
    // 分组 1：斜上方（左上、右上）—— 次优先
    // 分组 2：正上方 —— 最后考虑
    private int[,] m_bubbleCandidateOffsets = new int[,]
    {
        { -1, 0 }, { 1, 0 },     // 分组 0：同一行左右
        { -1, 1 }, { 1, 1 },     // 分组 1：斜上方
        { 0, 1 }                   // 分组 2：正上方
    };
    private int[] m_bubbleShuffleIndices = new int[5] { 0, 1, 2, 3, 4 };

    // 预分配的气泡生成位置缓冲区（默认 1024，无需动态扩容）
    private int[] m_bubbleSpawnColBuffer = new int[1024];
    private int[] m_bubbleSpawnRowBuffer = new int[1024];

    public BubbleSystem(LayerGridPainter owner)
    {
        m_owner = owner;
        m_grid = owner.gridData;
    }

    /// <summary>
    /// 从指定碰撞体区域内移除一格水（从最底部水格中随机选择一个移除，模拟底部均匀蒸发）
    /// 蒸发后会在原处或附近随机生成气泡，气泡会上浮穿过水体，模拟沸腾效果
    /// </summary>
    /// <param name="spawnBubbles">是否在蒸发时生成气泡。沸腾蒸发传 true；常温缓慢蒸发传 false（不冒泡）</param>
    /// <returns>是否成功移除了一格水</returns>
    public bool RemoveWaterFromRegion(Collider2D[] regionColliders, bool spawnBubbles = true)
    {
        if (m_grid.Cells == null || regionColliders == null || regionColliders.Length == 0) return false;

        // Step 1: 先找到网格中真正有水的最底行（不依赖区域碰撞器，避免边界漏检）
        int trueBottomRow = -1;
        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.Cells[col, row] == CellState.Water || m_grid.Cells[col, row] == CellState.Bubble)
                {
                    trueBottomRow = row;
                    break;
                }
            }
            if (trueBottomRow >= 0) break;
        }

        if (trueBottomRow < 0) return false;

        // Step 2: 从真正的最底行开始，逐行向上找，收集在区域内的水格
        int bottomRow = -1;
        int waterCount = 0;

        for (int row = trueBottomRow; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.Cells[col, row] != CellState.Water) continue;

                m_grid.SetTempToCell(col, row);

                bool inRegion = false;
                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(m_grid.TempPoint))
                    {
                        inRegion = true;
                        break;
                    }
                }

                if (inRegion)
                {
                    if (bottomRow < 0) bottomRow = row;
                    if (row == bottomRow)
                    {
                        if (waterCount < m_randomWaterBuffer.Length)
                        {
                            m_randomWaterBuffer[waterCount] = col;
                            waterCount++;
                        }
                    }
                }
            }

            // 找到了最底行且已收集到水格，就不用再往上找了
            if (bottomRow >= 0 && waterCount > 0)
                break;
        }

        if (waterCount == 0) return false;

        // Step 3: 从最底部的水格中随机选一个移除
        int randomIdx = UnityEngine.Random.Range(0, waterCount);
        int selectedCol = m_randomWaterBuffer[randomIdx];
        int selectedRow = bottomRow;

        m_grid.Cells[selectedCol, selectedRow] = CellState.Empty;
        m_grid.LiquidColors[selectedCol, selectedRow] = Color.clear;

        // Step 4: 蒸发后尝试在附近随机生成气泡（优先同一行左右两侧，确保气泡从最底层上浮）
        // 常温蒸发传 spawnBubbles=false 时不冒泡，仅移除水分
        if (spawnBubbles && m_owner.enableBubbles && UnityEngine.Random.value < m_owner.bubbleSpawnChance)
        {
            SpawnBubbleNearby(selectedCol, selectedRow, regionColliders);
        }

        m_owner.m_isDirty = true;
        return true;
    }

    /// <summary>
    /// 在指定位置附近随机生成一个气泡（在水格中）
    /// 优先在同一行左右两侧生成（确保气泡从最底层开始上浮），
    /// 同一优先级内随机打乱顺序。
    /// </summary>
    private void SpawnBubbleNearby(int col, int row, Collider2D[] regionColliders)
    {
        // 按优先级分组尝试：组内随机，组间有序
        int[][] groups = new int[][]
        {
            new int[] { 0, 1 },    // 分组 0：同一行左右（最优先）
            new int[] { 2, 3 },    // 分组 1：斜上方
            new int[] { 4 }         // 分组 2：正上方
        };

        foreach (var group in groups)
        {
            // 组内 Fisher-Yates 洗牌
            for (int i = group.Length - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                int tmp = group[i];
                group[i] = group[j];
                group[j] = tmp;
            }

            // 按随机顺序尝试该组的候选位置
            foreach (int idx in group)
            {
                int tc = col + m_bubbleCandidateOffsets[idx, 0];
                int tr = row + m_bubbleCandidateOffsets[idx, 1];

                if (tc <= 0 || tc >= m_grid.Columns - 1 || tr <= 0 || tr >= m_grid.Rows - 1) continue;
                if (m_grid.IsBoundaryCell(tc, tr)) continue;

                // 气泡必须生成在水格中（被水包围才能上浮）
                if (m_grid.Cells[tc, tr] != CellState.Water) continue;

                // 确保在蒸发区域内
                m_grid.SetTempToCell(tc, tr);
                bool inRegion = false;
                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(m_grid.TempPoint))
                    {
                        inRegion = true;
                        break;
                    }
                }
                if (!inRegion) continue;

                // 生成气泡：将该水格变成气泡（气泡会在下一次物理模拟时上浮）
                m_grid.Cells[tc, tr] = CellState.Bubble;
                return;
            }
        }
    }

    /// <summary>
    /// 在指定区域内的最底层水格中随机生成指定数量的气泡
    /// 用于沸腾强度增加时批量生成气泡
    /// </summary>
    /// <param name="regionColliders">蒸发区域碰撞体</param>
    /// <param name="count">要生成的气泡数量</param>
    public void SpawnBubblesInRegion(Collider2D[] regionColliders, int count)
    {
        if (!m_owner.enableBubbles || m_grid.Cells == null || regionColliders == null || regionColliders.Length == 0 || count <= 0) return;

        // Step 1: 收集最底部 1~3 行所有在区域内的水格作为气泡候选
        int trueBottomRow = -1;
        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.Cells[col, row] == CellState.Water)
                {
                    trueBottomRow = row;
                    break;
                }
            }
            if (trueBottomRow >= 0) break;
        }

        if (trueBottomRow < 0) return;

        // 收集底部 3 行的水格（给气泡更多生成位置）
        int candidateCount = 0;
        int maxScanRow = Mathf.Min(trueBottomRow + 3, m_grid.Rows - 1);

        for (int row = trueBottomRow; row < maxScanRow; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                if (m_grid.Cells[col, row] != CellState.Water) continue;

                m_grid.SetTempToCell(col, row);
                bool inRegion = false;
                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(m_grid.TempPoint))
                    {
                        inRegion = true;
                        break;
                    }
                }
                if (!inRegion) continue;

                if (candidateCount < m_bubbleSpawnColBuffer.Length)
                {
                    m_bubbleSpawnColBuffer[candidateCount] = col;
                    m_bubbleSpawnRowBuffer[candidateCount] = row;
                    candidateCount++;
                }
            }
        }

        if (candidateCount == 0) return;

        // Step 2: 随机选择候选位置生成气泡（不重复选择同一位置）
        int spawnCount = Mathf.Min(count, candidateCount);

        // Fisher-Yates 洗牌前 N 个位置
        for (int i = 0; i < spawnCount; i++)
        {
            int j = UnityEngine.Random.Range(i, candidateCount);
            if (i != j)
            {
                int tmpCol = m_bubbleSpawnColBuffer[i];
                int tmpRow = m_bubbleSpawnRowBuffer[i];
                m_bubbleSpawnColBuffer[i] = m_bubbleSpawnColBuffer[j];
                m_bubbleSpawnRowBuffer[i] = m_bubbleSpawnRowBuffer[j];
                m_bubbleSpawnColBuffer[j] = tmpCol;
                m_bubbleSpawnRowBuffer[j] = tmpRow;
            }

            int bc = m_bubbleSpawnColBuffer[i];
            int br = m_bubbleSpawnRowBuffer[i];
            if (m_grid.Cells[bc, br] == CellState.Water)
            {
                m_grid.Cells[bc, br] = CellState.Bubble;
            }
        }

        if (spawnCount > 0)
            m_owner.m_isDirty = true;
    }
}
