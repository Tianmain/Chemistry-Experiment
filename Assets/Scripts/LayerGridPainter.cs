using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 互阻规则：指定两个 Tag 之间互相阻碍
/// </summary>
[Serializable]
public class MutualBlockRule
{
    [Tooltip("需要互相阻碍的 Tag A（如 Alcohol_Lamp）")]
    public string tagA = "";

    [Tooltip("需要互相阻碍的 Tag B（如 Tripod）")]
    public string tagB = "";

    [Tooltip("碰撞检测容差（distance 阈值，值越大越难触发碰撞）")]
    public float collisionTolerance = 0f;
}

/// <summary>
/// 水网格模拟器 - 基于网格的水流动模拟
/// 检测场景中 Water Layer 的物体作为初始水源，水受到重力自然往下流
/// 遇到 Obstacle Layer 的物体停止向下流动，可以向左/右流动
/// </summary>
[ExecuteInEditMode]
public partial class LayerGridPainter : MonoBehaviour
{
    [Header("网格设置")]
    [Tooltip("每个单元格的边长（世界单位）")]
    public float cellSize = 1.0f;

    [Tooltip("网格总宽度（世界单位）")]
    public float gridWidth = 10f;

    [Tooltip("网格总高度（世界单位）")]
    public float gridHeight = 10f;

    [Header("颜色设置")]
    public Color waterColor = new Color(0.2f, 0.5f, 1f, 0.6f);
    public Color obstacleColor = new Color(1f, 0.3f, 0.3f, 0.8f);
    public Color bubbleColor = new Color(1f, 1f, 1f, 0.9f);

    [Header("气泡效果")]
    [Tooltip("是否启用蒸发气泡效果")]
    public bool enableBubbles = true;
    [Tooltip("每次蒸发时生成气泡的概率（0~1）")]
    [Range(0f, 1f)]
    public float bubbleSpawnChance = 0.7f;
    [Tooltip("气泡上浮速度因子（值越大上浮越快）")]
    [Range(0.5f, 3f)]
    public float bubbleRiseSpeed = 1.2f;

    [Header("选项")]
    [Tooltip("是否启用水模拟")]
    public bool enableSimulation = true;

    [Tooltip("水流更新间隔（秒）")]
    public float updateInterval = 0.05f;

    [Tooltip("每次模拟更新执行多少步（值越大水流越快，但可能视觉跳跃）")]
    public int simulationStepsPerTick = 1;

    [Tooltip("是否实时跟踪物体移动")]
    public bool trackObjectMovement = true;

    [Tooltip("可拖拽物体的Tag名称列表")]
    public string[] draggableTags = { };

    [Tooltip("碰到此 Tag 的物体时水会被吸收（消失）")]
    public string absorbTag = "Table";

    [Header("拖拽设置")]
    [Tooltip("拖拽时物体的最大移动速度（世界单位/秒）")]
    public float dragSpeed = 10f;

    [Tooltip("父子/同组可拖拽物体之间的碰撞穿透容差（世界单位），用于灯帽套合等场景")]
    public float parentChildCollisionTolerance = 0.2f;

    [Tooltip("绝对不可穿透的 Tag 列表（所有物体都不能穿透）")]
    public string[] impassableTags = { "Table" };

    [Tooltip("全局可穿透的 Tag 列表（除非在当前拖拽物体的不可穿透列表中）")]
    public string[] penetrableTags = { "Tripod" };

    [Tooltip("互相阻碍的 Tag 对列表（如 Alcohol_Lamp 和 Tripod 会互相阻挡）")]
    public MutualBlockRule[] mutualBlockRules;

    [Header("液体混合")]
    [Tooltip("液体混合扩散速率（0=不混合，0.1=每步向邻居颜色靠近10%）")]
    [Range(0f, 0.5f)]
    public float mixingDiffusionRate = 0.08f;

    [Tooltip("颜色差异阈值（RGB分量差值之和低于此值不触发混合）")]
    [Range(0f, 0.2f)]
    public float mixingColorThreshold = 0.02f;

    [Header("Layer 设置")]
    [Tooltip("障碍物 Layer 名称列表")]
    public string[] obstacleLayerNames = { "Obstacle" , "Equipment" };

    // 每个单元格在纹理中占多少像素
    private const int PIXELS_PER_CELL = 16;

