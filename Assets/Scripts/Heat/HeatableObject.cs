using UnityEngine;
using Chemistry;

/// <summary>
/// 可加热容器：检测下方火焰热源，维护**整个容器共享的一个温度**，并同时驱动液相与固相的热行为。
///
/// 【固液合一】一个容器只需要这一个加热组件。它同时管：
///   - 液相（LiquidSource）：升温 → 沸点 → 沸腾蒸发 + 气泡；此外常温下也会缓慢挥发。
///   - 固相（SolidSource）：升温 → 相变温度 → 熔化 / 升华 / 分解（优先级 分解 &gt; 升华 &gt; 熔化）。
///
/// 挂载到容器上（与 LiquidSource / SolidSource 同物体，通常是 LiquidRegion 子物体），需配置：
///   - liquidSource：容器内液体的 LiquidSource（留空自动取同物体）
///   - solidSource：容器内固体的 SolidSource（留空自动取同物体，没有固体源也能正常工作）
///   - heatDetectCollider：向下检测热源的碰撞体（须探出容器底 1.2~1.5 单位）
///
/// 【水浴限温】容器里只要还有「别的液体」，温度就锁在该液体沸点附近（沸腾吸热）。
/// 所以往沸水里扔食盐只会溶解、不会熔化；等水蒸干了温度才继续爬升到固体的相变温度。
/// 固体自己熔化出来的熔体不算「别的液体」，不会限制自身继续熔化。
///
/// 液体理化性质优先从 ChemicalReagent 数据库查询（boilingPoint / boilingEvaporationRate / evaporationRate），
/// 固体相变温度取自 ChemicalReagent（meltingPoint / sublimationPoint / decompositionTemp / decompositionProductName）。
/// 两者都支持运行时更换内容物后自动重新读取。
/// </summary>
public class HeatableObject : MonoBehaviour
{
    [Header("内容物设置")]
    [Tooltip("容器内液体的 LiquidSource（留空则自动取同物体；为空则不进行蒸发）")]
    [SerializeField] private LiquidSource liquidSource;

    [Tooltip("容器内固体的 SolidSource（留空则自动取同物体；为空则不进行固体相变）")]
    [SerializeField] private SolidSource solidSource;

    [Header("加热检测")]
    [Tooltip("用于检测下方热源的碰撞体（建议在容器底部设置一个触发器碰撞体，须探出容器底 1.2~1.5 单位）")]
    [SerializeField] private Collider2D heatDetectCollider;

    /// <summary>本容器的热源感应碰撞体（只读）。</summary>
    public Collider2D HeatDetectCollider => heatDetectCollider;

    /// <summary>本容器关联的液体源（只读）。</summary>
    public LiquidSource AttachedLiquidSource => liquidSource;

    /// <summary>本容器关联的固体源（只读）。</summary>
    public SolidSource AttachedSolidSource => solidSource;

    [Header("数据库设置")]
    [Tooltip("是否从数据库查询内容物的理化性质（沸点、挥发性、相变温度等）")]
    [SerializeField] private bool useDatabaseProperties = true;

    [Header("温度参数")]
    [Tooltip("初始温度（°C）")]
    [SerializeField] private float initialTemperature = 25f;

    [Tooltip("默认沸点（°C），若数据库查询失败则使用此值")]
    [SerializeField] private float defaultBoilingPoint = 100f;

    [Tooltip("被加热时的升温速率（°C/秒）")]
    [SerializeField] private float heatingRate = 15f;

    [Tooltip("无热源时的冷却速率（°C/秒）")]
    [SerializeField] private float coolingRate = 5f;

    [Tooltip("停止加热后，因余热维持沸腾/相变的持续时间（秒）。期间温度保持不降，气泡维持峰值；结束后才开始降温")]
    [SerializeField] private float residualHeatDuration = 3f;

    [Tooltip("水浴限温：容器内存在其它液体时，温度锁在该液体沸点附近，固体因此不会熔化（只会溶解）。关闭后固体可无视液体直接升温至相变温度")]
    [SerializeField] private bool liquidLimitsTemperature = true;

