using UnityEngine;
using Chemistry;
using System.Collections.Generic;

/// <summary>
/// 固体系统全局管理器（单例）。
///
/// 职责：**只做溶解调度**，不再动态挂载任何组件。
///   每帧检查固体与同容器液体的共存情况，按 ChemicalReagent 溶解度与固体形态
///   逐步溶解：固体质量减少、液体向溶液色「变浓」（不改变体积）。
///
/// 【接线约定】容器上的组件一律由使用者在预制体/场景中**静态挂好**（见项目 MEMORY.md）：
///   容器 = LiquidSource + SolidSource（+ LiquidVolumeUI 标签 / HeatableObject 加热）。
///   本管理器只负责收集场景中已存在的 SolidSource 并驱动溶解。
///
/// 加热相变（熔化/升华/分解）由 HeatableObject 一并负责（液相沸腾蒸发与固相相变共享同一温度），
/// 不在本类处理，也不再有独立的固体加热组件。
/// </summary>
public class SolidSystem : MonoBehaviour
{
    [Header("溶解参数")]
    [Tooltip("每克固体每帧溶解的基准速率（克/秒），再乘以形态系数与溶解度系数")]
    public float baseDissolveRate = 2f;

    [Tooltip("溶解时液体向溶液色靠拢的插值强度（0~1，越大变浓越快）")]
    public float tintStrength = 0.08f;

    [Tooltip("饱和色调：溶质色与水色混合比例，决定溶液最终颜色（0.5=等比例混合）")]
    public float solutionMix = 0.5f;

    [Tooltip("每格水对应的水的质量（克）。默认 1，与 LiquidVolumeUI 的 mLPerCell（1 mL≈1 g 水）保持一致，用于按溶解度计算「饱和上限」")]
    public float waterGramsPerCell = 1f;

    private static SolidSystem s_instance;
    public static SolidSystem Instance
    {
        get
        {
            if (s_instance == null)
            {
                s_instance = FindObjectOfType<SolidSystem>();
                if (s_instance == null)
                    Debug.LogWarning("[SolidSystem] 场景中未找到 SolidSystem，请确保已挂载该组件。");
            }
            return s_instance;
        }
    }

    /// <summary>不触发查找、不打警告的实例访问（供 SolidSource 自注册使用）。</summary>
    internal static SolidSystem InstanceOrNull => s_instance;

    private List<SolidSource> m_solids = new List<SolidSource>();

    private void Awake()
    {
        if (s_instance != null && s_instance != this)
        {
            Destroy(gameObject);
            return;
        }
        s_instance = this;
    }

    private void Start()
    {
        // 收集场景中已静态挂好的固体源。之后运行时实例化的容器由 SolidSource.OnEnable 自注册。
        RefreshSolidCache();
    }

    /// <summary>重新收集场景中所有 SolidSource。</summary>
    public void RefreshSolidCache()
    {
        m_solids.Clear();
        var all = FindObjectsOfType<SolidSource>();
        foreach (var s in all)
            if (s != null) m_solids.Add(s);
    }

    public void RegisterSolid(SolidSource s)
    {
        if (s != null && !m_solids.Contains(s))
            m_solids.Add(s);
    }

    public void UnregisterSolid(SolidSource s)
    {
        if (s != null) m_solids.Remove(s);
    }

    public IReadOnlyList<SolidSource> GetAllSolids() => m_solids;

    /// <summary>
    /// 往指定容器投放固体（运行时便捷入口）。
    /// 容器必须**已经挂好** SolidSource（预制体上静态挂载），否则返回 null 并报警告。
    /// </summary>
    public SolidSource AddSolidToContainer(LiquidSource container, string reagentName, float grams,
                                           SolidForm form = SolidForm.Powder)
    {
        if (container == null) return null;

        SolidSource solid = container.GetComponent<SolidSource>();
        if (solid == null)
        {
            Debug.LogWarning($"[SolidSystem] 容器「{container.name}」上没有 SolidSource 组件，" +
                             "请先在其预制体上挂载 SolidSource（与 LiquidSource 同物体）。");
            return null;
        }

        if (!solid.SetSolid(reagentName, grams, form))
        {
            Debug.LogWarning($"[SolidSystem] 试剂库中找不到固体试剂：{reagentName}");
            return solid;
        }
        RegisterSolid(solid);
        return solid;
    }

    private void Update()
    {
        if (LayerGridPainter.Instance == null) return;
        for (int i = 0; i < m_solids.Count; i++)
        {
            var s = m_solids[i];
            if (s == null) continue;
            TryDissolve(s);
            TryCrystallize(s);
        }
    }

