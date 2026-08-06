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
/// 水网格模拟器（协调器）——基于网格的水流动模拟。
/// 检测场景中 Water Layer 的物体作为初始水源，水受到重力自然往下流；
/// 遇到 Obstacle Layer 的物体停止向下流动，可以向左/右流动。
///
/// 本类只负责协调：持有网格数据底座 LiquidGrid 与 6 个独立子系统
/// （LiquidVisualizer / WaterSimulator / BubbleSystem / LiquidRegionQueries /
/// ObjectGridTracker / DragController），并把公开 API 委托给对应子系统。
/// 与渲染共享的少量拖拽状态（m_isDragging / m_draggedObject / m_dragOffsetX/Y /
/// m_draggedCoverage）保留在此（internal），供渲染器读取以做视觉偏移。
/// </summary>
[ExecuteInEditMode]
public class LayerGridPainter : MonoBehaviour
{
    [Header("网格设置")]
    [Tooltip("每个单元格的边长（世界单位）")]
    public float cellSize = 1.0f;

    [Tooltip("网格总宽度（世界单位）")]
    public float gridWidth = 10f;

    [Tooltip("网格总高度（世界单位）")]
    public float gridHeight = 10f;

    [Header("颜色设置")]
    public Color waterColor = ChemistryConstants.DefaultLiquidColor;
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
    public string[] obstacleLayerNames = { "Obstacle", "Equipment" };

    // ---------- 共享网格数据底座 ----------
    internal LiquidGrid m_gridData;

    /// <summary>网格数据底座（子系统通过它读写元胞/颜色/几何）。</summary>
    internal LiquidGrid gridData => m_gridData;

    // ---------- 子系统实例 ----------
    private LiquidVisualizer m_visualizer;
    private WaterSimulator m_simulator;
    private BubbleSystem m_bubbles;
    private LiquidRegionQueries m_queries;
    private ObjectGridTracker m_tracker;
    private DragController m_dragger;

    // ---------- 与渲染共享的拖拽状态（渲染器据此做视觉偏移）----------
    internal bool m_isDragging = false;
    internal GameObject m_draggedObject;
    internal float m_dragOffsetX = 0;
    internal float m_dragOffsetY = 0;
    internal bool[] m_draggedCoverage;

    // ---------- 跨子系统共享的设施与脏标记 ----------
    internal Collider2D[] m_colliderBuffer = new Collider2D[128];
    internal ContactFilter2D m_contactFilter;
    internal LiquidSource[] m_cachedLiquidSources;
    internal Collider2D[] m_containerInteriorColliders = null;
    internal int m_waterLayer = -1;
    internal int m_obstacleLayerMask = 0;
    internal bool m_sceneDirty = true;
    internal bool m_isDirty;
    internal Camera m_cachedCamera;

    internal HashSet<string> m_draggableTagSet;
    internal HashSet<string> m_penetrableTagSet;
    internal HashSet<string> m_impassableTagSet;

    // 更新计时器与位置基准（仅协调器 Update 使用）
    private float m_updateTimer;
    private Vector3 m_lastPainterPos;
    private Vector3 m_lastDraggedPos;
    private Quaternion m_lastDraggedRot;

    /// <summary>
    /// 当前是否正在拖拽物体
    /// </summary>
    public bool IsDragging => m_isDragging;

    /// <summary>
    /// 当前是否正在旋转被拖拽的物体（右键 90° 旋转）
    /// </summary>
    public bool IsRotating => m_dragger != null && m_dragger.IsRotating;

    /// <summary>
    /// 当前是否正在倾倒液体
    /// </summary>
    public bool IsPouring => m_dragger != null && m_dragger.IsPouring;

    /// <summary>
    /// 当前被拖拽的物体（未拖拽时为 null）
    /// </summary>
    public GameObject DraggedObject => m_draggedObject;

    /// <summary>
    /// 全局单例引用（懒加载并缓存）。避免多个 HeatableObject / FlammableObject / LiquidVolumeUI
    /// 在各自 Start/Awake 中重复调用 FindObjectOfType。
    /// </summary>
    private static LayerGridPainter s_instance;
    public static LayerGridPainter Instance
    {
        get
        {
            if (s_instance == null)
                s_instance = FindObjectOfType<LayerGridPainter>();
            return s_instance;
        }
    }

    #region 生命周期 (Awake / OnEnable / Update)