    [Header("蒸发参数")]
    [Tooltip("默认沸腾蒸发速率（格/秒）。仅当数据库关闭、或液体数据中 boilingEvaporationRate 字段为 NaN（未设置）时作为兜底；否则以液体数据中的 boilingEvaporationRate 字段为准")]
    [SerializeField] private float defaultEvaporationRate = 0.5f;

    [Tooltip("默认常温蒸发速率（格/秒）。液体在室温、无加热时的缓慢挥发速度。仅当数据库关闭、或液体数据中 evaporationRate 字段为 NaN（未设置）时作为兜底；否则以液体数据中的 evaporationRate 字段为准。设为 0 可关闭常温蒸发")]
    [SerializeField] private float defaultRoomTempEvaporationRate = 0.05f;

    [Header("气泡参数")]
    [Tooltip("沸腾及加热过程中，单批最多生成的气泡数（会乘以加热进度 0~1）")]
    [SerializeField] private int maxExtraBubblesPerTick = 6;
    [Tooltip("加热过程中每秒生成气泡批次的基础数量（会乘以加热进度 0~1）")]
    [SerializeField] private float extraBubbleBatchesPerSecond = 2.5f;

    [Header("固体相变参数")]
    [Tooltip("默认熔点（°C）。当固体试剂三个相变温度全部未设置时，用此值兜底一个熔点。设为 NaN 则固体永不相变")]
    [SerializeField] private float defaultMeltingPoint = 200f;
    [Tooltip("熔化速率（克/秒）")]
    [SerializeField] private float meltRate = 1f;
    [Tooltip("升华速率（克/秒）")]
    [SerializeField] private float sublimeRate = 1f;
    [Tooltip("分解速率（克/秒）")]
    [SerializeField] private float decomposeRate = 1f;
    [Tooltip("分解产物质量（克）：分解完成后产物固体的质量（简化，不考虑化学计量）")]
    [SerializeField] private float decomposeYield = 5f;
    [Tooltip("相变过程中每秒生成气泡批次的基础数量（升华/分解的气体逸出效果）")]
    [SerializeField] private float phaseBubbleBatchesPerSecond = 3f;
    [Tooltip("相变过程每批气泡数")]
    [SerializeField] private int phaseBubblesPerBatch = 3;

    // ===== 运行时状态（容器共享）=====
    private float m_currentTemperature;
    private float m_evaporateTimer;
    private float m_roomEvapTimer;      // 常温蒸发计时器
    private float m_bubbleTimer;        // 液体气泡生成计时器
    private float m_phaseBubbleTimer;   // 固体相变气泡计时器
    private float m_residualTimer;      // 停止加热后的余热计时器（秒），>0 时维持沸腾/相变
    private bool m_isHeated;
    private LayerGridPainter m_gridPainter;
    private Collider2D[] m_heatDetectBuffer = new Collider2D[16];
    private ContactFilter2D m_heatContactFilter;

    // 液体数据库缓存
    private ChemicalReagent m_currentReagent;
    private string m_lastLiquidType;
    private float m_effectiveBoilingPoint;
    private float m_effectiveEvaporationRate;   // 沸腾蒸发速率（格/秒）
    private float m_effectiveRoomTempRate;      // 常温蒸发速率（格/秒）
    private bool m_canEvaporate;

    // 固体相变缓存
    private ChemicalReagent m_lastSolidReagent;
    private bool m_solidCanReact;
    private float m_effMelt = float.NaN;
    private float m_effSub = float.NaN;
    private float m_effDec = float.NaN;
    private float m_solidMaxTemp;

    /// <summary>当前温度（°C）——液相与固相共享同一个温度。</summary>
    public float CurrentTemperature => m_currentTemperature;

    /// <summary>是否正在被加热（下方有活跃火焰）。</summary>
    public bool IsHeated => m_isHeated;