    /// <summary>
    /// 尝试让固体溶解到同容器的液体中：需要液体源非空且区域内有溶剂（水格）。
    /// 溶解表现为固体质量减少 + 液体向溶液色「变浓」（TintWaterInRegion，体积不变）。
    /// </summary>
    private void TryDissolve(SolidSource solid)
    {
        if (solid.IsEmpty()) return;
        ChemicalReagent r = solid.reagentData;
        if (r == null) return;
        // 不溶（solubilitySoluteParts <= 0）：跳过
        if (r.solubilitySoluteParts <= 0f) return;

        LiquidSource liquid = solid.GetPairedLiquidSource();
        if (liquid == null || liquid.IsEmpty()) return;

        // 需要同容器里真有溶剂（水格）
        int waterCount = LayerGridPainter.Instance.GetWaterCountInRegion(liquid.regionColliders);
        if (waterCount <= 0) return;

        // 形态系数：粉末最快、块状最慢
        float formFactor = SolidFormDissolveFactor(solid.solidForm);
        // 溶解度系数（归一化）：溶解度越高溶得越快
        float solvFactor = Mathf.Clamp01(r.solubilitySoluteParts / Mathf.Max(1f, r.solubilityWaterParts));
        float rate = baseDissolveRate * formFactor * (0.3f + 0.7f * solvFactor);
        float dissolveMass = rate * Time.deltaTime;
        dissolveMass = Mathf.Min(dissolveMass, solid.GetMass());
        if (dissolveMass <= 0f) return;

        // 饱和上限：溶液中已溶质量不可超过「当前水量 × 溶解度比」
        //   solubilitySoluteParts / solubilityWaterParts ≈ 每克水最多溶多少克溶质（如 CuSO4 32/100=0.32）
        //   已达饱和则固体不再溶解，留在杯底（undissolved）。
        float solubilityRatio = r.solubilitySoluteParts / Mathf.Max(1f, r.solubilityWaterParts);
        float waterMass = waterCount * waterGramsPerCell;
        float remainingCapacity = waterMass * solubilityRatio - liquid.dissolvedMass;
        if (remainingCapacity <= 0f)
            return;                                   // 饱和，整帧不溶解
        dissolveMass = Mathf.Min(dissolveMass, remainingCapacity);
        if (dissolveMass <= 0f)
            return;

        // 溶液最终色：溶质色与水色混合
        Color solute = solid.GetEffectiveColor();
        Color water = liquid.GetEffectiveColor();
        Color solution = Color.Lerp(water, solute, solutionMix);

        // 让液体「变浓」而不增加体积
        LayerGridPainter.Instance.TintWaterInRegion(liquid.regionColliders, solution, tintStrength);

        // 更新液源标签：若原本是纯溶剂（水/none），则显示为溶质水溶液名
        bool isSolvent = string.IsNullOrEmpty(liquid.liquidType)
            || liquid.liquidType == "Water"
            || liquid.liquidType == LiquidSource.EMPTY_MARKER;
        if (isSolvent)
        {
            liquid.liquidType = solid.solidType + "(aq)";
            liquid.useReagentColor = false;
            liquid.liquidColor = solution;
        }
        else
        {
            liquid.liquidColor = Color.Lerp(liquid.liquidColor, solution, 0.05f);
        }

        solid.ReduceMass(dissolveMass);

        // 记账：固体溶进水里后，水本身只保留「溶液色 + 类型名」，
        // 溶了多少克、什么试剂、什么形态，靠下面三个字段记录——蒸干时据此还原成固体。
        liquid.dissolvedReagentData = r;
        liquid.dissolvedForm = solid.solidForm;
        liquid.dissolvedMass += dissolveMass;
        // 运行时真实溶解发生 → 解除「初始溶液」浓度锁定，改用实际溶解质量计算浓度
        liquid.initialSolutionActive = false;
    }

    /// <summary>
    /// 尝试析出（结晶）：溶解的逆过程。
    /// 当某容器「溶有固体（dissolvedMass>0）且溶液已被蒸干（区域内持续无水）」，
    /// 把溶着的固体还原回固相——盐水蒸干 → 水走了，盐结晶留下来。
    /// 仅在持续无水达到阈值后才触发，避免水位抖动瞬间误判蒸干。
    /// </summary>
    private void TryCrystallize(SolidSource solid)
    {
        LiquidSource liquid = solid.GetPairedLiquidSource();
        if (liquid == null) return;
        if (liquid.dissolvedReagentData == null || liquid.dissolvedMass <= 0.0001f)
        {
            liquid.crystalTicks = 0;
            return;
        }

        // 溶液必须真的蒸干（区域内持续无水）才析出；还有水就只浓缩、不结晶
        int water = LayerGridPainter.Instance.GetWaterCountInRegion(liquid.regionColliders);
        if (water > 0)
        {
            liquid.crystalTicks = 0;
            return;
        }

        liquid.crystalTicks++;
        const int kCrystalThreshold = 8;   // ≈0.4s，与 SyncEmptyContainerTypes 的空判定节奏一致
        if (liquid.crystalTicks < kCrystalThreshold) return;

        // 析出固体：与同容器已有固体同种类则累加质量，否则覆盖
        if (solid.IsEmpty())
            solid.SetSolid(liquid.dissolvedReagentData, liquid.dissolvedMass, liquid.dissolvedForm);
        else if (liquid.dissolvedReagentData == solid.reagentData)
            solid.AddMass(liquid.dissolvedMass);
        else
            solid.SetSolid(liquid.dissolvedReagentData, liquid.dissolvedMass, liquid.dissolvedForm);

        // 清空溶解态记账
        liquid.dissolvedReagentData = null;
        liquid.dissolvedForm = SolidForm.Powder;
        liquid.dissolvedMass = 0f;
        liquid.crystalTicks = 0;
        // 蒸干结晶 → 解除「初始溶液」浓度锁定
        liquid.initialSolutionActive = false;
    }

    private static float SolidFormDissolveFactor(SolidForm form)
    {
        switch (form)
        {
            case SolidForm.Powder: return 1f;
            case SolidForm.Granule: return 0.6f;
            case SolidForm.Crystal: return 0.45f;
            case SolidForm.Flake: return 0.35f;
            case SolidForm.Chunk: return 0.2f;
            default: return 0.5f;
        }
    }
}