    /// <summary>
    /// 惰性初始化所有子系统。OnEnable 可能在 Awake 之前触发（例如编辑器中启用组件、
    /// 或某些激活顺序），此时子系统尚未分配，必须在访问前确保它们已就绪。
    /// 所有分配都做了 null 守卫，可重复安全调用。
    /// </summary>
    private void EnsureSubsystems()
    {
        if (m_gridData == null) m_gridData = new LiquidGrid();
        CacheLayerIndices();
        RebuildDraggableTagSet();
        if (m_visualizer == null) m_visualizer = new LiquidVisualizer(this);
        if (m_simulator == null) m_simulator = new WaterSimulator(this);
        if (m_bubbles == null) m_bubbles = new BubbleSystem(this);
        if (m_queries == null) m_queries = new LiquidRegionQueries(this);
        if (m_tracker == null) m_tracker = new ObjectGridTracker(this);
        if (m_dragger == null) m_dragger = new DragController(this);
        // ContactFilter2D 是 struct（值类型），永远不为 null；无条件初始化即可。
        m_contactFilter = new ContactFilter2D();
    }

    /// <summary>
    /// 公开 API 入口的惰性守卫：若子系统尚未分配（例如 LiquidVolumeUI.Awake 早于本组件
    /// Awake 时通过单例抢先调用），则补一次初始化。最多触发一次，后续调用直接跳过。
    /// </summary>
    private void EnsureReady()
    {
        if (m_visualizer == null) EnsureSubsystems();
    }

    private void Awake()
    {
        EnsureSubsystems();
        InitializeGrid();
    }

    private void OnEnable()
    {
        EnsureSubsystems();
        m_visualizer.CreateVisualizerObject(transform);
        InitializeGrid();
        m_visualizer.Rebuild(gridWidth, gridHeight);
    }

    // 注意：容量标签（LiquidVolumeUI）完全由 LiquidVolumeUI 自身负责，
    // LayerGridPainter 不再自动创建/控制标签，故此处没有 Start 逻辑。

    private void Update()
    {
        if (Application.isPlaying && m_gridData != null && m_gridData.Cells != null)
        {
            m_dragger.HandleDrag();

            // 检测场景是否真的移动（拖拽物体位移/旋转，或网格本体移动）。
            // 仅在移动时才需要重新扫描障碍物；静止时 UpdateGridFromObjects 会跳过昂贵的逐格 OverlapPoint 重检。
            if (transform.position != m_lastPainterPos)
            {
                m_sceneDirty = true;
                m_lastPainterPos = transform.position;
            }
            if (m_draggedObject != null)
            {
                var dt = m_draggedObject.transform;
                if (dt.position != m_lastDraggedPos || dt.rotation != m_lastDraggedRot)
                {
                    m_sceneDirty = true;
                    m_lastDraggedPos = dt.position;
                    m_lastDraggedRot = dt.rotation;
                }
            }

            if (m_isDragging && m_draggedObject != null)
            {
                if (m_dragger.IsPouring)
                {
                    m_updateTimer += Time.deltaTime;
                    if (m_updateTimer >= updateInterval)
                    {
                        m_updateTimer = 0f;
                        if (trackObjectMovement)
                            m_tracker.UpdateGridFromObjects();
                        if (enableSimulation)
                        {
                            int steps = Mathf.Max(1, simulationStepsPerTick);
                            for (int i = 0; i < steps; i++)
                                m_simulator.ProcessWaterSimulation();
                        }
                    }
                }
                m_isDirty = true;
            }
            else if (!Input.GetMouseButton(0))
            {
                m_dragOffsetX = 0;
                m_dragOffsetY = 0;

                // 右键拖拽旋转期间：暂停水物理模拟与障碍重检，让杯内液体随杯体刚性旋转（不流动、不倒）
                if (!m_dragger.IsRotating)
                {
                    m_updateTimer += Time.deltaTime;
                    if (m_updateTimer >= updateInterval)
                    {
                        m_updateTimer = 0f;

                        if (trackObjectMovement)
                        {
                            m_tracker.UpdateGridFromObjects();
                        }

                        if (enableSimulation)
                        {
                            int steps = Mathf.Max(1, simulationStepsPerTick);
                            for (int i = 0; i < steps; i++)
                            {
                                m_simulator.ProcessWaterSimulation();
                            }
                        }
                    }
                }
            }
        }

        // 只在脏标记时刷新纹理
        if (m_isDirty)
        {
            m_visualizer.RefreshColors();
            m_isDirty = false;
        }
    }