    /// <summary>
    /// 是否正在蒸发：沸腾蒸发（加热到沸点且有热源/余热）或常温蒸发（有液体且常温速率>0）任一成立即为真
    /// </summary>
    public bool IsEvaporating
    {
        get
        {
            if (!m_canEvaporate) return false;
            // 沸腾蒸发：温度达到沸点且有（余热）热源
            if (m_currentTemperature >= m_effectiveBoilingPoint && HasActiveHeat)
                return true;
            // 常温蒸发：有液体且常温蒸发速率 > 0（无需加热）
            if (m_effectiveRoomTempRate > 0f && liquidSource != null && !liquidSource.IsEmpty())
                return true;
            return false;
        }
    }

    /// <summary>是否正在发生固体相变（熔化/升华/分解任一）。</summary>
    public bool IsPhaseChanging
    {
        get
        {
            if (!m_solidCanReact || solidSource == null || solidSource.GetMass() <= 0f) return false;
            return m_currentTemperature >= GetActivePhaseThreshold();
        }
    }

    /// <summary>当前液体的数据库条目</summary>
    public ChemicalReagent CurrentReagent => m_currentReagent;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Start()
    {
        m_gridPainter = LayerGridPainter.Instance;
        m_currentTemperature = initialTemperature;
        m_evaporateTimer = 0f;
        m_roomEvapTimer = 0f;

        m_heatContactFilter = new ContactFilter2D();
        m_heatContactFilter.useTriggers = true;
        m_heatContactFilter.SetLayerMask(Physics2D.AllLayers);

        ResolveReferences();
        UpdateLiquidProperties();
        UpdateSolidProperties();
    }

    /// <summary>
    /// 自动补齐内容物引用。固液同体约定：LiquidSource 与 SolidSource 都在本物体上，
    /// 所以留空即可，不需要在 Inspector 里手动拖。
    /// </summary>
    private void ResolveReferences()
    {
        if (liquidSource == null)
            liquidSource = GetComponent<LiquidSource>();
        if (solidSource == null)
            solidSource = GetComponent<SolidSource>();
        // 兼容旧布局：加热组件与内容物不在同一物体上时，从已解析到的液体源上取固体源
        if (solidSource == null && liquidSource != null)
            solidSource = liquidSource.GetComponent<SolidSource>();
    }

    private void Update()
    {
        if (m_gridPainter == null)
        {
            m_gridPainter = LayerGridPainter.Instance;
            if (m_gridPainter == null) return;
        }

        // 固体源可能是后来才挂上/实例化出来的，做一次惰性回补
        if (solidSource == null) ResolveReferences();
        if (liquidSource == null && solidSource == null) return;

        // 1. 内容物变化检测：液体换了、或容器里被投放了新固体，都要重新读理化性质
        string currentType = GetCurrentLiquidType();
        if (currentType != m_lastLiquidType)
        {
            m_lastLiquidType = currentType;
            UpdateLiquidProperties();
        }
        if (solidSource != null && solidSource.reagentData != m_lastSolidReagent)
            UpdateSolidProperties();

        bool hasLiquid = liquidSource != null && !liquidSource.IsEmpty();
        bool hasSolid = solidSource != null && !solidSource.IsEmpty();

        // 2. 检测下方是否有火焰热源
        //    完全空的容器（既无液体也无固体）无需检测，省去每帧的 OverlapCollider + GetComponent 开销
        m_isHeated = (hasLiquid || hasSolid) && DetectHeatSource();

        // 3. 更新温度（液相固相共享）
        UpdateTemperature(hasLiquid);

        // 4. 液相热行为
        if (liquidSource != null)
            UpdateLiquidPhase(hasLiquid);

        // 5. 固相热行为
        if (hasSolid)
            UpdateSolidPhase();
    }

    // ================= 液相 =================

