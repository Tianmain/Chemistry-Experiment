using UnityEngine;
using Chemistry;

/// <summary>
/// 液体源 - 定义一块液体区域的类型、颜色和几何范围
/// 挂在液体区域的子物体上，通过多边形碰撞器确定该区域内的液体种类
/// </summary>
public class LiquidSource : MonoBehaviour
{
    [Tooltip("关联的化学试剂数据（可选，关联后颜色和类型会自动同步）")]
    public ChemicalReagent reagentData;

    [Tooltip("液体类型名称（如：Water、Alcohol、HCl 等）")]
    public string liquidType = "Water";

    [Tooltip("液体显示颜色（若关联了 Reagent Data 且 Use Reagent Color 为 true，则优先使用试剂颜色）")]
    public Color liquidColor = ChemistryConstants.DefaultLiquidColor;

    [Tooltip("是否优先使用关联试剂的颜色")]
    public bool useReagentColor = true;

    [Tooltip("手动标记为空容器（可装液体但目前无液体）。勾选后无论试剂名为何都按 empty 处理；也可由「试剂/类型为 none」自动勾上")]
    public bool isEmptyContainer = false;

    [Tooltip("用于确定液体区域的多边形碰撞器（留空则自动获取自身及子物体的所有 Collider2D）")]
    public Collider2D[] regionColliders;

    // ===== 溶解态：当前溶在本容器水里的固体（蒸干后据此析出） =====
    // 这是溶解的逆过程所需的「记账」：固体溶进水里后，只以溶液色 + 这些字段存在，
    // 水被蒸干（或倒掉）时再据此把溶质还原成固体。与液相 color/type 互相独立。

    [Tooltip("溶解态：当前溶在水里的固体试剂（蒸干后析出为固体）。null 表示水里没有溶解物")]
    public ChemicalReagent dissolvedReagentData = null;

    [Tooltip("溶解态：溶在水里的固体质量（克）")]
    public float dissolvedMass = 0f;

    [Tooltip("溶解态：溶在水里的固体形态（析出时按此形态还原，如蒸干得晶体）")]
    public SolidForm dissolvedForm = SolidForm.Powder;

    /// <summary>
    /// 运行期标记：本液体是「开局即配好的初始溶液」，浓度由 reagentData.defaultSolutionConcentration 决定。
    /// 为真时 LiquidVolumeUI 每帧按当前水量反推溶解质量，使标签浓度恒等于配置浓度（不受水量变化/初始化时序影响）。
    /// 一旦运行时发生真实化学行为（继续溶解、蒸干结晶、倾倒转移），由对应模块把本标记置 false，
    /// 此后浓度改由实际溶解质量计算。
    /// </summary>
    [HideInInspector] public bool initialSolutionActive = false;

    /// <summary>
    /// 运行期防抖计数：容器「溶液（dissolvedMass>0）且区域内持续无水」的连续 tick 数，
    /// 达到阈值才析出固体，避免水位波动瞬间误判蒸干。
    /// </summary>
    [System.NonSerialized] internal int crystalTicks = 0;

    /// <summary>
    /// 空容器标记词：当试剂名或液体类型为 "none" 时，视为「可装液体但目前无液体」的空容器。
    /// 这类容器不参与初始灌水，标签只显示 "none"。
    /// </summary>
    public const string EMPTY_MARKER = "none";

    /// <summary>
    /// 碰撞器是否已收集过（避免 ContainsPoint 重复调用 AutoCollectColliders）
    /// </summary>
    private bool m_collidersCollected = false;

    /// <summary>
    /// 运行期防抖计数（不序列化，仅存在于运行时）：
    ///   - emptyTicks：容器「已持有液体类型、但区域内持续无水」的连续 tick 数，达到阈值才回退为 none；
    ///   - fillTicks：容器「为空、但区域内持续有水」的连续 tick 数，达到阈值才归类为某液体类型。
    /// 由 LiquidRegionQueries.SyncEmptyContainerTypes 使用，防止标签在 none / 类型之间抖动（乱变）。
    /// </summary>
    [System.NonSerialized] internal int emptyTicks = 0;
    [System.NonSerialized] internal int fillTicks = 0;

    private void OnValidate()
    {
        AutoCollectColliders();
        SyncFromReagent();
        // 若试剂名 / 类型 / 资产文件名为 "none"，自动标记为空容器（持久化到序列化字段，运行时稳定）
        if (!isEmptyContainer && NameLooksEmpty())
            isEmptyContainer = true;

        // 固液互斥护栏：初始配置时一个容器只能二选一（液体或固体）。
        // 配了液体就把配对的固体清空，避免两者同配导致标签同时显示两行。
        // 仅改序列化字段，不调用对方 OnValidate，无递归风险。
        if (HasExplicitLiquid())
        {
            // 溶于水形成的溶液形态（显式标记 isAqueousSolution）被配置为「液体」时，
            // 自动补 "(aq)" 后缀，使标签明确读作溶液（如 CuSO4 → CuSO4(aq)），
            // 并让 IsAqueousSolution 据此识别（不再误判为纯液体 → 100%）。
            if (reagentData != null
                && reagentData.isAqueousSolution
                && !liquidType.Equals("Water", System.StringComparison.OrdinalIgnoreCase)
                && !liquidType.EndsWith("(aq)", System.StringComparison.OrdinalIgnoreCase))
            {
                liquidType = liquidType + "(aq)";
            }

            SolidSource sibling = GetComponent<SolidSource>();
            if (sibling != null && sibling.HasExplicitSolid())
                sibling.SetEmptySolid();
        }
    }

