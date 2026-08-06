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
/// 接触吸附规则：一条规则 = 载体标签 + 货物标签 + 接触传感器(碰撞体引用) + 判定参数。
/// 货物碰到载体的接触传感器（推荐：直接在 Inspector 把传感器碰撞体拖到 Sensor 字段；
/// 也可留空让其按 sensorChildName / 首个子碰撞器自动查找）时，
/// 会被挂为载体的子物体，从而随载体一起移动（拖拽碰撞范围自动变为两者之和）；
/// 单独拖动货物则立即脱离，放回接触区会在“刚进入接触”的上升沿重新吸附。
/// 配置字段会序列化到 Inspector；运行时缓存字段标 [System.NonSerialized]，不进入序列化。
/// </summary>
[Serializable]
public class AttachRule
{
    [Tooltip("是否启用本规则。")]
    public bool enabled = true;

    [Tooltip("载体标签，例如 Tripod。系统会找到所有带此标签的物体作为载体。")]
    public string carrierTag = "Tripod";

    [Tooltip("货物标签，例如 Asbestos_Mesh。")]
    public string cargoTag = "Asbestos_Mesh";

    [Tooltip("直接在 Inspector 里把载体上的接触传感器碰撞体拖进来即可（推荐），不必填名字。")]
    public Collider2D sensor;

    [Tooltip("兜底：当上方未指定碰撞体时，按此子物体名称（应为 IsTrigger 碰撞器）在载体下查找；留空则自动取第一个子碰撞器。")]
    public string sensorChildName = "Trigger";

    [Tooltip("接触判定容差（世界单位）。略大于 0 可让“刚好搭在边缘”也算接触。")]
    public float touchTolerance = 0.05f;

    [Tooltip("当货物自身被拖动时，是否立即脱离载体（默认开启，使货物可独立移动）。")]
    public bool detachWhenCargoDragged = true;

    // ---- 运行时缓存（不序列化）----
    [System.NonSerialized] public List<GameObject> carriers = new List<GameObject>();
    [System.NonSerialized] public List<GameObject> cargos = new List<GameObject>();
    [System.NonSerialized] public Dictionary<GameObject, Collider2D> sensorCache = new Dictionary<GameObject, Collider2D>();
    [System.NonSerialized] public Dictionary<GameObject, bool> prevTouching = new Dictionary<GameObject, bool>();
    [System.NonSerialized] public int lastCarrierCount = -1;
    [System.NonSerialized] public int lastCargoCount = -1;
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

    [Header("接触吸附规则")]
    [Tooltip("载体标签 ↔ 货物标签 的吸附规则表。每条登记一对：货物碰到载体顶部接触传感器即挂为子物体，随载体移动。" +
             "例如 carrierTag=Tripod, cargoTag=Asbestos_Mesh，并把三脚架顶部的 Trigger 碰撞体直接拖到 Sensor 字段。")]
    public List<AttachRule> attachRules = new List<AttachRule>();

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