    /// <summary>液相：沸腾蒸发 / 常温挥发 / 气泡。</summary>
    private void UpdateLiquidPhase(bool hasLiquid)
    {
        // 加热进度（0~1）：从室温升到沸点的过程，用于驱动气泡数量“逐渐变多”。
        // 温度到达沸点后恒为 1，因此气泡数量在沸腾时维持峰值、不再增多。
        float heatProgress = Mathf.Clamp01(
            (m_currentTemperature - initialTemperature) /
            Mathf.Max(0.1f, m_effectiveBoilingPoint - initialTemperature));

        // 沸腾判定：达到沸点、有（有效）热源、且液体可蒸发。
        // 余热期内仍视为有热源，沸腾与蒸发会继续维持一会儿。
        bool isBoiling = m_canEvaporate && m_currentTemperature >= m_effectiveBoilingPoint && HasActiveHeat;

        // (a) 沸腾蒸发：仅在沸腾时按沸腾蒸发速率匀速移除液体（伴随气泡，由下方 heatProgress 分支生成）
        if (isBoiling)
        {
            m_evaporateTimer += Time.deltaTime;
            float interval = 1f / m_effectiveEvaporationRate;
            while (m_evaporateTimer >= interval)
            {
                m_evaporateTimer -= interval;
                bool removed = m_gridPainter.RemoveWaterFromRegion(liquidSource.regionColliders, true);
                if (!removed)
                {
                    // 液体已蒸发完毕
                    m_evaporateTimer = 0f;
                    break;
                }
            }
        }
        else
        {
            m_evaporateTimer = 0f;
        }

        // (b) 常温蒸发：液体在室温、未沸腾时也会缓慢挥发（如酒精、水放置变少）。
        //     不生成气泡（spawnBubbles=false），速率取 m_effectiveRoomTempRate；沸腾时由 (a) 处理，避免重复。
        if (!isBoiling && m_canEvaporate && m_effectiveRoomTempRate > 0f && hasLiquid)
        {
            m_roomEvapTimer += Time.deltaTime;
            float interval = 1f / m_effectiveRoomTempRate;
            while (m_roomEvapTimer >= interval)
            {
                m_roomEvapTimer -= interval;
                bool removed = m_gridPainter.RemoveWaterFromRegion(liquidSource.regionColliders, false);
                if (!removed)
                {
                    m_roomEvapTimer = 0f;
                    break;
                }
            }
        }
        else
        {
            m_roomEvapTimer = 0f;
        }

        // (c) 气泡生成：
        //     气泡数量/频率随温度（heatProgress）变化——加热时逐渐增多，沸腾时维持峰值；
        //     关火后余热期内仍保持峰值，余热散尽降温时 heatProgress 回落，气泡逐渐减少到无。
        //     注意：不强制要求 m_isHeated，这样关火后气泡不会瞬间消失，而是平滑减退。
        if (hasLiquid && heatProgress > 0f)
        {
            m_bubbleTimer += Time.deltaTime;
            // 加热越充分，每秒生成的批次越多（间隔越短）
            float batchesPerSecond = extraBubbleBatchesPerSecond * heatProgress;
            float batchInterval = 1f / Mathf.Max(0.01f, batchesPerSecond);
            while (m_bubbleTimer >= batchInterval)
            {
                m_bubbleTimer -= batchInterval;
                // 每批气泡数随加热进度线性增长，沸腾时达到最大，降温时回到 0
                int count = Mathf.RoundToInt(heatProgress * maxExtraBubblesPerTick);
                if (count > 0)
                    m_gridPainter.SpawnBubblesInRegion(liquidSource.regionColliders, count);
            }
        }
        else
        {
            m_bubbleTimer = 0f;
        }
    }

    /// <summary>
    /// 获取当前液体类型（优先从 LiquidSource 获取）
    /// </summary>
    private string GetCurrentLiquidType()
    {
        if (liquidSource == null) return null;
        // 优先使用英文名
        if (liquidSource.reagentData != null && !string.IsNullOrEmpty(liquidSource.reagentData.englishName))
            return liquidSource.reagentData.englishName;
        if (liquidSource.reagentData != null && !string.IsNullOrEmpty(liquidSource.reagentData.reagentName))
            return liquidSource.reagentData.reagentName;
        if (!string.IsNullOrEmpty(liquidSource.liquidType))
            return liquidSource.liquidType;
        return null;
    }

