using UnityEngine;
using Chemistry;

/// <summary>
/// 可加热物体：检测下方火焰热源，累积温度到沸点后开始蒸发液体。
/// 挂载到需要被加热的容器上（如 Beaker），需配置：
///   - liquidSource：容器内液体的 LiquidSource
///   - heatDetectCollider：向下检测热源的碰撞体（通常在容器底部）
/// 液体理化性质优先从 ChemicalReagent 数据库查询，支持液体更换时动态更新。
/// 升温阶段：检测到下方有火焰时温度上升，无火焰时自然冷却。
/// 气泡行为：加热过程中气泡数量随温度上升逐渐增多，到达沸点后维持峰值、不再增多。
/// 到达沸点后：维持温度并按蒸发速率匀速蒸发液体。
/// 液体蒸发完毕后：温度逐渐回落，停止蒸发。
/// </summary>
public class HeatableObject : MonoBehaviour
{
    [Header("液体设置")]
    [Tooltip("容器内液体的 LiquidSource（若为空则不进行蒸发）")]
    [SerializeField] private LiquidSource liquidSource;

    [Header("加热检测")]
    [Tooltip("用于检测下方热源的碰撞体（建议在容器底部设置一个触发器碰撞体）")]
    [SerializeField] private Collider2D heatDetectCollider;

    [Header("数据库设置")]
    [Tooltip("是否从数据库查询液体的理化性质（沸点、挥发性等）")]
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

    [Tooltip("停止加热后，因余热维持沸腾/气泡的持续时间（秒）。期间温度保持在沸点附近，气泡维持峰值；结束后才开始降温、气泡逐渐减少")]
    [SerializeField] private float residualHeatDuration = 3f;

    [Header("蒸发参数")]
    [Tooltip("默认蒸发速率（格/秒），若数据库标记为易挥发会适当提高")]
    [SerializeField] private float defaultEvaporationRate = 0.5f;

    [Header("气泡参数")]
    [Tooltip("沸腾及加热过程中，单批最多生成的气泡数（会乘以加热进度 0~1）")]
    [SerializeField] private int maxExtraBubblesPerTick = 6;
    [Tooltip("加热过程中每秒生成气泡批次的基础数量（会乘以加热进度 0~1）")]
    [SerializeField] private float extraBubbleBatchesPerSecond = 2.5f;

    // 运行时状态
    private float m_currentTemperature;
    private float m_evaporateTimer;
    private float m_bubbleTimer;        // 气泡生成计时器
    private float m_residualTimer;      // 停止加热后的余热计时器（秒），>0 时维持沸腾
    private bool m_isHeated;
    private bool m_wasHeated;
    private LayerGridPainter m_gridPainter;
    private Collider2D[] m_heatDetectBuffer = new Collider2D[16];
    private ContactFilter2D m_heatContactFilter;

    // 液体数据库缓存
    private ChemicalReagent m_currentReagent;
    private string m_lastLiquidType;
    private float m_effectiveBoilingPoint;
    private float m_effectiveEvaporationRate;
    private bool m_canEvaporate;

    /// <summary>
    /// 当前温度（°C）
    /// </summary>
    public float CurrentTemperature => m_currentTemperature;

    /// <summary>
    /// 是否正在被加热（下方有活跃火焰）
    /// </summary>
    public bool IsHeated => m_isHeated;

    /// <summary>
    /// 是否正在蒸发（温度已达到沸点、有液体、且处于加热或余热期内）
    /// </summary>
    public bool IsEvaporating => m_canEvaporate && m_currentTemperature >= m_effectiveBoilingPoint && HasActiveHeat;

    /// <summary>
    /// 当前液体的数据库条目
    /// </summary>
    public ChemicalReagent CurrentReagent => m_currentReagent;

