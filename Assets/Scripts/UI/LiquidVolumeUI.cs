using UnityEngine;
using TMPro;
using System.Text;
using Chemistry;

/// <summary>
/// 浓度计算方式。
///   - MassPercent：质量分数 = 溶质质量 / (溶质质量 + 溶剂质量) × 100%（默认，最直观）
///   - MassPerVolume：质量浓度 = 溶质质量 / 溶液体积（g/L）
/// </summary>
public enum ConcentrationUnit
{
    MassPercent,
    MassPerVolume,
}

/// <summary>
/// 容器内容标签（同一块标签，既显示液体也显示固体）。
///
/// 显示规则（同一容器要么是纯液体、要么是纯固体，故**在同一位置替换**，不再分成两块标签）：
///   - 容器内有固体   → 显示「固体名 + 质量(g)」
///   - 容器内有液体   → 显示「液体名 + 体积(mL)」，若为溶液（溶有固体）则额外显示「浓度」一行
///   - 溶解中固液共存 → 默认两者都显示（各占两行，字号自动缩小）；
///                      可关闭 showBothWhenMixed，改为按 solidTakesPriority 只显示一个
///   - 都没有         → 显示 "none"
///
/// 类名保留 LiquidVolumeUI 不变，以免破坏场景/预制体里已有的组件引用。
/// 所有标签样式配置（字号、每格毫升数、偏移、底板等）都只在本组件内定义一次。
/// </summary>
[DisallowMultipleComponent]
public class LiquidVolumeUI : MonoBehaviour
{
    [Header("功能引用（通常无需手动设）")]
    [Tooltip("网格绘制器，用于统计区域内水量。留空则自动查找场景中的 LayerGridPainter")]
    public LayerGridPainter painter;
    [Tooltip("关联的液体源。留空则向上查找 LiquidSource")]
    public LiquidSource source;
    [Tooltip("关联的固体源。留空则自动取同物体（或父级）的 SolidSource；没有固体源时本标签只显示液体")]
    public SolidSource solidSource;

    [Header("标签配置（仅在此处定义一次）")]
    [Tooltip("每格水代表的液体体积（mL），用于把水格数换算成 mL")]
    public float mLPerCell = 1f;
    [Tooltip("字号相对底板可用区域的微调系数：1=恰好填满底板留白区，>1 更大，<1 更小")]
    public float labelFontSize = 1f;
    [Tooltip("标签自容器顶部向下的偏移量（世界单位）。越大标签在容器内垂得越低")]
    public float labelOffsetY = 0.3f;
    [Tooltip("是否在黑字后面显示白色矩形底板（关闭则只剩黑字、无底块）")]
    public bool showLabelBackdrop = true;
    [Tooltip("白色底块宽度相对容器宽度的系数（底块宽 = 容器宽 × 此值）")]
    public float backdropWidthScale = 1.1f;
    [Tooltip("白色底块高度相对容器高度的系数（底块高 = 容器高 × 此值）")]
    public float backdropHeightScale = 0.55f;
    [Tooltip("标签刷新间隔（秒）。降低刷新频率可减少每帧的整网格扫描次数")]
    public float refreshInterval = 0.2f;

    [Header("固体显示")]
    [Tooltip("固液共存时（例如溶解过程中）是否两者都显示。关闭则只显示其中一个")]
    public bool showBothWhenMixed = true;
    [Tooltip("只显示一个时，固体优先于液体（showBothWhenMixed 关闭时才生效）")]
    public bool solidTakesPriority = true;
    [Tooltip("质量显示的小数位数")]
    [Range(0, 3)] public int massDecimals = 1;

    [Header("浓度显示")]
    [Tooltip("是否在液体标签下方额外显示一行浓度。仅对「含有溶解固体的溶液」生效；纯液体/纯水不显示该行")]
    public bool showConcentration = true;
    [Tooltip("浓度计算方式：质量分数(%)=溶质质量/(溶质+溶剂质量)×100；质量浓度(g/L)=溶质质量/溶液体积")]
    public ConcentrationUnit concentrationUnit = ConcentrationUnit.MassPercent;
    [Tooltip("浓度显示的小数位数")]
    [Range(0, 3)] public int concentrationDecimals = 1;