    /// <summary>
    /// 根据当前液体类型更新理化性质（从数据库查询或回退到默认值）
    /// </summary>
    private void UpdateLiquidProperties()
    {
        m_currentReagent = null;
        m_effectiveBoilingPoint = defaultBoilingPoint;
        m_effectiveEvaporationRate = defaultEvaporationRate;       // 沸腾蒸发速率兜底
        m_effectiveRoomTempRate = defaultRoomTempEvaporationRate;  // 常温蒸发速率兜底
        m_canEvaporate = true;

        if (!useDatabaseProperties || !ChemistrySystem.IsReady)
            return;

        string liquidType = GetCurrentLiquidType();
        if (string.IsNullOrEmpty(liquidType))
        {
            m_canEvaporate = false;
            return;
        }

        // 从数据库查询试剂信息
        ChemicalReagent reagent = ChemistrySystem.FindReagent(liquidType);
        if (reagent != null)
        {
            m_currentReagent = reagent;

            // 沸点：数据库中有有效值则使用，否则使用默认值
            if (!float.IsNaN(reagent.boilingPoint))
                m_effectiveBoilingPoint = reagent.boilingPoint;

            // 沸腾蒸发速率：优先使用液体数据中的 boilingEvaporationRate 字段；
            // 未设置（NaN）时保留本组件的 defaultEvaporationRate 兜底值。
            if (!float.IsNaN(reagent.boilingEvaporationRate))
                m_effectiveEvaporationRate = reagent.boilingEvaporationRate;

            // 常温蒸发速率：优先使用液体数据中的 evaporationRate 字段；
            // 未设置（NaN）时保留本组件的 defaultRoomTempEvaporationRate 兜底值。
            if (!float.IsNaN(reagent.evaporationRate))
                m_effectiveRoomTempRate = reagent.evaporationRate;

            // 挥发性：易挥发液体至少保证一定蒸发速率（兜底下限）
            //   沸腾：≥1.5；常温：≥0.8（乙醇等易挥发液体在室温下也明显挥发）
            if (reagent.isVolatile)
            {
                m_effectiveEvaporationRate = Mathf.Max(m_effectiveEvaporationRate, 1.5f);
                m_effectiveRoomTempRate = Mathf.Max(m_effectiveRoomTempRate, 0.8f);
            }

            // 物态检查：如果不是液态（如固态），则不可蒸发
            if (reagent.defaultState != PhysicalState.Liquid)
                m_canEvaporate = false;
        }
        else
        {
            // 数据库中未找到该液体，回退到默认值，仍然允许蒸发
            Debug.LogWarning($"[HeatableObject] 数据库中未找到液体 '{liquidType}'，使用默认沸点 {defaultBoilingPoint}°C。");
        }
    }

    // ================= 固相 =================

    /// <summary>
    /// 根据固体关联试剂读取相变温度（熔点/升华点/分解点），并推算固体侧的温度上限。
    /// 运行时往容器投放/更换固体后会自动重新调用，也可由外部主动调用。
    /// </summary>
    public void UpdateSolidProperties()
    {
        m_solidCanReact = false;
        m_effMelt = float.NaN;
        m_effSub = float.NaN;
        m_effDec = float.NaN;
        m_lastSolidReagent = (solidSource != null) ? solidSource.reagentData : null;

        if (solidSource == null || solidSource.reagentData == null)
        {
            m_solidMaxTemp = initialTemperature;
            return;
        }

        ChemicalReagent r = solidSource.reagentData;
        if (!float.IsNaN(r.meltingPoint)) { m_effMelt = r.meltingPoint; m_solidCanReact = true; }
        if (!float.IsNaN(r.sublimationPoint)) { m_effSub = r.sublimationPoint; m_solidCanReact = true; }
        if (!float.IsNaN(r.decompositionTemp)) { m_effDec = r.decompositionTemp; m_solidCanReact = true; }

        float maxT = initialTemperature;
        if (!float.IsNaN(m_effMelt)) maxT = Mathf.Max(maxT, m_effMelt);
        if (!float.IsNaN(m_effSub)) maxT = Mathf.Max(maxT, m_effSub);
        if (!float.IsNaN(m_effDec)) maxT = Mathf.Max(maxT, m_effDec);
        m_solidMaxTemp = maxT + 20f;

        // defaultMeltingPoint 兜底：数据里三个相变温度都没填时，仍给一个默认熔点
        if (!m_solidCanReact && !float.IsNaN(defaultMeltingPoint))
        {
            m_effMelt = defaultMeltingPoint;
            m_solidCanReact = true;
            m_solidMaxTemp = Mathf.Max(m_solidMaxTemp, defaultMeltingPoint + 20f);
        }
    }

