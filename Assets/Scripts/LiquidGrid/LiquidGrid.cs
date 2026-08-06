using UnityEngine;

/// <summary>
/// 液体网格数据底座：保存水模拟所需的全部网格状态与坐标换算。
/// 所有子系统（模拟、渲染、气泡、区域查询、障碍跟踪、拖拽）共享同一个 LiquidGrid 实例，
/// 通过它读写元胞状态、颜色、维度与几何原点，避免把庞大的网格数组散落在协调器里。
/// 字段以 public 暴露，因为它是纯数据容器；坐标换算与缓冲交换等无副作用的小工具也放在这里。
/// </summary>
public class LiquidGrid
{
    // 元胞状态网格（当前帧 / 下一帧双缓冲）
    public CellState[,] Cells;
    public CellState[,] NextCells;

    // 液体颜色网格（与 Cells 并行，仅对 Water 格子有效）
    public Color[,] LiquidColors;
    public Color[,] NextLiquidColors;

    // 网格维度
    public int Columns;
    public int Rows;

    // 网格几何原点（左下角世界坐标）与每格边长
    public float OriginX;
    public float OriginY;
    public float CellSize;

    // 每帧「是否已移动」标记（模拟主循环用）
    public bool[,] Moved;

    // 每行最左/最右水格列（间隙填充 pass 用，O(1) 替代 O(width) 扫描）
    public int[] RowLeftmostWater;
    public int[] RowRightmostWater;

    // 标记本次模拟是否有颜色扩散变化（交换缓冲后复位）
    public bool ColorDiffused;

    // 复用的临时点：热循环中每格 new Vector2 会产生大量 GC，统一复用避免分配
    public Vector2 TempPoint;
    public Vector2 TempPoint2;

    /// <summary>
    /// 按维度分配所有网格数组（若已存在则覆盖）。
    /// </summary>
    public void Allocate(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        Cells = new CellState[columns, rows];
        NextCells = new CellState[columns, rows];
        Moved = new bool[columns, rows];
        LiquidColors = new Color[columns, rows];
        NextLiquidColors = new Color[columns, rows];
        RowLeftmostWater = new int[rows];
        RowRightmostWater = new int[rows];
    }

    /// <summary>
    /// 清空元胞状态网格（Array.Clear 比嵌套循环快）。
    /// </summary>
    public void Clear()
    {
        if (Cells != null) System.Array.Clear(Cells, 0, Cells.Length);
    }

    /// <summary>
    /// 把当前帧网格复制到下一帧缓冲（模拟开始时调用）。
    /// </summary>
    public void CopyCurrentToNext()
    {
        System.Array.Copy(Cells, NextCells, Cells.Length);
        if (LiquidColors != null && NextLiquidColors != null)
            System.Array.Copy(LiquidColors, NextLiquidColors, LiquidColors.Length);
    }

    /// <summary>
    /// 交换当前帧与下一帧缓冲的引用（模拟/拖拽提交后调用，避免复制）。
    /// </summary>
    public void SwapSimulationBuffers()
    {
        var tg = Cells; Cells = NextCells; NextCells = tg;
        var tc = LiquidColors; LiquidColors = NextLiquidColors; NextLiquidColors = tc;
    }

    /// <summary>
    /// 根据网格坐标获取该格中心的世界坐标。
    /// </summary>
    public Vector2 GetWorldPosition(int col, int row)
    {
        return new Vector2(
            OriginX + (col + 0.5f) * CellSize,
            OriginY + (row + 0.5f) * CellSize
        );
    }

    /// <summary>
    /// 把某格中心的世界坐标写入 TempPoint（避免每格 new 分配）。
    /// </summary>
    public void SetTempToCell(int col, int row)
    {
        TempPoint.Set(
            OriginX + (col + 0.5f) * CellSize,
            OriginY + (row + 0.5f) * CellSize
        );
    }

    /// <summary>
    /// 判断是否是边界单元格（边界恒为障碍物，水不会流出）。
    /// </summary>
    public bool IsBoundaryCell(int col, int row)
    {
        return col == 0 || col == Columns - 1 || row == 0 || row == Rows - 1;
    }
}