            // 接触吸附规则：处理“货物碰到载体即挂为子物体、随载体移动、单独拖则脱开”
            EvaluateAttachRules();

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
                            // 倒进液体后，把原本 none 的空容器同步成对应液体类型
                            m_queries.SyncEmptyContainerTypes();
                        }
                    }
                }
                else
                {
                    // 普通拖拽：只冻结被拖拽容器内部的液体（WaterSimulator 按 m_draggedCoverage 跳过这些格子），
                    // 场景其余部分的水/气泡照常模拟——不再整体暂停物理。
                    m_updateTimer += Time.deltaTime;
                    if (m_updateTimer >= updateInterval)
                    {
                        m_updateTimer = 0f;
                        if (enableSimulation)
                        {
                            int steps = Mathf.Max(1, simulationStepsPerTick);
                            for (int i = 0; i < steps; i++)
                                m_simulator.ProcessWaterSimulation();
                        }
                        // 注意：普通拖拽期间【不】调用 SyncEmptyContainerTypes。
                        // 被拖容器里的水被锁在旧网格位置，但其 LiquidSource 的 region 碰撞器已随物体移到新位置，
                        // RegionHasWater 会误判「区域内无水」，连续若干 tick 后把容器类型错误回退成 "none"（标签变 none）。
                        // 普通拖拽时本就没有水真正流动/倒入，none↔类型 归类不会发生，无需同步；
                        // 倾倒（需归类被倒进的液体）与空闲时仍照常同步。
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
                            // 倒进液体后，把原本 none 的空容器同步成对应液体类型
                            m_queries.SyncEmptyContainerTypes();
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

    #region 接触吸附规则（载体 + 货物）

    /// <summary>
    /// 每帧遍历所有吸附规则并求值。把货物挂到载体下成为子物体，使拖/推载体时货物随之移动、
    /// 拖拽碰撞范围变为两者之和；单独拖货物则立即脱离；放回接触区在“刚进入接触”的上升沿重新吸附。
    /// </summary>
    private void EvaluateAttachRules()
    {
        if (attachRules == null || attachRules.Count == 0) return;
        foreach (var rule in attachRules)
        {
            if (rule != null && rule.enabled) EvaluateAttachRule(rule);
        }
    }

    private void EvaluateAttachRule(AttachRule rule)
    {
        GameObject[] carriersArr;
        GameObject[] cargosArr;
        try
        {
            carriersArr = GameObject.FindGameObjectsWithTag(rule.carrierTag);
            cargosArr = GameObject.FindGameObjectsWithTag(rule.cargoTag);
        }
        catch (UnityException)
        {
            Debug.LogError($"[LayerGridPainter] 吸附规则中的标签 “{rule.carrierTag}” 或 “{rule.cargoTag}” 未在 TagManager 注册，已停用该规则。", this);
            rule.enabled = false;
            return;
        }

        // 仅在载体 / 货物数量变化时重新扫描（兼顾运行中实例化新物体），并清掉失效的传感器缓存
        if (carriersArr.Length != rule.lastCarrierCount)
        {
            rule.carriers = new List<GameObject>(carriersArr);
            rule.lastCarrierCount = carriersArr.Length;
            rule.sensorCache.Clear();
        }
        if (cargosArr.Length != rule.lastCargoCount)
        {
            rule.cargos = new List<GameObject>(cargosArr);
            rule.lastCargoCount = cargosArr.Length;
        }

        if (rule.carriers.Count == 0 || rule.cargos.Count == 0) return;

        foreach (var cargo in rule.cargos)
        {
            if (cargo == null) continue;

            // 当前是否已吸附（父物体是某个载体）
            Transform parent = cargo.transform.parent;
            GameObject attachedCarrier = (parent != null) ? parent.gameObject : null;
            bool isAttached = attachedCarrier != null && rule.carriers.Contains(attachedCarrier);

            // 找正在接触（或容差内）的载体
            GameObject touchingCarrier = null;
            foreach (var carrier in rule.carriers)
            {
                Collider2D sensor = GetAttachSensor(rule, carrier);
                if (sensor != null && IsCargoTouching(cargo, sensor, rule.touchTolerance))
                {
                    touchingCarrier = carrier;
                    break;
                }
            }

            bool beingDragged = rule.detachWhenCargoDragged
                                && m_isDragging
                                && m_draggedObject == cargo;

            if (isAttached)
            {
                // 已吸附：货物被单独拖动、或离开接触区 -> 脱离
                if (beingDragged || touchingCarrier == null) DetachCargo(cargo);
            }
            else
            {
                // 未吸附：只要货物接触载体且当前没被拖动就吸附。
                // 拖动中 beingDragged 为真 → 不会吸附；松手后才会吸附，避免拖动时抖动。
                // 注意：不再要求“刚进入接触区的上升沿(!prev)”——否则手动把货物拖到载体上方松手后，
                // prevTouching 已被上一帧（拖动中）置为 true，吸附条件永远不满足，货物永远吸不上。
                if (touchingCarrier != null && !beingDragged) AttachCargo(cargo, touchingCarrier);
            }

            rule.prevTouching[cargo] = touchingCarrier != null;
        }
    }

    /// <summary>取载体上的接触传感器：优先用规则直接指定的碰撞体引用，否则按 sensorChildName 找子物体，再否则取第一个子碰撞器。</summary>
    private Collider2D GetAttachSensor(AttachRule rule, GameObject carrier)
    {
        if (rule.sensorCache.TryGetValue(carrier, out var cached) && cached != null) return cached;

        Collider2D sensor = null;

        // 1) 规则直接指定的碰撞体引用：仅当它属于当前载体（或载体子孙）时才采用
        if (rule.sensor != null
            && (rule.sensor.gameObject == carrier || rule.sensor.transform.IsChildOf(carrier.transform)))
        {
            sensor = rule.sensor;
        }

        // 2) 兜底：按名字找子物体
        if (sensor == null && !string.IsNullOrEmpty(rule.sensorChildName))
        {
            var t = carrier.transform.Find(rule.sensorChildName);
            if (t != null) sensor = t.GetComponent<Collider2D>();
        }

        // 3) 再兜底：第一个子碰撞器（优先 Trigger）
        if (sensor == null)
        {
            Collider2D firstAny = null;
            foreach (var c in carrier.GetComponentsInChildren<Collider2D>())
            {
                if (c == null || c.gameObject == carrier) continue; // 跳过载体自身碰撞器
                if (firstAny == null) firstAny = c;
                if (c.isTrigger) { sensor = c; break; }
            }
            if (sensor == null) sensor = firstAny;
        }

        rule.sensorCache[carrier] = sensor;
        return sensor;
    }

    private static bool IsCargoTouching(GameObject cargo, Collider2D sensor, float tol)
    {
        if (sensor == null) return false;
        foreach (var c in cargo.GetComponentsInChildren<Collider2D>())
        {
            if (c == null || c.isTrigger) continue;
            if (Physics2D.IsTouching(c, sensor)) return true;
            var d = Physics2D.Distance(c, sensor);
            if (d.isValid && d.distance < tol) return true;
        }
        return false;
    }

    private static void AttachCargo(GameObject cargo, GameObject carrier)
    {
        // 保留世界变换地把货物挂到载体之下；之后载体移动会带动货物，拖拽碰撞范围也变为两者之和
        cargo.transform.SetParent(carrier.transform, true);
    }

    private static void DetachCargo(GameObject cargo)
    {
        // 保留世界变换地脱离，使货物留在当前位置、可自由移动
        cargo.transform.SetParent(null, true);
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