    /// <summary>当前温度下应触发的相变阈值（分解 &gt; 升华 &gt; 熔化中最先被越过的那个）。</summary>
    private float GetActivePhaseThreshold()
    {
        if (!float.IsNaN(m_effDec)) return m_effDec;
        if (!float.IsNaN(m_effSub)) return m_effSub;
        if (!float.IsNaN(m_effMelt)) return m_effMelt;
        return float.PositiveInfinity;
    }

    /// <summary>固相：按温度优先级调度 分解 &gt; 升华 &gt; 熔化。</summary>
    private void UpdateSolidPhase()
    {
        if (!m_solidCanReact || solidSource.GetMass() <= 0f) return;

        if (!float.IsNaN(m_effDec) && m_currentTemperature >= m_effDec)
            Decompose(Time.deltaTime);
        else if (!float.IsNaN(m_effSub) && m_currentTemperature >= m_effSub)
            Sublime(Time.deltaTime);
        else if (!float.IsNaN(m_effMelt) && m_currentTemperature >= m_effMelt)
            Melt(Time.deltaTime);
    }

    /// <summary>熔化：固体质量减少，同容器灌入熔体液体（体积增加），并同步液源类型为熔体。</summary>
    private void Melt(float dt)
    {
        float meltMass = Mathf.Min(meltRate * dt, solidSource.GetMass());
        if (meltMass <= 0f) return;

        Color meltColor = solidSource.GetEffectiveColor();
        Collider2D[] region = solidSource.GetRegionColliders();
        if (region != null)
            m_gridPainter.FillRegionWithLiquid(region, meltColor);

        // 同步同容器液源：若原本为空(none)，则标记为熔体液体（使 LiquidVolumeUI 显示液体名+体积）
        LiquidSource liquid = solidSource.GetPairedLiquidSource();
        if (liquid != null && liquid.IsEmpty())
        {
            liquid.isEmptyContainer = false;
            liquid.liquidType = (!string.IsNullOrEmpty(solidSource.reagentData.englishName))
                ? solidSource.reagentData.englishName : solidSource.reagentData.reagentName;
            liquid.reagentData = solidSource.reagentData;
            liquid.useReagentColor = true;
            liquid.liquidColor = meltColor;
        }

        solidSource.ReduceMass(meltMass);
        SpawnPhaseChangeBubbles();
    }

    /// <summary>升华：固体质量减少，喷气泡表示气体逸出（无液体生成）。</summary>
    private void Sublime(float dt)
    {
        float subMass = Mathf.Min(sublimeRate * dt, solidSource.GetMass());
        if (subMass <= 0f) return;
        solidSource.ReduceMass(subMass);
        SpawnPhaseChangeBubbles();
    }

    /// <summary>分解：固体质量减少，喷气泡表示气体产物逸出；固体耗尽后变为分解产物（若有）。</summary>
    private void Decompose(float dt)
    {
        float decMass = Mathf.Min(decomposeRate * dt, solidSource.GetMass());
        if (decMass <= 0f) return;
        solidSource.ReduceMass(decMass);
        SpawnPhaseChangeBubbles();

        // 固体耗尽且有分解产物：变为产物固体（保留产物质量）
        if (solidSource.GetMass() <= 0.0001f
            && solidSource.reagentData != null
            && !string.IsNullOrEmpty(solidSource.reagentData.decompositionProductName))
        {
            ChemicalReagent product = ChemistrySystem.FindReagent(solidSource.reagentData.decompositionProductName);
            if (product != null)
            {
                solidSource.reagentData = product;
                solidSource.solidType = (!string.IsNullOrEmpty(product.englishName))
                    ? product.englishName : product.reagentName;
                solidSource.useReagentColor = true;
                solidSource.mass = decomposeYield;
                solidSource.isEmptyContainer = false;
                // 产物可能也有自己的相变温度，重新读取
                UpdateSolidProperties();
            }
        }
    }