    #endregion

    #region 图层与 Tag 配置 / 缓存

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
    internal bool IsObstacleLayer(int layer)
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
    internal Camera GetCachedCamera()
    {
        if (m_cachedCamera == null)
            m_cachedCamera = Camera.main;
        return m_cachedCamera;
    }

    /// <summary>
    /// 缓存场景中的 LiquidSource 列表（初始化时调用一次，避免反复 FindObjectsOfType）。
    /// 之后所有需要 LiquidSource 的逻辑都复用本缓存。
    /// </summary>
    internal void CacheLiquidSources()
    {
        m_cachedLiquidSources = FindObjectsOfType<LiquidSource>();
    }

    #endregion

    #region 网格初始化

    private void InitializeGrid()
    {
        if (cellSize <= 0 || gridWidth <= 0 || gridHeight <= 0) return;

        int columns = Mathf.CeilToInt(gridWidth / cellSize);
        int rows = Mathf.CeilToInt(gridHeight / cellSize);

        m_gridData.OriginX = transform.position.x - gridWidth * 0.5f;
        m_gridData.OriginY = transform.position.y - gridHeight * 0.5f;
        m_gridData.CellSize = cellSize;
        m_gridData.Allocate(columns, rows);
        m_gridData.Clear();

        // Array.Clear 比嵌套循环快
        CacheLiquidSources();
        m_tracker.Initialize();
        m_isDirty = true;
        // 初始化脏标记：首帧需要扫描障碍物；并记录初始位置基准
        m_sceneDirty = true;
        m_lastPainterPos = transform.position;
        m_lastDraggedPos = Vector3.zero;
        m_lastDraggedRot = Quaternion.identity;
    }

    #endregion

    #region 公开 API（委托给子系统）

    /// <summary>
    /// 从指定碰撞体区域内移除一格水（蒸发）。委托给 BubbleSystem。
    /// </summary>
    public bool RemoveWaterFromRegion(Collider2D[] regionColliders)
    {
        EnsureReady();
        return m_bubbles.RemoveWaterFromRegion(regionColliders);
    }

    /// <summary>
    /// 在指定区域内批量生成气泡。委托给 BubbleSystem。
    /// </summary>
    public void SpawnBubblesInRegion(Collider2D[] regionColliders, int count)
    {
        EnsureReady();
        m_bubbles.SpawnBubblesInRegion(regionColliders, count);
    }

    /// <summary>
    /// 统计指定碰撞体区域内的水格总数。委托给 LiquidRegionQueries。
    /// </summary>
    public int GetWaterCountInRegion(Collider2D[] regionColliders)
    {
        EnsureReady();
        return m_queries.GetWaterCountInRegion(regionColliders);
    }

    /// <summary>
    /// 统计指定碰撞体区域内的容量格总数。委托给 LiquidRegionQueries。
    /// </summary>
    public int GetCellCountInRegion(Collider2D[] regionColliders)
    {
        EnsureReady();
        return m_queries.GetCellCountInRegion(regionColliders);
    }

    /// <summary>
    /// 运行时向容器注入液体。委托给 LiquidRegionQueries。
    /// </summary>
    public void FillContainer(GameObject container, Color? color = null)
    {
        EnsureReady();
        m_queries.FillContainer(container, color);
    }

    #endregion

    #region 纹理重建 / 资源释放

    /// <summary>
    /// 纹理为空时由渲染器回调：重建填充纹理（委托给 LiquidVisualizer）。
    /// </summary>
    internal void RebuildGrid()
    {
        m_visualizer.Rebuild(gridWidth, gridHeight);
    }

    /// <summary>
    /// 拖拽松手时把 FillArea 子物体复位到本地原点（委托给 LiquidVisualizer）。
    /// </summary>
    internal void ResetFillObject()
    {
        m_visualizer.ResetFillTransform();
    }

    /// <summary>
    /// 重新初始化网格与纹理（编辑器修改参数后调用）。
    /// </summary>
    public void UpdateGrid()
    {
        EnsureReady();
        m_visualizer.DestroySprite();
        m_visualizer.ResetCache();
        InitializeGrid();
        m_visualizer.Rebuild(gridWidth, gridHeight);
    }

    private void OnDestroy()
    {
        m_visualizer.Release();
    }

    #endregion

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
}