    // 可视化变量（填充显示）
    private Texture2D fillTexture;          // 填充纹理
    private Sprite fillSprite;              // 填充精灵
    private GameObject fillObj;
    private SpriteRenderer fillRenderer;
    private Color[] fillCache;
    private int cachedColumns = -1;
    private int cachedRows = -1;
    private bool m_isRebuilding = false;

    // 水模拟变量
    private CellState[,] m_grid;
    private CellState[,] m_nextGrid;
    private bool[,] m_moved;
    private int m_columns;
    private int m_rows;

    // Layer 索引缓存
    private int m_waterLayer = -1;
    private int m_obstacleLayerMask = 0;  // 合并的障碍物 Layer 位掩码

    // 网格起点
    private float m_originX;
    private float m_originY;

    // 更新计时器
    private float m_updateTimer;

    // Camera 缓存
    private Camera m_cachedCamera;

    // dirty flag — 网格变化时才刷新纹理
    private bool m_isDirty;

    // 可拖拽 Tag 的 HashSet
    private HashSet<string> m_draggableTagSet;

    // 可穿透 Tag 的 HashSet
    private HashSet<string> m_penetrableTagSet;

    // 绝对不可穿透 Tag 的 HashSet
    private HashSet<string> m_impassableTagSet;

    // 预分配碰撞器缓冲区
    private Collider2D[] m_colliderBuffer = new Collider2D[128];

    // 缓存碰撞过滤器，避免每帧重复构造（SetLayerMask 按需更新）
    private ContactFilter2D m_contactFilter;

    // 拖拽状态
    private bool m_isDragging = false;
    private GameObject m_draggedObject;
    private Vector3 m_dragStartMousePos;
    private Vector3 m_dragStartObjPos;
    private Quaternion m_dragStartObjRot;
    private float m_dragOffsetX = 0;
    private float m_dragOffsetY = 0;
    private Bounds m_draggedObjOriginalBounds;
    private Collider2D m_draggedCollider;
    private Vector3 m_lastValidDragPos;
    private Collider2D[] m_draggedObjColliders; // 被拖拽物体的碰撞器缓存（用于精确偏移格子）
    private bool[] m_draggedCoverage; // 拖拽开始时缓存的格子覆盖状态（原始位置）

    /// <summary>
    /// 当前是否正在拖拽物体
    /// </summary>
    public bool IsDragging => m_isDragging;

    /// <summary>
    /// 当前被拖拽的物体（未拖拽时为 null）
    /// </summary>
    public GameObject DraggedObject => m_draggedObject;

    // 液体颜色网格（与 m_grid 并行，仅对 Water 格子有效）
    private Color[,] m_liquidColorGrid;
    private Color[,] m_nextLiquidColorGrid;

    private void Awake()
    {
        CacheLayerIndices();
        RebuildDraggableTagSet();
        CreateVisualizerObject();
        InitializeGrid();
        m_contactFilter = new ContactFilter2D();
    }

    private void OnEnable()
    {
        CacheLayerIndices();
        RebuildDraggableTagSet();
        CreateVisualizerObject();
        InitializeGrid();
        RebuildGrid();
    }

    private void Update()
    {
        if (Application.isPlaying && m_grid != null)
        {
            HandleDrag();

            if (m_isDragging && m_draggedObject != null)
            {
                m_isDirty = true;
            }
            else if (!Input.GetMouseButton(0))
            {
                m_dragOffsetX = 0;
                m_dragOffsetY = 0;

                m_updateTimer += Time.deltaTime;
                if (m_updateTimer >= updateInterval)
                    {
                        m_updateTimer = 0f;

                        if (trackObjectMovement)
                        {
                            UpdateGridFromObjects();
                        }

                        if (enableSimulation)
                        {
                            int steps = Mathf.Max(1, simulationStepsPerTick);
                            for (int i = 0; i < steps; i++)
                            {
                                ProcessWaterSimulation();
                            }
                        }
                    }
            }
        }

        // 只在脏标记时刷新纹理
        if (m_isDirty)
        {
            RefreshColors();
            m_isDirty = false;
        }
    }