    private void SpawnPhaseChangeBubbles()
    {
        Collider2D[] region = solidSource.GetRegionColliders();
        if (region == null) return;
        m_phaseBubbleTimer += Time.deltaTime;
        float interval = 1f / Mathf.Max(0.01f, phaseBubbleBatchesPerSecond);
        if (m_phaseBubbleTimer >= interval)
        {
            m_phaseBubbleTimer = 0f;
            m_gridPainter.SpawnBubblesInRegion(region, phaseBubblesPerBatch);
        }
    }

    // ================= 热源与温度 =================

    /// <summary>
    /// 检测下方是否有活跃的火焰热源
    /// 使用 OverlapCollider 检测 heatDetectCollider 范围内的 FlammableObject 或 IgniterController
    /// </summary>
    private bool DetectHeatSource()
    {
        if (heatDetectCollider == null) return false;

        int count = Physics2D.OverlapCollider(heatDetectCollider, m_heatContactFilter, m_heatDetectBuffer);

        for (int i = 0; i < count; i++)
        {
            Collider2D other = m_heatDetectBuffer[i];
            if (other == null || other.transform.IsChildOf(transform)) continue;

            HeatComponentFinder.Find(other, out FlammableObject flammable, out IgniterController igniter);

            if (flammable != null && flammable.IsIgnited)
                return true;

            if (igniter != null && igniter.IsIgnited)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 计算当前温度上限。
    /// 水浴限温：容器里还有「别的液体」时，锁在该液体沸点附近（沸腾吸热，温度上不去）；
    /// 液体蒸干后，若容器内有可相变固体，上限才提升到固体相变温度以上。
    /// 固体自己熔出来的熔体不算「别的液体」，否则熔化会把自己卡住。
    /// </summary>
    private float GetTemperatureCap(bool hasLiquid)
    {
        float liquidCap = m_effectiveBoilingPoint + 5f;

        if (!m_solidCanReact) return liquidCap;

        if (liquidLimitsTemperature && hasLiquid && m_canEvaporate)
        {
            // 判断当前液体是不是本固体自身熔化产生的熔体
            bool isOwnMelt = solidSource != null
                && solidSource.reagentData != null
                && liquidSource != null
                && liquidSource.reagentData == solidSource.reagentData;
            if (!isOwnMelt)
                return liquidCap;
        }

        return Mathf.Max(liquidCap, m_solidMaxTemp);
    }

    /// <summary>
    /// 根据热源状态更新温度；停止加热后有一段余热期，期间温度维持不降，
    /// 余热散尽才开始自然冷却。
    /// </summary>
    private void UpdateTemperature(bool hasLiquid)
    {
        if (m_isHeated)
        {
            // 被加热：温度上升，但不超过当前上限
            float maxTemp = GetTemperatureCap(hasLiquid);
            m_currentTemperature = Mathf.Min(m_currentTemperature + heatingRate * Time.deltaTime, maxTemp);
            // 持续加热则把余热计时器保持在满值
            m_residualTimer = residualHeatDuration;
        }
        else if (m_residualTimer > 0f)
        {
            // 余热期：温度维持（模拟关火后液体仍沸腾、固体仍继续相变一会儿），不降温
            m_residualTimer = Mathf.Max(0f, m_residualTimer - Time.deltaTime);
        }
        else
        {
            // 余热散尽：温度自然冷却至室温
            float roomTemp = initialTemperature;
            if (m_currentTemperature > roomTemp)
                m_currentTemperature = Mathf.Max(m_currentTemperature - coolingRate * Time.deltaTime, roomTemp);
        }
    }

    /// <summary>
    /// 是否处于“有效加热”状态（火焰加热中，或刚关火还在余热期内）
    /// </summary>
    private bool HasActiveHeat => m_isHeated || m_residualTimer > 0f;
}