    private void Start()
    {
        m_gridPainter = FindObjectOfType<LayerGridPainter>();
        m_currentTemperature = initialTemperature;
        m_evaporateTimer = 0f;

        m_heatContactFilter = new ContactFilter2D();
        m_heatContactFilter.useTriggers = true;
        m_heatContactFilter.SetLayerMask(Physics2D.AllLayers);

        // 初始化液体属性
        UpdateLiquidProperties();
    }

    private void Update()
    {
        if (liquidSource == null || m_gridPainter == null) return;

        // 1. 检测液体是否变化，变化则重新查询数据库
        string currentType = GetCurrentLiquidType();
        if (currentType != m_lastLiquidType)
        {
            m_lastLiquidType = currentType;
            UpdateLiquidProperties();
        }

        // 2. 检测下方是否有火焰热源
        m_isHeated = DetectHeatSource();

        // 3. 更新温度
        UpdateTemperature();

        // 4. 加热进度（0~1）：从室温升到沸点的过程，用于驱动气泡数量“逐渐变多”。
        //    温度到达沸点后恒为 1，因此气泡数量在沸腾时维持峰值、不再增多。
        float heatProgress = Mathf.Clamp01(
            (m_currentTemperature - initialTemperature) /
            Mathf.Max(0.1f, m_effectiveBoilingPoint - initialTemperature));

        // 5. 沸腾判定：达到沸点、有（有效）热源、且液体可蒸发。
        //    余热期内仍视为有热源，沸腾与蒸发会继续维持一会儿。
        bool isBoiling = m_canEvaporate && m_currentTemperature >= m_effectiveBoilingPoint && HasActiveHeat;

        // 6. 蒸发液体：仅在沸腾时按蒸发速率匀速移除液体
        if (isBoiling)
        {
            m_evaporateTimer += Time.deltaTime;
            float interval = 1f / m_effectiveEvaporationRate;
            while (m_evaporateTimer >= interval)
            {
                m_evaporateTimer -= interval;
                bool removed = m_gridPainter.RemoveWaterFromRegion(liquidSource.regionColliders);
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

        // 7. 气泡生成：
        //    气泡数量/频率随温度（heatProgress）变化——加热时逐渐增多，沸腾时维持峰值；
        //    关火后余热期内仍保持峰值，余热散尽降温时 heatProgress 回落，气泡逐渐减少到无。
        //    注意：不再强制要求 m_isHeated，这样关火后气泡不会瞬间消失，而是平滑减退。
        if (heatProgress > 0f)
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

        m_wasHeated = m_isHeated;
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
        m_effectiveEvaporationRate = defaultEvaporationRate;
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

            // 挥发性：易挥发的液体蒸发速率更高
            if (reagent.isVolatile)
                m_effectiveEvaporationRate = Mathf.Max(m_effectiveEvaporationRate, 1.5f);

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

            // 检测 FlammableObject（如酒精灯）
            FlammableObject flammable = other.GetComponent<FlammableObject>();
            if (flammable == null) flammable = other.GetComponentInParent<FlammableObject>();

            if (flammable != null && flammable.IsIgnited)
                return true;

            // 检测 IgniterController（点火器）
            IgniterController igniter = other.GetComponent<IgniterController>();
            if (igniter == null) igniter = other.GetComponentInParent<IgniterController>();

            if (igniter != null && igniter.IsIgnited)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 根据热源状态更新温度；停止加热后有一段余热期，期间温度保持在沸点附近，
    /// 余热散尽才开始自然冷却。
    /// </summary>
    private void UpdateTemperature()
    {
        if (m_isHeated)
        {
            // 被加热：温度上升，但不超过沸点 + 一点余量
            float maxTemp = m_effectiveBoilingPoint + 5f;
            m_currentTemperature = Mathf.Min(m_currentTemperature + heatingRate * Time.deltaTime, maxTemp);
            // 持续加热则把余热计时器保持在满值
            m_residualTimer = residualHeatDuration;
        }
        else if (m_residualTimer > 0f)
        {
            // 余热期：温度维持在沸点附近（模拟关火后液体仍沸腾一会儿），不降温
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