    private TextMeshPro m_label;
    private SpriteRenderer m_back;
    private Renderer m_labelRenderer;
    private StringBuilder m_textBuilder = new StringBuilder(64);
    private float m_refreshTimer;
    private bool m_initialized;
    private int m_cachedWater;   // 缓存水量格数，拖拽/旋转时沿用，避免毫升数跳动
    private float m_cachedMass;  // 缓存固体质量，同上

    // 共享白色贴图（所有标签共用一张，避免重复分配）
    private static Texture2D s_whiteTex;
    private static Sprite s_whiteSprite;

    // 复用的角点缓冲：避免 GetLocalContainerBounds 每次调用都 new 一个数组（每刷新一次就分配一次）
    private static readonly Vector3[] s_cornerBuffer = new Vector3[4];
    private static Sprite WhiteSprite
    {
        get
        {
            if (s_whiteSprite == null)
            {
                s_whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                s_whiteTex.SetPixel(0, 0, Color.white);
                s_whiteTex.Apply();
                s_whiteSprite = Sprite.Create(s_whiteTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            }
            return s_whiteSprite;
        }
    }

    private void Awake()
    {
        ResolveReferences();
        BuildLabelObjects();
        m_initialized = true;
        Refresh();
    }

    private void ResolveReferences()
    {
        if (painter == null)
            painter = LayerGridPainter.Instance;
        if (source == null)
            source = GetComponentInParent<LiquidSource>();
        if (source == null)
            source = GetComponentInChildren<LiquidSource>();

        if (solidSource == null)
        {
            // 【固液同体】固体源与液体源必定挂在同一个物体上（SolidSource 有 RequireComponent(LiquidSource)）。
            // 本标签常挂在容器根物体、而液/固源在子物体 LiquidRegion 上，
            // 因此优先顺着已解析出的 source 去同物体取，比 GetComponentInParent 可靠。
            if (source != null)
                solidSource = source.GetComponent<SolidSource>();
            if (solidSource == null)
                solidSource = GetComponent<SolidSource>();
            if (solidSource == null)
                solidSource = GetComponentInChildren<SolidSource>();
            if (solidSource == null)
                solidSource = GetComponentInParent<SolidSource>();
        }
    }

    private void BuildLabelObjects()
    {
        // 标签文字载体（按 labelFontSize 缩放）
        GameObject lab = new GameObject("VolumeLabel");
        lab.transform.SetParent(transform, false);
        m_label = lab.AddComponent<TextMeshPro>();
        TMP_FontAsset fa = TMP_Settings.defaultFontAsset;
        if (fa == null) fa = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (fa != null) m_label.font = fa;
        m_label.alignment = TextAlignmentOptions.Center;
        m_label.fontSize = 12;
        m_label.color = Color.black;
        m_labelRenderer = m_label.GetComponent<Renderer>();
        m_labelRenderer.sortingOrder = 102;

        // 白色底板（底块，不随字号缩放，按世界尺寸设置）
        GameObject back = new GameObject("VolumeLabelBack");
        back.transform.SetParent(transform, false);
        m_back = back.AddComponent<SpriteRenderer>();
        m_back.sprite = WhiteSprite;
        m_back.color = Color.white;
        m_back.sortingOrder = 101;
    }

    private void Update()
    {
        if (!m_initialized || painter == null || source == null) return;

        // 惰性回补：固液同体，固体源与 source 在同一物体上（容器上没挂 SolidSource 时保持 null，只显示液体）
        if (solidSource == null)
            solidSource = source.GetComponent<SolidSource>();

        m_refreshTimer -= Time.deltaTime;
        if (m_refreshTimer > 0f) return;
        m_refreshTimer = refreshInterval;
        Refresh();
    }

    private void Refresh()
    {
        // 使用局部空间 bounds，标签位置不受容器旋转影响
        Bounds cb = GetLocalContainerBounds();

        // 锚点：容器顶部中心，再向下偏移 labelOffsetY（全部在局部空间，无需世界↔局部转换）
        Vector3 localAnchor = cb.center + Vector3.up * (cb.size.y * 0.5f - labelOffsetY);

        Transform labT = m_label.transform;
        labT.localPosition = localAnchor;
        // 底板位置（与标签同锚点，略靠后）
        m_back.transform.localPosition = localAnchor + Vector3.back * 0.01f;

        // 拖拽 / 旋转过程中不重新统计，沿用上次缓存值，避免数字频繁跳动
        bool suppressUpdate = painter.IsDragging || painter.IsRotating;
        if (!suppressUpdate)
        {
            m_cachedWater = (source != null && !source.IsEmpty())
                ? painter.GetWaterCountInRegion(source.regionColliders) : 0;
            m_cachedMass = (solidSource != null && !solidSource.IsEmpty())
                ? solidSource.GetMass() : 0f;
        }

        UpdateText(m_cachedWater, m_cachedMass);

        // ===== 尺寸依赖链：容器大小 → 白色底板尺寸 → 文字大小 =====
        m_back.gameObject.SetActive(showLabelBackdrop);

        // 1. 白色底板尺寸完全由容器大小决定（局部空间，旋转后保持不变）
        float desiredWidth  = Mathf.Max(cb.size.x * backdropWidthScale, 0.1f);
        float desiredHeight = Mathf.Max(cb.size.y * backdropHeightScale, 0.1f);

        if (showLabelBackdrop && m_back.sprite != null)
        {
            // localScale 是相对于父级的缩放：局部尺寸 = localScale × 精灵原生尺寸
            float spriteW = m_back.sprite.bounds.size.x;
            float spriteH = m_back.sprite.bounds.size.y;
            if (spriteW < 1e-4f) spriteW = 0.01f;
            if (spriteH < 1e-4f) spriteH = 0.01f;

            m_back.transform.localScale = new Vector3(
                desiredWidth  / spriteW,
                desiredHeight / spriteH,
                1f
            );
        }

        // 2. 文字缩放：测量 scale=1 下的自然世界尺寸，由底板内部可用区域决定字号，保证文字不超出底板
        labT.localScale = Vector3.one;
        m_label.ForceMeshUpdate();
        float naturalW = m_label.bounds.size.x;
        float naturalH = m_label.bounds.size.y;
        if (naturalW < 1e-4f) naturalW = 0.5f;
        if (naturalH < 1e-4f) naturalH = 0.5f;

        float marginX = desiredWidth  * 0.08f + 0.04f;
        float marginY = desiredHeight * 0.08f + 0.04f;
        float availW = Mathf.Max(0.01f, desiredWidth  - marginX * 2f);
        float availH = Mathf.Max(0.01f, desiredHeight - marginY * 2f);
        float scale = Mathf.Min(availW / naturalW, availH / naturalH) * labelFontSize;
        scale = Mathf.Max(scale, 0.02f);
        labT.localScale = Vector3.one * scale;
    }

    /// <summary>
    /// 获取容器在局部空间中的包围盒（不受容器旋转影响）。
    /// 将每个碰撞器的世界 AABB 角点逆变换回局部空间后取包围盒。
    /// 对于 90° 倍数旋转，这等价于原始局部 bounds，旋转后尺寸/中心保持稳定。
    /// </summary>
    private Bounds GetLocalContainerBounds()
    {
        Bounds b = new Bounds();
        bool has = false;
        Collider2D[] cols = (source != null) ? source.regionColliders : null;
        if (cols != null)
        {
            foreach (var c in cols)
            {
                if (c == null) continue;
                Bounds wb = c.bounds;
                // 取世界 AABB 的 4 个角点，逆变换回局部空间
                Vector3 min = wb.min, max = wb.max;
                s_cornerBuffer[0].Set(min.x, min.y, min.z);
                s_cornerBuffer[1].Set(max.x, min.y, min.z);
                s_cornerBuffer[2].Set(min.x, max.y, min.z);
                s_cornerBuffer[3].Set(max.x, max.y, min.z);
                foreach (var corner in s_cornerBuffer)
                {
                    Vector3 localPt = transform.InverseTransformPoint(corner);
                    if (!has) { b = new Bounds(localPt, Vector3.zero); has = true; }
                    else b.Encapsulate(localPt);
                }
            }
        }
        if (!has)
        {
            // 退化情况：没有任何区域碰撞器时，用原点当一点
            b = new Bounds(Vector3.zero, Vector3.one * 0.5f);
        }
        return b;
    }

    /// <summary>
    /// 组装标签文字：液体「名 + mL」、固体「名 + g」，同一块标签内切换/共存。
    /// </summary>
    private void UpdateText(int water, float mass)
    {
        if (source == null) return;

        // ---- 液相 ----
        // 毫升数取整百（四舍五入）；取整后为 0（不足 50 mL 的微量液体）视为空
        int volRounded = 0;
        if (!source.IsEmpty())
            volRounded = Mathf.FloorToInt(water * mLPerCell / 100f + 0.5f) * 100;
        bool hasLiquid = volRounded > 0;

        // ---- 固相 ----
        bool hasSolid = solidSource != null && !solidSource.IsEmpty() && mass > 0.0001f;

        // 都没有：容器可装东西但当前是空的
        if (!hasLiquid && !hasSolid)
        {
            m_label.text = LiquidSource.EMPTY_MARKER;
            return;
        }

        // 只显示一个时，按优先级取舍（正常情况下固液不共存，此分支很少走到）
        if (hasLiquid && hasSolid && !showBothWhenMixed)
        {
            if (solidTakesPriority) hasLiquid = false;
            else hasSolid = false;
        }

        m_textBuilder.Length = 0;

        if (hasSolid)
        {
            m_textBuilder.Append(solidSource.solidType);
            m_textBuilder.Append('\n');
            m_textBuilder.Append(mass.ToString("F" + massDecimals));
            m_textBuilder.Append(" g");
        }

        if (hasLiquid)
        {
            if (m_textBuilder.Length > 0) m_textBuilder.Append('\n');
            m_textBuilder.Append(source.liquidType);
            m_textBuilder.Append('\n');
            m_textBuilder.Append(volRounded.ToString());
            m_textBuilder.Append(" mL");

            // 仅「水溶液（溶液）」额外显示浓度一行；熔融态（纯熔化液体）、纯非水液体、水均不显示。
            // 浓度数值的「来源」始终指向溶质固体试剂（溶解质量 / 其 defaultSolutionConcentration），不取自溶液态资产本身。
            if (showConcentration && ShouldShowConcentration())
            {
                EnsureInitialSolutionConcentration(water);
                float conc = ComputeConcentration(water);
                if (conc >= 0f)
                {
                    m_textBuilder.Append('\n');
                    m_textBuilder.Append("Conc. ");
                    m_textBuilder.Append(conc.ToString("F" + concentrationDecimals));
                    m_textBuilder.Append(concentrationUnit == ConcentrationUnit.MassPercent ? " %" : " g/L");
                }
            }
        }

        m_label.text = m_textBuilder.ToString();
    }

    /// <summary>
    /// 是否为「水溶液」——liquidType 以 "(aq)" 结尾（如 CuSO4(aq)、NaCl(aq)）。
    /// 溶液含有水、必然溶有固体，其浓度必须按溶解质量计算，绝不可能为 100%。
    /// </summary>
    private bool IsAqueousSolution()
    {
        return source != null && source.IsAqueousSolution();
    }

    /// <summary>
    /// 该液体是否应显示浓度行。
    /// 浓度只属于「溶液」（溶质溶于水形成的体系）：水溶液（liquidType 以 "(aq)" 结尾，或试剂显式标记 isAqueousSolution）
    /// 才显示真实浓度。以下情况一律不显示浓度：
    ///   - 水（纯溶剂）；
    ///   - 熔融态（固体熔化成的纯液体，如熔融盐）——它不是溶液，没有「浓度」可言；
    ///   - 纯非水液体（如乙醇）——同样不是溶液；
    ///   - 「溶液态溶质资产」（如直接摆放的 Liquid/Copper Sulfate，其 isAqueousSolution 已置 false）——浓度应直指真正的溶质固体试剂，而非这种资产本身。
    /// </summary>
    private bool ShouldShowConcentration()
    {
        if (!showConcentration) return false;
        return IsAqueousSolution();
    }

    /// <summary>
    /// 维护「开局即配好的溶液」的初始浓度：若本液体是水溶液、且其试剂设有「最常见浓度」
    /// (defaultSolutionConcentration)，则把溶解质量锁定为「按当前水量反推出的设计浓度值」，
    /// 使标签始终显示配置浓度（绝不会因初始化时序 / 水量变化而变成一半或其它值）。
    /// 浓度上限取「配置值」与「真实溶解度」二者较小者：既显示你配的浓度，也绝不超过该物质在水中的真实最大浓度。
    /// （溶解度上限由试剂的 solubilitySoluteParts/solubilityWaterParts 推算；若未填溶解度数据则不设上限、直接信任配置值。）
    /// 运行时继续溶解的溶解度上限由 SolidSystem 负责，与初始溶液的显示无关。一旦运行时发生真实溶解/结晶/倾倒
    /// （由对应模块把 initialSolutionActive 置 false），本方法停止覆盖，浓度改由实际溶解质量计算。
    /// </summary>
    private void EnsureInitialSolutionConcentration(int waterCells)
    {
        if (source == null) return;

        // 非水溶液 / 无试剂数据 → 不走「初始溶液」逻辑，浓度完全交给实际溶解质量
        if (!source.IsAqueousSolution() || source.reagentData == null)
        {
            source.initialSolutionActive = false;
            return;
        }

        // 首次：从试剂数据的「最常见浓度」捕获并激活（仅一次）
        if (!source.initialSolutionActive)
        {
            float defaultConc = source.reagentData.defaultSolutionConcentration;
            if (defaultConc <= 0.0001f)
            {
                source.initialSolutionActive = false;
                return;
            }
            source.initialSolutionActive = true;
        }

        // 已激活：每帧按「当前水量」反推溶解质量，保证标签浓度 = 设计浓度（与水量多少无关）。
        // 浓度上限取「配置值」与「真实溶解度」二者较小者：既显示你配的浓度，也绝不超过该物质在水中的真实最大浓度。
        // （溶解度上限由试剂的 solubilitySoluteParts/solubilityWaterParts 决定；若未填写溶解度数据则不设上限、直接信任配置值。）
        // 运行时继续往里加溶质时的溶解度上限仍由 SolidSystem 负责，与初始溶液的显示无关。
        float waterMass = waterCells * mLPerCell;
        if (waterMass <= 0.0001f) return;   // 没水时不强行清零，避免覆盖蒸干/倾倒过程

        // 真实最大浓度（质量分数%）：试剂未定义溶解度时视为无上限（100）
        float maxConc = (source.reagentData.solubilitySoluteParts > 0.0001f)
            ? SaturationMassPercent() : 100f;
        // 配置值不超过真实溶解度上限；并夹紧到 <100% 避免分母 (100-p)=0 除零
        float p = Mathf.Clamp(Mathf.Min(source.reagentData.defaultSolutionConcentration, maxConc), 0f, 99.99f);
        // 质量分数 p% → 溶质质量 d 满足 d/(d+w)=p/100 → d = w·p/(100-p)
        source.dissolvedMass = (p / (100f - p)) * waterMass;
        source.dissolvedReagentData = source.reagentData;
        source.dissolvedForm = SolidForm.Powder;
    }

    /// <summary>
    /// 由溶解度份数比换算出的质量分数上限（%），即该物质在水中的真实最大浓度。
    /// 用于约束初始溶液浓度不超过真实溶解度（仅在试剂定义过溶解度时调用）。
    /// </summary>
    private float SaturationMassPercent()
    {
        if (source.reagentData == null) return 100f;
        float ratio = source.reagentData.solubilitySoluteParts
            / Mathf.Max(1f, source.reagentData.solubilityWaterParts);
        return ratio / (ratio + 1f) * 100f;
    }

    /// <summary>
    /// 计算当前液体浓度。本方法只在 ShouldShowConcentration() 返回 true（即水溶液）时被调用，
    /// 因此这里只处理「水溶液」：按实际溶解质量计算质量分数 / 质量浓度。
    /// 溶液含水、必然溶有溶质，浓度严格小于 100%（且受溶解度上限约束，如 CuSO4 约 32%）。
    /// </summary>
    private float ComputeConcentration(int waterCells)
    {
        float exactMl = waterCells * mLPerCell;          // 溶液体积（mL），密度近似 1 g/mL
        if (exactMl < 1e-3f) return 0f;

        // 水溶液：按溶解质量算真实浓度；异常态（溶液却无溶质）显示 0%，绝不回退到 100%
        if (source.dissolvedMass <= 0.0001f)
            return 0f;
        if (concentrationUnit == ConcentrationUnit.MassPercent)
        {
            float solventMass = exactMl;                 // 水密度 ≈ 1 g/mL
            float solutionMass = solventMass + source.dissolvedMass;
            return source.dissolvedMass / solutionMass * 100f;
        }
        return source.dissolvedMass / (exactMl / 1000f);   // 质量浓度 g/L
    }

}
