using UnityEngine;
using Chemistry;

/// <summary>
/// 固体形态枚举：影响溶解速率与外观。
/// </summary>
public enum SolidForm
{
    Powder,    // 粉末（溶解最快）
    Granule,   // 颗粒
    Crystal,   // 晶体
    Flake,     // 片状
    Chunk      // 块状（溶解最慢）
}

/// <summary>
/// 固体源 - 描述一个容器当前所盛的固体（种类、颜色、质量、形态）。
///
/// 【固液同体约定】所有容器都既能装液体、也能装固体：
///   - SolidSource 与 LiquidSource **挂在同一个容器物体上**（RequireComponent 强制配对）；
///   - 两者**共用同一个范围区域（单一真相源）**：区域几何只在 LiquidSource.regionColliders 上配置一次，
///     SolidSource 自身不再持有任何区域字段，运行时通过 GetRegionColliders() 直接复用液体源的区域；
///   - 液相为空用 LiquidSource 的 "none"，固相为空用 SolidSource 的 "none"，二者互相独立。
///   - **初始配置只能二选一（液体或固体）**：OnValidate 内置互斥护栏——配固体会自动清空配对液体、
///     配液体会自动清空配对固体，从源头杜绝「两者同配」；标签也不会同时显示两行。
///     运行时溶解会把固体转为「液体 + 溶解态账本（dissolvedMass）」，故始终只有一种相态被显式配置。
///
/// 固体用「质量(克)」而非「体积」描述量，并额外引入形态(SolidForm)维度。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(LiquidSource))]
public class SolidSource : MonoBehaviour
{
    [Tooltip("关联的化学试剂数据（应为 defaultState=Solid 的试剂；关联后名称/颜色会自动同步）")]
    public ChemicalReagent reagentData;

    [Tooltip("固体类型名称（如：NaCl、CuSO4、I2 等）。默认 none 表示容器目前没有固体")]
    public string solidType = EMPTY_MARKER;

    [Tooltip("固体显示颜色（若关联了 Reagent Data 且 Use Reagent Color 为 true，则优先使用试剂颜色）")]
    public Color solidColor = Color.white;

    [Tooltip("是否优先使用关联试剂的颜色")]
    public bool useReagentColor = true;

    [Tooltip("固体质量（克）—— 固体的「量」用质量表示。溶解/熔化/升华/分解时按此值增减")]
    public float mass = 0f;

    [Tooltip("固体形态：影响溶解速率（粉末最快、块状最慢）")]
    public SolidForm solidForm = SolidForm.Powder;

    [Tooltip("手动标记为「无固体」（容器可装固体但目前是空的）。默认勾选；放入固体后自动取消")]
    public bool isEmptyContainer = true;

    /// <summary>
    /// 空标记词：当试剂名或固体类型为 "none" 时，视为「可装固体但目前无固体」。
    /// 与 LiquidSource.EMPTY_MARKER 语义一致，但两相互相独立。
    /// </summary>
    public const string EMPTY_MARKER = "none";

    // 区域几何不在此重复持有：固液共用同一个内腔，统一由同容器 LiquidSource.regionColliders 提供。
    // 见 GetRegionColliders()。
    private LiquidSource m_pairedLiquid;
    private Collider2D[] m_fallbackColliders;
    [System.NonSerialized] internal int emptyTicks = 0;
    [System.NonSerialized] internal int fillTicks = 0;

    private void OnValidate()
    {
        SyncFromReagent();

        if (NameLooksEmpty())
        {
            // 试剂/类型为 none：容器可装固体，但当前是空的
            isEmptyContainer = true;
        }
        else if (reagentData != null)
        {
            // Inspector 里指定了具体固体试剂 = 该容器开局就装着这种固体，自动脱离空态
            isEmptyContainer = false;
            if (mass <= 0f) mass = 10f;
        }

        // 固液互斥护栏：初始配置时一个容器只能二选一（液体或固体）。
        // 配了固体就把配对的液体清空，避免两者同配导致标签同时显示两行。
        // 仅改序列化字段，不调用对方 OnValidate，无递归风险。
        if (HasExplicitSolid())
        {
            LiquidSource sibling = GetComponent<LiquidSource>();
            if (sibling != null && sibling.HasExplicitLiquid())
                sibling.SetEmptyLiquid();
        }
    }

