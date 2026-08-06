using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 网格/物体跟踪子系统：初始化网格内容（边界、障碍物、液体、容器内部），
/// 并在运行时实时跟踪物体移动、重检障碍物、吸收洒在桌上的水。
/// 读写 LiquidGrid，复用协调器的碰撞器缓冲区与缓存（LiquidSource 列表、容器内部碰撞器）。
/// </summary>
public class ObjectGridTracker
{
    private readonly LayerGridPainter m_owner;
    private readonly LiquidGrid m_grid;

    public ObjectGridTracker(LayerGridPainter owner)
    {
        m_owner = owner;
        m_grid = owner.gridData;
    }

    /// <summary>
    /// 初始化网格内容（边界 → 障碍物 → 液体 → 容器内部 → 吸收），与协调器 InitializeGrid 中的顺序一致。
    /// </summary>
    public void Initialize()
    {
        AddBoundaryObstacles();
        DetectObstacles();
        InitializeLiquids();
        CacheContainerInteriors();
        AbsorbWaterAtTaggedObjects();
    }

    /// <summary>
    /// 添加边界障碍物（防止水流出容器）
    /// </summary>
    private void AddBoundaryObstacles()
    {
        for (int col = 0; col < m_grid.Columns; col++)
        {
            m_grid.Cells[col, 0] = CellState.Obstacle;
            m_grid.Cells[col, m_grid.Rows - 1] = CellState.Obstacle;
        }

        for (int row = 0; row < m_grid.Rows; row++)
        {
            m_grid.Cells[0, row] = CellState.Obstacle;
            m_grid.Cells[m_grid.Columns - 1, row] = CellState.Obstacle;
        }
    }