    private void CacheLayerIndices()
    {
        m_waterLayer = LayerMask.NameToLayer("Water");
        m_obstacleLayerMask = 0;
        if (obstacleLayerNames != null)
        {
            foreach (string name in obstacleLayerNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0)
                    m_obstacleLayerMask |= 1 << layer;
            }
        }
    }

    /// <summary>
    /// 判断指定 Layer 是否属于障碍物
    /// </summary>
    private bool IsObstacleLayer(int layer)
    {
        return (m_obstacleLayerMask & (1 << layer)) != 0;
    }

    /// <summary>
    /// 重建可拖拽 Tag 的 HashSet
    /// </summary>
    private void RebuildDraggableTagSet()
    {
        m_draggableTagSet = new HashSet<string>(draggableTags ?? System.Array.Empty<string>());
        m_penetrableTagSet = new HashSet<string>(penetrableTags ?? System.Array.Empty<string>());
        m_impassableTagSet = new HashSet<string>(impassableTags ?? System.Array.Empty<string>());
    }

    /// <summary>
    /// 获取缓存的 Camera（避免每帧 Camera.main 查找）
    /// </summary>
    private Camera GetCachedCamera()
    {
        if (m_cachedCamera == null)
            m_cachedCamera = Camera.main;
        return m_cachedCamera;
    }

    /// <summary>
    /// 初始化网格数据（水模拟用）
    /// </summary>
    private void InitializeGrid()
    {
        if (cellSize <= 0 || gridWidth <= 0 || gridHeight <= 0) return;

        m_columns = Mathf.CeilToInt(gridWidth / cellSize);
        m_rows = Mathf.CeilToInt(gridHeight / cellSize);

        m_grid = new CellState[m_columns, m_rows];
        m_nextGrid = new CellState[m_columns, m_rows];
        m_moved = new bool[m_columns, m_rows];
        m_liquidColorGrid = new Color[m_columns, m_rows];
        m_nextLiquidColorGrid = new Color[m_columns, m_rows];
        m_originX = transform.position.x - gridWidth * 0.5f;
        m_originY = transform.position.y - gridHeight * 0.5f;

        // Array.Clear 比嵌套循环快
        Array.Clear(m_grid, 0, m_grid.Length);

        AddBoundaryObstacles();
        DetectObstacles();
        InitializeLiquids();
        AbsorbWaterAtTaggedObjects();
        m_isDirty = true;
    }

    /// <summary>
    /// 添加边界障碍物（防止水流出容器）
    /// </summary>
    private void AddBoundaryObstacles()
    {
        for (int col = 0; col < m_columns; col++)
        {
            m_grid[col, 0] = CellState.Obstacle;
            m_grid[col, m_rows - 1] = CellState.Obstacle;
        }

        for (int row = 0; row < m_rows; row++)
        {
            m_grid[0, row] = CellState.Obstacle;
            m_grid[m_columns - 1, row] = CellState.Obstacle;
        }
    }

    /// <summary>
    /// 检测网格中的障碍物
    /// </summary>
    private void DetectObstacles()
    {
        if (m_obstacleLayerMask == 0) return;

        Vector2 center = new Vector2(m_originX + gridWidth * 0.5f, m_originY + gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(gridWidth + cellSize, gridHeight + cellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_colliderBuffer, m_obstacleLayerMask);

        for (int col = 0; col < m_columns; col++)
        {
            for (int row = 0; row < m_rows; row++)
            {
                if (m_grid[col, row] != CellState.Empty) continue;

                Vector2 worldPos = GetWorldPosition(col, row);
                for (int i = 0; i < count; i++)
                {
                    var c = m_colliderBuffer[i];
                    if (!c.isTrigger && c.OverlapPoint(worldPos))
                    {
                        m_grid[col, row] = CellState.Obstacle;
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
        LiquidSource[] sources = FindObjectsOfType<LiquidSource>();
        if (sources != null && sources.Length > 0)
        {
            for (int col = 0; col < m_columns; col++)
            {
                for (int row = 0; row < m_rows; row++)
                {
                    if (m_grid[col, row] != CellState.Empty) continue;

                    Vector2 worldPos = GetWorldPosition(col, row);
                    foreach (var source in sources)
                    {
                        if (source != null && source.ContainsPoint(worldPos))
                        {
                            m_grid[col, row] = CellState.Water;
                            m_liquidColorGrid[col, row] = source.GetEffectiveColor();
                            break;
                        }
                    }
                }
            }
        }

        // 后备：仍支持传统的 Water Layer 初始化（无自定义颜色）
        InitializeWater();
    }

    /// <summary>
    /// 初始化水（检测场景中 Water layer 的物体位置）
    /// </summary>
    private void InitializeWater()
    {
        if (m_waterLayer < 0) return;

        Vector2 center = new Vector2(m_originX + gridWidth * 0.5f, m_originY + gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(gridWidth + cellSize, gridHeight + cellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_colliderBuffer, 1 << m_waterLayer);

        for (int col = 0; col < m_columns; col++)
        {
            for (int row = 0; row < m_rows; row++)
            {
                if (m_grid[col, row] != CellState.Empty) continue;

                Vector2 worldPos = GetWorldPosition(col, row);
                for (int i = 0; i < count; i++)
                {
                    if (m_colliderBuffer[i].OverlapPoint(worldPos))
                    {
                        m_grid[col, row] = CellState.Water;
                        break;
                    }
                }
            }
        }
    }

    private void CreateVisualizerObject()
    {
        // 填充对象
        if (fillObj != null && fillRenderer != null)
        {
            fillObj.transform.localPosition = Vector3.zero;
            fillObj.transform.localRotation = Quaternion.identity;
        }
        else
        {
            Transform existing = transform.Find("FillArea");
            if (existing == null)
            {
                fillObj = new GameObject("FillArea");
                fillObj.transform.SetParent(transform);
                fillObj.transform.localPosition = Vector3.zero;
                fillObj.transform.localRotation = Quaternion.identity;
                fillRenderer = fillObj.AddComponent<SpriteRenderer>();
                fillRenderer.sortingOrder = 99;
            }
            else
            {
                fillObj = existing.gameObject;
                fillRenderer = fillObj.GetComponent<SpriteRenderer>();
                if (fillRenderer == null)
                    fillRenderer = fillObj.AddComponent<SpriteRenderer>();
            }
        }
    }

    private void RebuildGrid()
    {
        if (cellSize <= 0 || gridWidth <= 0 || gridHeight <= 0) return;
        if (m_isRebuilding) return;

        m_isRebuilding = true;

        int columns = Mathf.CeilToInt(gridWidth / cellSize);
        int rows = Mathf.CeilToInt(gridHeight / cellSize);

        int texWidth = columns * PIXELS_PER_CELL;
        int texHeight = rows * PIXELS_PER_CELL;

        if (fillCache == null || cachedColumns != columns || cachedRows != rows)
        {
            fillCache = new Color[texWidth * texHeight];
            cachedColumns = columns;
            cachedRows = rows;
        }

        // 填充 Sprite
        if (fillObj != null && fillRenderer != null && fillCache != null)
        {
            // 尺寸变化时才重建纹理
            if (fillTexture == null || fillTexture.width != texWidth || fillTexture.height != texHeight)
            {
                if (fillTexture != null) DestroyImmediate(fillTexture);
                fillTexture = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
                fillTexture.filterMode = FilterMode.Point;
                fillTexture.wrapMode = TextureWrapMode.Clamp;
            }

            // Sprite 需要引用新纹理时重建
            if (fillSprite == null)
            {
                Rect fillRect = new Rect(0, 0, texWidth, texHeight);
                Vector2 fillPivot = new Vector2(0.5f, 0.5f);
                fillSprite = Sprite.Create(fillTexture, fillRect, fillPivot, 1f);
                fillRenderer.sprite = fillSprite;
            }

            float scaleX = gridWidth / texWidth;
            float scaleY = gridHeight / texHeight;
            fillObj.transform.localScale = new Vector3(scaleX, scaleY, 1f);
            fillObj.transform.localPosition = Vector3.zero;
            fillObj.transform.localRotation = Quaternion.identity;
        }

        m_isRebuilding = false;

        RefreshColors();
    }

    public void UpdateGrid()
    {
        if (fillSprite != null) { DestroyImmediate(fillSprite); fillSprite = null; }
        cachedColumns = -1;
        cachedRows = -1;
        InitializeGrid();
        RebuildGrid();
    }

    // 预分配的随机水格索引缓冲区（避免每帧 GC）
    private int[] m_randomWaterBuffer = new int[256];

    /// <summary>
    /// 从指定碰撞体区域内移除一格水（从最底部水格中随机选择一个移除，模拟底部均匀蒸发）
    /// 蒸发后会在原处或附近随机生成气泡，气泡会上浮穿过水体，模拟沸腾效果
    /// </summary>
    /// <returns>是否成功移除了一格水</returns>
    public bool RemoveWaterFromRegion(Collider2D[] regionColliders)
    {
        if (m_grid == null || regionColliders == null || regionColliders.Length == 0) return false;

        // Step 1: 先找到网格中真正有水的最底行（不依赖区域碰撞器，避免边界漏检）
        int trueBottomRow = -1;
        for (int row = 1; row < m_rows - 1; row++)
        {
            for (int col = 1; col < m_columns - 1; col++)
            {
                if (m_grid[col, row] == CellState.Water || m_grid[col, row] == CellState.Bubble)
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

        for (int row = trueBottomRow; row < m_rows - 1; row++)
        {
            for (int col = 1; col < m_columns - 1; col++)
            {
                if (m_grid[col, row] != CellState.Water) continue;

                Vector2 worldPos = new Vector2(
                    m_originX + (col + 0.5f) * cellSize,
                    m_originY + (row + 0.5f) * cellSize);

                bool inRegion = false;
                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(worldPos))
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
                        if (waterCount >= m_randomWaterBuffer.Length)
                        {
                            Array.Resize(ref m_randomWaterBuffer, m_randomWaterBuffer.Length * 2);
                        }
                        m_randomWaterBuffer[waterCount] = col;
                        waterCount++;
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

        m_grid[selectedCol, selectedRow] = CellState.Empty;
        m_liquidColorGrid[selectedCol, selectedRow] = Color.clear;

        // Step 4: 蒸发后尝试在附近随机生成气泡（优先同一行左右两侧，确保气泡从最底层上浮）
        if (enableBubbles && UnityEngine.Random.value < bubbleSpawnChance)
        {
            SpawnBubbleNearby(selectedCol, selectedRow, regionColliders);
        }

        m_isDirty = true;
        return true;
    }

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

                if (tc <= 0 || tc >= m_columns - 1 || tr <= 0 || tr >= m_rows - 1) continue;
                if (IsBoundaryCell(tc, tr)) continue;

                // 气泡必须生成在水格中（被水包围才能上浮）
                if (m_grid[tc, tr] != CellState.Water) continue;

                // 确保在蒸发区域内
                Vector2 worldPos = GetWorldPosition(tc, tr);
                bool inRegion = false;
                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(worldPos))
                    {
                        inRegion = true;
                        break;
                    }
                }
                if (!inRegion) continue;

                // 生成气泡：将该水格变成气泡（气泡会在下一次物理模拟时上浮）
                m_grid[tc, tr] = CellState.Bubble;
                return;
            }
        }
    }

    // 预分配的气泡生成位置缓冲区
    private int[] m_bubbleSpawnColBuffer = new int[256];
    private int[] m_bubbleSpawnRowBuffer = new int[256];

    /// <summary>
    /// 在指定区域内的最底层水格中随机生成指定数量的气泡
    /// 用于沸腾强度增加时批量生成气泡
    /// </summary>
    /// <param name="regionColliders">蒸发区域碰撞体</param>
    /// <param name="count">要生成的气泡数量</param>
    public void SpawnBubblesInRegion(Collider2D[] regionColliders, int count)
    {
        if (!enableBubbles || m_grid == null || regionColliders == null || regionColliders.Length == 0 || count <= 0) return;

        // Step 1: 收集最底部 1~3 行所有在区域内的水格作为气泡候选
        int trueBottomRow = -1;
        for (int row = 1; row < m_rows - 1; row++)
        {
            for (int col = 1; col < m_columns - 1; col++)
            {
                if (m_grid[col, row] == CellState.Water)
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
        int maxScanRow = Mathf.Min(trueBottomRow + 3, m_rows - 1);

        for (int row = trueBottomRow; row < maxScanRow; row++)
        {
            for (int col = 1; col < m_columns - 1; col++)
            {
                if (m_grid[col, row] != CellState.Water) continue;

                Vector2 worldPos = GetWorldPosition(col, row);
                bool inRegion = false;
                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(worldPos))
                    {
                        inRegion = true;
                        break;
                    }
                }
                if (!inRegion) continue;

                if (candidateCount >= m_bubbleSpawnColBuffer.Length)
                {
                    Array.Resize(ref m_bubbleSpawnColBuffer, m_bubbleSpawnColBuffer.Length * 2);
                    Array.Resize(ref m_bubbleSpawnRowBuffer, m_bubbleSpawnRowBuffer.Length * 2);
                }
                m_bubbleSpawnColBuffer[candidateCount] = col;
                m_bubbleSpawnRowBuffer[candidateCount] = row;
                candidateCount++;
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
            if (m_grid[bc, br] == CellState.Water)
            {
                m_grid[bc, br] = CellState.Bubble;
            }
        }

        if (spawnCount > 0)
            m_isDirty = true;
    }

    /// <summary>
    /// 统计指定碰撞体区域内的水格总数
    /// </summary>
    public int GetWaterCountInRegion(Collider2D[] regionColliders)
    {
        if (m_grid == null || regionColliders == null || regionColliders.Length == 0) return 0;

        int count = 0;
        for (int row = 0; row < m_rows; row++)
        {
            for (int col = 0; col < m_columns; col++)
            {
                if (m_grid[col, row] != CellState.Water) continue;

                Vector2 worldPos = new Vector2(
                    m_originX + (col + 0.5f) * cellSize,
                    m_originY + (row + 0.5f) * cellSize);

                foreach (var colld in regionColliders)
                {
                    if (colld != null && colld.OverlapPoint(worldPos))
                    {
                        count++;
                        break;
                    }
                }
            }
        }
        return count;
    }

    private void RefreshColors()
    {
        if ((fillTexture == null || fillCache == null) && !m_isRebuilding)
        {
            RebuildGrid();
            return;
        }

        if (fillTexture == null || fillCache == null) return;

        int columns = cachedColumns;
        int rows = cachedRows;
        int texWidth = columns * PIXELS_PER_CELL;
        int texHeight = rows * PIXELS_PER_CELL;

        // 清空填充缓存（Array.Clear 比手动循环更快）
        Array.Clear(fillCache, 0, fillCache.Length);

        // 绘制水和障碍物到填充缓存
        if (Application.isPlaying && m_grid != null)
        {
            RefreshColorsFromSimulation(columns, rows, texWidth, texHeight);
        }
        else
        {
            RefreshColorsFromLayer(columns, rows, texWidth, texHeight);
        }

        // 应用填充纹理
        if (fillTexture != null)
        {
            fillTexture.SetPixels(fillCache);
            fillTexture.Apply(false);
        }
    }

    private void RefreshColorsFromSimulation(int columns, int rows, int texWidth, int texHeight)
    {
        // 计算拖拽偏移对应的格子偏移
        int offsetCol = Mathf.RoundToInt(m_dragOffsetX / cellSize);
        int offsetRow = Mathf.RoundToInt(m_dragOffsetY / cellSize);

        // 只偏移被拖拽物体自身范围内的格子
        bool hasOriginalBounds = m_isDragging && m_draggedObject != null;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                Color c = Color.clear;
                bool isBubble = false;
                if (m_grid[col, row] == CellState.Water)
                {
                    // 优先使用 LiquidSource 定义的自定义颜色，未定义则回退到默认 waterColor
                    c = m_liquidColorGrid[col, row];
                    if (c == Color.clear || c.a <= 0.001f)
                        c = waterColor;
                }
                else if (m_grid[col, row] == CellState.Bubble)
                {
                    // 气泡：先绘制背景水色，再叠加白色气泡圆点
                    c = waterColor;
                    isBubble = true;
                }
                else if (m_grid[col, row] == CellState.Obstacle)
                    c = obstacleColor;
                else
                    continue;

                // 判断该网格是否被被拖拽物体的实际碰撞器覆盖（使用拖拽开始时缓存的原始覆盖状态）
                bool shouldOffset = false;
                if (hasOriginalBounds && m_draggedCoverage != null)
                {
                    int index = col + row * m_columns;
                    if (index >= 0 && index < m_draggedCoverage.Length)
                        shouldOffset = m_draggedCoverage[index];
                }

                // 计算绘制位置
                int drawCol = shouldOffset ? col + offsetCol : col;
                int drawRow = shouldOffset ? row + offsetRow : row;

                // 确保在网格范围内
                if (drawCol < 0 || drawCol >= columns || drawRow < 0 || drawRow >= rows)
                    continue;

                int startX = drawCol * PIXELS_PER_CELL;
                int startY = drawRow * PIXELS_PER_CELL;

                if (isBubble)
                {
                    // 气泡：先填充水色背景，再在中心绘制一个较小的白色圆点
                    for (int py = 0; py < PIXELS_PER_CELL; py++)
                    {
                        int offset = (startY + py) * texWidth + startX;
                        Array.Fill(fillCache, c, offset, PIXELS_PER_CELL);
                    }
                    // 在格子中心绘制一个圆形的白色气泡（半径约为格子的 1/3）
                    float cx = PIXELS_PER_CELL * 0.5f;
                    float cy = PIXELS_PER_CELL * 0.5f;
                    float radius = PIXELS_PER_CELL * 0.35f;
                    for (int py = 0; py < PIXELS_PER_CELL; py++)
                    {
                        for (int px = 0; px < PIXELS_PER_CELL; px++)
                        {
                            float dx = px + 0.5f - cx;
                            float dy = py + 0.5f - cy;
                            if (dx * dx + dy * dy <= radius * radius)
                            {
                                int offset = (startY + py) * texWidth + (startX + px);
                                fillCache[offset] = bubbleColor;
                            }
                        }
                    }
                }
                else
                {
                    // Array.Fill 比嵌套循环更高效
                    for (int py = 0; py < PIXELS_PER_CELL; py++)
                    {
                        int offset = (startY + py) * texWidth + startX;
                        Array.Fill(fillCache, c, offset, PIXELS_PER_CELL);
                    }
                }
            }
        }
    }

    private void RefreshColorsFromLayer(int columns, int rows, int texWidth, int texHeight)
    {
        float originX = transform.position.x - gridWidth * 0.5f;
        float originY = transform.position.y - gridHeight * 0.5f;

        // 一次查询获取所有碰撞器
        Vector2 center = new Vector2(originX + gridWidth * 0.5f, originY + gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(gridWidth + cellSize, gridHeight + cellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_colliderBuffer);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                float worldX = originX + (col + 0.5f) * cellSize;
                float worldY = originY + (row + 0.5f) * cellSize;
                Vector2 worldPos = new Vector2(worldX, worldY);

                Color c = Color.clear;
                for (int i = 0; i < count; i++)
                {
                    var hit = m_colliderBuffer[i];
                    if (!hit.OverlapPoint(worldPos)) continue;

                    if (hit.gameObject.layer == m_waterLayer && m_waterLayer >= 0)
                    {
                        c = waterColor;
                        break;
                    }
                    if (IsObstacleLayer(hit.gameObject.layer) && !hit.isTrigger)
                    {
                        c = obstacleColor;
                        break;
                    }
                }
                if (c == Color.clear) continue;

                int startX = col * PIXELS_PER_CELL;
                int startY = row * PIXELS_PER_CELL;
                // Array.Fill 比嵌套循环更高效
                for (int py = 0; py < PIXELS_PER_CELL; py++)
                {
                    int offset = (startY + py) * texWidth + startX;
                    Array.Fill(fillCache, c, offset, PIXELS_PER_CELL);
                }
            }
        }
    }

    private void OnDestroy()
    {
        if (fillTexture != null) DestroyImmediate(fillTexture);
        if (fillSprite != null) DestroyImmediate(fillSprite);
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(LayerGridPainter))]
public class LayerGridPainterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        bool changed = EditorGUI.EndChangeCheck();

        EditorGUILayout.Space();

        if (changed)
        {
            ((LayerGridPainter)target).UpdateGrid();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Layer 检测状态", EditorStyles.boldLabel);

        int waterLayer = LayerMask.NameToLayer("Water");
        if (waterLayer >= 0)
            EditorGUILayout.LabelField($"Water: Layer {waterLayer}", EditorStyles.miniLabel);
        else
            EditorGUILayout.LabelField("Water: 未找到该 Layer", EditorStyles.miniLabel);

        string[] obstacleNames = ((LayerGridPainter)target).obstacleLayerNames;
        if (obstacleNames != null)
        {
            foreach (string name in obstacleNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0)
                    EditorGUILayout.LabelField($"{name}: Layer {layer}", EditorStyles.miniLabel);
                else
                    EditorGUILayout.LabelField($"{name}: 未找到该 Layer", EditorStyles.miniLabel);
            }
        }
    }
}
#endif