    /// <summary>
    /// 当前固体源是否代表一种「真实固体」（非空容器）。用于固液互斥护栏。
    /// </summary>
    public bool HasExplicitSolid()
    {
        if (string.IsNullOrEmpty(solidType)) return false;
        if (solidType.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// 清空固体（互斥护栏用）：把固体还原为 none 空态。
    /// 仅修改序列化字段，不触发其它组件的 OnValidate（无递归风险）。
    /// </summary>
    public void SetEmptySolid()
    {
        solidType = EMPTY_MARKER;
        reagentData = null;
        mass = 0f;
        isEmptyContainer = true;
    }

    // 自注册到 SolidSystem：使运行时实例化（Instantiate）的容器也能参与溶解调度，
    // 而不必依赖 SolidSystem.Start 那一次性的全场景扫描。
    private void OnEnable()
    {
        if (SolidSystem.InstanceOrNull != null)
            SolidSystem.InstanceOrNull.RegisterSolid(this);
    }

    private void OnDisable()
    {
        if (SolidSystem.InstanceOrNull != null)
            SolidSystem.InstanceOrNull.UnregisterSolid(this);
    }

    // ===== 区域几何：与配对 LiquidSource 共用同一套区域（单一真相源） =====

    /// <summary>
    /// 获取区域碰撞器。固液共用同一个内腔，因此**直接复用同容器 LiquidSource 的区域**，
    /// 固体自身不再持有任何独立的区域副本——这正是「液体固体用一个范围区域」的落地方式。
    /// 仅当液体源区域尚未就绪（如 Awake 顺序导致）时，才回退到自身及子物体的碰撞器；
    /// 该兜底结果会被缓存，一旦液体源区域可用即以其为准。
    /// 外部一律走此方法，不要直接读 LiquidSource.regionColliders 以外的任何来源。
    /// </summary>
    public Collider2D[] GetRegionColliders()
    {
        LiquidSource liquid = GetPairedLiquidSource();
        if (liquid != null && liquid.regionColliders != null && liquid.regionColliders.Length > 0)
            return liquid.regionColliders;

        // 液体源区域尚未就绪时的兜底：自采集一次并缓存
        if (m_fallbackColliders == null)
            m_fallbackColliders = GetComponentsInChildren<Collider2D>();
        return m_fallbackColliders;
    }

    private void SyncFromReagent()
    {
        if (reagentData == null) return;

        if (string.IsNullOrEmpty(solidType) || solidType == EMPTY_MARKER)
            solidType = !string.IsNullOrEmpty(reagentData.englishName) ? reagentData.englishName : reagentData.reagentName;

        if (useReagentColor)
            solidColor = reagentData.GetDisplayColor();
    }

    /// <summary>获取实际用于渲染的固体颜色。</summary>
    public Color GetEffectiveColor()
    {
        if (useReagentColor && reagentData != null)
            return reagentData.GetDisplayColor();
        return solidColor;
    }

    /// <summary>
    /// 当前是否「没有固体」：满足以下任一即为真——
    ///   1) 手动勾选了 isEmptyContainer；
    ///   2) 固体类型 solidType 为 "none"；
    ///   3) 关联试剂 reagentData 的名称（reagentName / englishName / 资产文件名）为 "none"。
    /// 注意：这只表示固相为空，容器仍可能装有液体（由 LiquidSource 独立判断）。
    /// </summary>
    public bool IsEmpty()
    {
        if (isEmptyContainer) return true;
        if (!string.IsNullOrEmpty(solidType)
            && solidType.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (reagentData != null)
        {
            if (!string.IsNullOrEmpty(reagentData.reagentName)
                && reagentData.reagentName.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(reagentData.englishName)
                && reagentData.englishName.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(reagentData.name)
                && reagentData.name.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool NameLooksEmpty()
    {
        if (!string.IsNullOrEmpty(solidType)
            && solidType.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (reagentData != null)
        {
            if (!string.IsNullOrEmpty(reagentData.reagentName)
                && reagentData.reagentName.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(reagentData.englishName)
                && reagentData.englishName.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(reagentData.name)
                && reagentData.name.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>检测某个世界坐标是否落在本固体源定义的区域内。</summary>
    public bool ContainsPoint(Vector2 worldPos)
    {
        Collider2D[] cols = GetRegionColliders();
        if (cols == null) return false;
        foreach (var col in cols)
        {
            if (col != null && col.OverlapPoint(worldPos))
                return true;
        }
        return false;
    }

    // ===== 质量增减 =====

    /// <summary>当前质量（克）。无固体时返回 0。</summary>
    public float GetMass()
    {
        return IsEmpty() ? 0f : Mathf.Max(0f, mass);
    }

    /// <summary>减少质量（溶解/升华/分解/熔化时调用），不会低于 0；耗尽后固相退化为空。</summary>
    public void ReduceMass(float amount)
    {
        mass = Mathf.Max(0f, mass - amount);
        if (mass <= 0.0001f && !IsEmpty())
            ClearSolid();
    }

    /// <summary>增加质量（外部加入固体时调用），并从空状态恢复。</summary>
    public void AddMass(float amount)
    {
        mass = Mathf.Max(0f, mass + amount);
        if (isEmptyContainer && amount > 0f)
        {
            isEmptyContainer = false;
            if (solidType == EMPTY_MARKER) solidType = "";
        }
    }

    // ===== 运行时装填 / 清空 =====

    /// <summary>
    /// 往容器里放入固体（运行时）。会覆盖当前固体种类并重置质量。
    /// 与「往容器灌液体」对称，是让任意容器装固体的标准入口。
    /// </summary>
    public void SetSolid(ChemicalReagent reagent, float grams, SolidForm form = SolidForm.Powder)
    {
        if (reagent == null || grams <= 0f) return;
        reagentData = reagent;
        solidType = !string.IsNullOrEmpty(reagent.englishName) ? reagent.englishName : reagent.reagentName;
        useReagentColor = true;
        solidColor = reagent.GetDisplayColor();
        solidForm = form;
        mass = grams;
        isEmptyContainer = false;
    }

    /// <summary>按试剂名放入固体（从试剂数据库查找）。找不到试剂时返回 false。</summary>
    public bool SetSolid(string reagentName, float grams, SolidForm form = SolidForm.Powder)
    {
        ChemicalReagent r = ChemistrySystem.FindReagent(reagentName);
        if (r == null) return false;
        SetSolid(r, grams, form);
        return true;
    }

    /// <summary>清空固相（容器仍可装固体，只是当前没有）。液相不受影响。</summary>
    public void ClearSolid()
    {
        mass = 0f;
        isEmptyContainer = true;
        solidType = EMPTY_MARKER;
    }

    // ===== 固液配对 =====

    /// <summary>
    /// 获取与本固体源同容器的液体源。
    /// 解析顺序：同物体 → 子物体 → 父级链（兼容旧的「互为兄弟」布局）。
    /// 由于 RequireComponent(typeof(LiquidSource))，正常情况下第一步即可命中。
    /// </summary>
    public LiquidSource GetPairedLiquidSource()
    {
        if (m_pairedLiquid != null) return m_pairedLiquid;

        m_pairedLiquid = GetComponent<LiquidSource>();
        if (m_pairedLiquid == null) m_pairedLiquid = GetComponentInChildren<LiquidSource>();
        if (m_pairedLiquid == null) m_pairedLiquid = GetComponentInParent<LiquidSource>();
        if (m_pairedLiquid == null && transform.parent != null)
            m_pairedLiquid = transform.parent.GetComponentInChildren<LiquidSource>();

        return m_pairedLiquid;
    }

    /// <summary>旧名保留（等价于 GetPairedLiquidSource），避免外部调用点失效。</summary>
    public LiquidSource GetSiblingLiquidSource() => GetPairedLiquidSource();

    /// <summary>
    /// 取得某个容器（以其 LiquidSource 为标识）上的 SolidSource。
    /// 组件一律在预制体上静态挂载，此处**不会**自动补挂；容器没挂就返回 null。
    /// </summary>
    public static SolidSource Get(LiquidSource container)
    {
        return container == null ? null : container.GetComponent<SolidSource>();
    }
}