    /// <summary>
    /// 检测网格中的障碍物
    /// </summary>
    private void DetectObstacles()
    {
        if (m_owner.m_obstacleLayerMask == 0) return;

        Vector2 center = new Vector2(m_grid.OriginX + m_owner.gridWidth * 0.5f, m_grid.OriginY + m_owner.gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(m_owner.gridWidth + m_grid.CellSize, m_owner.gridHeight + m_grid.CellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_owner.m_colliderBuffer, m_owner.m_obstacleLayerMask);

        for (int col = 0; col < m_grid.Columns; col++)
        {
            for (int row = 0; row < m_grid.Rows; row++)
            {
                if (m_grid.Cells[col, row] != CellState.Empty) continue;

                m_grid.SetTempToCell(col, row);
                for (int i = 0; i < count; i++)
                {
                    var c = m_owner.m_colliderBuffer[i];
                    if (!c.isTrigger && c.OverlapPoint(m_grid.TempPoint))
                    {
                        m_grid.Cells[col, row] = CellState.Obstacle;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 初始化液体（通过 LiquidSource 组件确定不同液体的区域和颜色）
    /// </summary>
    private void InitializeLiquids()
    {
        LiquidSource[] sources = m_owner.m_cachedLiquidSources;
        if (sources == null) sources = UnityEngine.Object.FindObjectsOfType<LiquidSource>();
        if (sources != null && sources.Length > 0)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                for (int row = 0; row < m_grid.Rows; row++)
                {
                    if (m_grid.Cells[col, row] != CellState.Empty) continue;

                    m_grid.SetTempToCell(col, row);
                    foreach (var source in sources)
                    {
                        // 空容器（试剂/类型为 "none"）：此处可装液体但当前无液体，不参与初始灌水
                        if (source != null && !source.IsEmpty() && source.ContainsPoint(m_grid.TempPoint))
                        {
                            m_grid.Cells[col, row] = CellState.Water;
                            m_grid.LiquidColors[col, row] = source.GetEffectiveColor();
                            break;
                        }
                    }
                }
            }
        }

        // 后备：仍支持传统的 Water Layer 初始化（无自定义颜色）
        InitializeWater();

        // 双保险：无论水来自 LiquidSource 还是 Water Layer，空容器（none）区域一律清空，
        // 保证「可装液体但当前无液体」的容器液面透明、不显示任何体积。
        ClearEmptyContainerRegions();
    }

    /// <summary>
    /// 清空所有「空容器（IsEmpty）」液体区域内的水格，使其液面透明。
    /// 用于修正：空容器可能被 Water Layer 后备逻辑或其他途径误灌水的情况。
    /// </summary>
    private void ClearEmptyContainerRegions()
    {
        if (m_grid.Cells == null || m_grid.LiquidColors == null) return;

        LiquidSource[] sources = m_owner.m_cachedLiquidSources;
        if (sources == null) sources = UnityEngine.Object.FindObjectsOfType<LiquidSource>();
        if (sources == null || sources.Length == 0) return;

        foreach (var src in sources)
        {
            if (src == null || !src.IsEmpty()) continue;
            Collider2D[] region = src.regionColliders;
            if (region == null || region.Length == 0) continue;

            for (int col = 0; col < m_grid.Columns; col++)
            {
                for (int row = 0; row < m_grid.Rows; row++)
                {
                    if (m_grid.Cells[col, row] != CellState.Water && m_grid.Cells[col, row] != CellState.Bubble)
                        continue;
                    m_grid.SetTempToCell(col, row);
                    foreach (var c in region)
                    {
                        if (c != null && c.OverlapPoint(m_grid.TempPoint))
                        {
                            m_grid.Cells[col, row] = CellState.Empty;
                            m_grid.LiquidColors[col, row] = Color.clear;
                            break;
                        }
                    }
                }
            }
        }
        m_owner.m_isDirty = true;
    }

    /// <summary>
    /// 初始化水（检测场景中 Water layer 的物体位置）
    /// </summary>
    private void InitializeWater()
    {
        if (m_owner.m_waterLayer < 0) return;

        Vector2 center = new Vector2(m_grid.OriginX + m_owner.gridWidth * 0.5f, m_grid.OriginY + m_owner.gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(m_owner.gridWidth + m_grid.CellSize, m_owner.gridHeight + m_grid.CellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_owner.m_colliderBuffer, 1 << m_owner.m_waterLayer);

        for (int col = 0; col < m_grid.Columns; col++)
        {
            for (int row = 0; row < m_grid.Rows; row++)
            {
                if (m_grid.Cells[col, row] != CellState.Empty) continue;

                Vector2 worldPos = m_grid.GetWorldPosition(col, row);
                for (int i = 0; i < count; i++)
                {
                    if (m_owner.m_colliderBuffer[i].OverlapPoint(worldPos))
                    {
                        m_grid.Cells[col, row] = CellState.Water;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 实时更新网格状态（跟踪物体移动）
    /// </summary>
    public void UpdateGridFromObjects()
    {
        // 更新网格起点位置（跟随脚本所在物体移动）
        m_grid.OriginX = m_owner.transform.position.x - m_owner.gridWidth * 0.5f;
        m_grid.OriginY = m_owner.transform.position.y - m_owner.gridHeight * 0.5f;

        bool changed = false;

        // 仅在场景脏（物体/网格本体移动）时重扫障碍物。静止时障碍状态不变，跳过整段逐格 OverlapPoint。
        if (m_owner.m_sceneDirty && m_owner.m_obstacleLayerMask != 0)
        {
            // 一次物理查询获取网格区域内所有障碍物碰撞器
            Vector2 center = new Vector2(m_grid.OriginX + m_owner.gridWidth * 0.5f, m_grid.OriginY + m_owner.gridHeight * 0.5f);
            Vector2 boxSize = new Vector2(m_owner.gridWidth + m_grid.CellSize, m_owner.gridHeight + m_grid.CellSize);
            int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_owner.m_colliderBuffer, m_owner.m_obstacleLayerMask);

            for (int col = 0; col < m_grid.Columns; col++)
            {
                for (int row = 0; row < m_grid.Rows; row++)
                {
                    if (m_grid.IsBoundaryCell(col, row)) continue;

                    m_grid.SetTempToCell(col, row);
                    bool hasObstacle = false;
                    for (int i = 0; i < count; i++)
                    {
                        var c = m_owner.m_colliderBuffer[i];
                        if (!c.isTrigger && c.OverlapPoint(m_grid.TempPoint))
                        {
                            hasObstacle = true;
                            break;
                        }
                    }

                    if (hasObstacle && m_grid.Cells[col, row] != CellState.Obstacle)
                    {
                        // 新障碍物出现 — 挤出水或气泡
                        if (m_grid.Cells[col, row] == CellState.Water || m_grid.Cells[col, row] == CellState.Bubble)
                        {
                            if (!TryMoveWaterNearby(col, row))
                                continue; // 找不到空位，跳过，不覆盖水/气泡
                        }
                        m_grid.Cells[col, row] = CellState.Obstacle;
                        m_grid.LiquidColors[col, row] = Color.clear;
                        changed = true;
                    }
                    else if (!hasObstacle && m_grid.Cells[col, row] == CellState.Obstacle)
                    {
                        // 障碍物消失
                        m_grid.Cells[col, row] = CellState.Empty;
                        m_grid.LiquidColors[col, row] = Color.clear;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
            m_owner.m_isDirty = true;

        // 吸收碰到 absorbTag 物体的水（水每 tick 都会流动到桌上，故始终运行；内部有前置快判）
        AbsorbWaterAtTaggedObjects();

        // 本轮已处理，复位脏标记；下一帧若物体仍静止则跳过障碍重检
        m_owner.m_sceneDirty = false;
    }

    /// <summary>
    /// 吸收水：碰到 absorbTag 物体上方的水直接清空
    /// </summary>
    public void AbsorbWaterAtTaggedObjects()
    {
        if (string.IsNullOrEmpty(m_owner.absorbTag)) return;

        Vector2 center = new Vector2(m_grid.OriginX + m_owner.gridWidth * 0.5f, m_grid.OriginY + m_owner.gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(m_owner.gridWidth + m_grid.CellSize, m_owner.gridHeight + m_grid.CellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_owner.m_colliderBuffer);

        // 收集 absorbTag 的碰撞器到 buffer 前部
        int absorbCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (m_owner.m_colliderBuffer[i] != null && m_owner.m_colliderBuffer[i].tag == m_owner.absorbTag)
            {
                if (i != absorbCount)
                    m_owner.m_colliderBuffer[absorbCount] = m_owner.m_colliderBuffer[i];
                absorbCount++;
            }
        }
        if (absorbCount == 0) return;

        // 逐格检测：在 Table 上的格及其正上方有水 → 吸收
        bool absorbed = false;
        for (int col = 1; col < m_grid.Columns - 1; col++)
        {
            for (int row = 1; row < m_grid.Rows - 1; row++)
            {
                // 前置快判：本格和正上方都没有水或气泡 → 这里不可能有东西被吸收，直接跳过整段 OverlapPoint。
                // 干燥空气占网格绝大多数，此判断能把吸收扫描的 OverlapPoint 调用降到「仅水体附近」。
                bool hasWaterHere = m_grid.Cells[col, row] == CellState.Water || m_grid.Cells[col, row] == CellState.Bubble;
                bool hasWaterAbove = row + 1 < m_grid.Rows - 1
                    && (m_grid.Cells[col, row + 1] == CellState.Water || m_grid.Cells[col, row + 1] == CellState.Bubble);
                if (!hasWaterHere && !hasWaterAbove)
                    continue;

                m_grid.SetTempToCell(col, row);
                bool onTable = false;
                for (int i = 0; i < absorbCount; i++)
                {
                    if (m_owner.m_colliderBuffer[i].OverlapPoint(m_grid.TempPoint))
                    {
                        onTable = true;
                        break;
                    }
                }
                if (!onTable) continue;

                // 处于任意容器内部（LiquidRegion）的水不算「洒在桌上」，不要吸收，
                // 这样从一只杯子倒进空杯子时液体能被接住、留在杯内累积。
                if (IsInsideContainerInterior(m_grid.TempPoint)) continue;

                // Table 格本身有水或气泡 → 吸收
                if (m_grid.Cells[col, row] == CellState.Water || m_grid.Cells[col, row] == CellState.Bubble)
                {
                    m_grid.Cells[col, row] = CellState.Empty;
                    m_grid.LiquidColors[col, row] = Color.clear;
                    absorbed = true;
                }
                // Table 格正上方有水或气泡 → 吸收
                if (row + 1 < m_grid.Rows - 1 && (m_grid.Cells[col, row + 1] == CellState.Water || m_grid.Cells[col, row + 1] == CellState.Bubble))
                {
                    // 正上方的水若处于容器内部（如在空杯子里），也不吸收
                    m_grid.TempPoint2.Set(m_grid.OriginX + (col + 0.5f) * m_grid.CellSize, m_grid.OriginY + (row + 1.5f) * m_grid.CellSize);
                    if (IsInsideContainerInterior(m_grid.TempPoint2)) continue;
                    m_grid.Cells[col, row + 1] = CellState.Empty;
                    m_grid.LiquidColors[col, row + 1] = Color.clear;
                    absorbed = true;
                }
            }
        }

        if (absorbed)
            m_owner.m_isDirty = true;
    }

    /// <summary>
    /// 缓存所有「容器内部区域」碰撞器（LiquidRegion 子物体 + LiquidSource.regionColliders），
    /// 用于吸收逻辑判断某水格是否处于容器内，从而不被桌面吸收。
    /// 容器在场景中移动时碰撞器的世界坐标会自动更新，无需每帧重建。
    /// 仅遍历已缓存的容器子树，避免 FindObjectsOfType&lt;Transform&gt; 对整个场景扫描。
    /// </summary>
    private void CacheContainerInteriors()
    {
        if (m_owner.m_cachedLiquidSources == null) m_owner.CacheLiquidSources();
        var list = new List<Collider2D>();

        if (m_owner.m_cachedLiquidSources != null)
        {
            foreach (var s in m_owner.m_cachedLiquidSources)
            {
                if (s == null) continue;

                // 1) LiquidSource 自带的区域碰撞器（含空容器，通常已覆盖整个容器形状）
                if (s.regionColliders != null)
                {
                    foreach (var c in s.regionColliders)
                        if (c != null) list.Add(c);
                }

                // 2) 向上回溯到容器根，再收集名为 LiquidRegion 的子物体（兼容无 regionColliders 的遗留情况）。
                //    仅扫描容器子树，而非全场景所有 Transform。
                Transform root = s.transform;
                while (root.parent != null) root = root.parent;
                var regionTransforms = root.GetComponentsInChildren<Transform>();
                foreach (var t in regionTransforms)
                {
                    if (t != null && t.name == "LiquidRegion")
                    {
                        var cols = t.GetComponents<Collider2D>();
                        foreach (var c in cols)
                            if (c != null) list.Add(c);
                    }
                }
            }
        }

        m_owner.m_containerInteriorColliders = list.ToArray();
    }

    /// <summary>
    /// 判断某世界坐标是否处于任一容器内部（LiquidRegion 区域）。
    /// 用于吸收逻辑：容器内部的水不应被桌面吸收，而是被杯子接住/保留。
    /// </summary>
    private bool IsInsideContainerInterior(Vector2 worldPos)
    {
        if (m_owner.m_containerInteriorColliders == null || m_owner.m_containerInteriorColliders.Length == 0) return false;
        foreach (var c in m_owner.m_containerInteriorColliders)
        {
            if (c != null && c.OverlapPoint(worldPos)) return true;
        }
        return false;
    }

    /// <summary>
    /// 尝试把水或气泡移到附近的空位置，返回是否成功
    /// </summary>
    private bool TryMoveWaterNearby(int col, int row)
    {
        CellState originalState = m_grid.Cells[col, row];
        Color originalColor = m_grid.LiquidColors[col, row];

        // 向上
        if (row < m_grid.Rows - 1 && m_grid.Cells[col, row + 1] == CellState.Empty)
        {
            m_grid.Cells[col, row + 1] = originalState;
            m_grid.LiquidColors[col, row + 1] = originalColor;
            return true;
        }
        // 向左
        if (col > 0 && m_grid.Cells[col - 1, row] == CellState.Empty)
        {
            m_grid.Cells[col - 1, row] = originalState;
            m_grid.LiquidColors[col - 1, row] = originalColor;
            return true;
        }
        // 向右
        if (col < m_grid.Columns - 1 && m_grid.Cells[col + 1, row] == CellState.Empty)
        {
            m_grid.Cells[col + 1, row] = originalState;
            m_grid.LiquidColors[col + 1, row] = originalColor;
            return true;
        }
        // 向上左
        if (row < m_grid.Rows - 1 && col > 0 && m_grid.Cells[col - 1, row + 1] == CellState.Empty)
        {
            m_grid.Cells[col - 1, row + 1] = originalState;
            m_grid.LiquidColors[col - 1, row + 1] = originalColor;
            return true;
        }
        // 向上右
        if (row < m_grid.Rows - 1 && col < m_grid.Columns - 1 && m_grid.Cells[col + 1, row + 1] == CellState.Empty)
        {
            m_grid.Cells[col + 1, row + 1] = originalState;
            m_grid.LiquidColors[col + 1, row + 1] = originalColor;
            return true;
        }
        return false;
    }
}