    private void Awake()
    {
        AutoCollectColliders();
    }

    private void AutoCollectColliders()
    {
        if (m_collidersCollected) return;
        if (regionColliders == null || regionColliders.Length == 0)
        {
            regionColliders = GetComponentsInChildren<Collider2D>();
        }
        if (regionColliders != null && regionColliders.Length > 0)
            m_collidersCollected = true;
    }

    /// <summary>
    /// 从关联的试剂数据同步名称和颜色
    /// </summary>
    private void SyncFromReagent()
    {
        if (reagentData == null) return;

        if (string.IsNullOrEmpty(liquidType) || liquidType == "Water")
            liquidType = !string.IsNullOrEmpty(reagentData.englishName) ? reagentData.englishName : reagentData.reagentName;

        if (useReagentColor)
            liquidColor = reagentData.GetDisplayColor();
    }

    /// <summary>
    /// 获取实际用于渲染的液体颜色
    /// </summary>
    public Color GetEffectiveColor()
    {
        if (useReagentColor && reagentData != null)
            return reagentData.GetDisplayColor();
        return liquidColor;
    }

    /// <summary>
    /// 是否为空容器：满足以下任一即为真——
    ///   1) 手动勾选了 isEmptyContainer；
    ///   2) 液体类型 liquidType 为 "none"；
    ///   3) 关联试剂 reagentData 的名称（reagentName / englishName）为 "none"；
    ///   4) 关联试剂资产文件名为 "none"（兼容只改名没填名称字段的情况）。
    /// 空容器表示「此处可以装液体，但当前没有液体」——不参与初始灌水，标签显示 "none"，液面透明。
    /// </summary>
    public bool IsEmpty()
    {
        if (isEmptyContainer) return true;
        if (!string.IsNullOrEmpty(liquidType)
            && liquidType.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (reagentData != null)
        {
            if (!string.IsNullOrEmpty(reagentData.reagentName)
                && reagentData.reagentName.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(reagentData.englishName)
                && reagentData.englishName.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
            // 资产文件名（ScriptableObject.name 即 Project 中的文件名）
            if (!string.IsNullOrEmpty(reagentData.name)
                && reagentData.name.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 仅按「名称/类型/文件名」判断是否为空容器（不含手动标记），供 OnValidate 自动勾选使用。
    /// </summary>
    private bool NameLooksEmpty()
    {
        if (!string.IsNullOrEmpty(liquidType)
            && liquidType.Equals(EMPTY_MARKER, System.StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 当前液体源是否代表一种「真实液体」（非空容器）。用于固液互斥护栏。
    /// </summary>
    public bool HasExplicitLiquid()
    {
        return !IsEmpty();
    }

    /// <summary>
    /// 当前液体是否为「水溶液」——溶质溶于水形成的溶液（含溶质、含水，浓度严格小于 100%）。
    /// 判定比单纯看 "(aq)" 后缀更本质：本身是固体、又可溶于水的试剂（如 CuSO4、NaCl），
    /// 其「液体」形态只能是水溶液；纯液体（水、乙醇）或运行时溶解产生的 "(aq)" 溶液均据此识别。
    /// </summary>
    public bool IsAqueousSolution()
    {
        if (string.IsNullOrEmpty(liquidType)) return false;
        if (liquidType.EndsWith("(aq)", System.StringComparison.OrdinalIgnoreCase)) return true;
        // 显式标记：本身是溶于水形成的溶液形态（如「硫酸铜溶液」资产 defaultState 虽为 Liquid，但本质是水溶液）
        if (reagentData != null && reagentData.isAqueousSolution) return true;
        return false;
    }

    /// <summary>
    /// 清空液体（互斥护栏用）：把液体还原为 none 空态，并清掉溶解态记账。
    /// 仅修改序列化字段，不触发其它组件的 OnValidate（无递归风险）。
    /// </summary>
    public void SetEmptyLiquid()
    {
        liquidType = EMPTY_MARKER;
        reagentData = null;
        isEmptyContainer = true;
        dissolvedReagentData = null;
        dissolvedMass = 0f;
        dissolvedForm = SolidForm.Powder;
    }

    /// <summary>
    /// 检测某个世界坐标是否落在本液体源定义的区域内
    /// </summary>
    public bool ContainsPoint(Vector2 worldPos)
    {
        if (regionColliders == null) return false;

        foreach (var col in regionColliders)
        {
            if (col != null && col.OverlapPoint(worldPos))
                return true;
        }
        return false;
    }
}