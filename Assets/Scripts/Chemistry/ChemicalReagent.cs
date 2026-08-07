using UnityEngine;

namespace Chemistry
{
    /// <summary>
    /// 物态枚举
    /// </summary>
    public enum PhysicalState
    {
        Solid,
        Liquid,
        Gas
    }

    /// <summary>
    /// 酸碱性分类
    /// </summary>
    public enum AcidityType
    {
        StrongAcid,
        WeakAcid,
        Neutral,
        WeakBase,
        StrongBase
    }

    /// <summary>
    /// 氧化还原性分类
    /// </summary>
    public enum RedoxProperty
    {
        Oxidizing,
        Reducing,
        Both,
        None
    }

    /// <summary>
    /// 化学试剂数据定义（ScriptableObject）
    /// 可在 Project 窗口通过右键 Create > Chemistry > Reagent 创建实例
    /// </summary>
    [CreateAssetMenu(fileName = "NewReagent", menuName = "Chemistry/Reagent")]
    public class ChemicalReagent : ScriptableObject
    {
        [Header("基本信息")]
        [Tooltip("试剂中文名称")]
        public string reagentName;

        [Tooltip("试剂英文名称")]
        public string englishName;

        [Tooltip("化学式")]
        public string chemicalFormula;

        [Tooltip("别名/俗名")]
        public string[] aliases;

        [Tooltip("CAS号")]
        public string casNumber;

        [Header("物理性质")]
        [Tooltip("在模拟中显示的液体颜色")]
        public Color displayColor = ChemistryConstants.DefaultLiquidColor;

        [Tooltip("常温常压下的物态")]
        public PhysicalState defaultState = PhysicalState.Liquid;

        [Tooltip("密度 (g/cm³)")]
        public float density = 1f;

        [Tooltip("沸点 (°C)，不适用请填 NaN")]
        public float boilingPoint = float.NaN;

        [Tooltip("常温蒸发速率（格/秒）：液体在室温、无加热时的缓慢挥发速度。不适用/未设置请填 NaN，届时回退到 HeatableObject 的 defaultRoomTempEvaporationRate。乙醇等易挥发液体可设较大值")]
        public float evaporationRate = float.NaN;

        [Tooltip("沸腾蒸发速率（格/秒）：液体被加热到沸点、持续沸腾时的蒸发速度。不适用/未设置请填 NaN，届时回退到 HeatableObject 的 defaultEvaporationRate。通常远大于常温蒸发速率")]
        public float boilingEvaporationRate = float.NaN;

        [Tooltip("熔点 (°C)，不适用请填 NaN")]
        public float meltingPoint = float.NaN;

        [Header("热致相变（固体受热行为）")]
        [Tooltip("升华温度 (°C)：加热到该温度时固体直接变为气体（如碘、干冰）。不适用/非升华固体请填 NaN")]
        public float sublimationPoint = float.NaN;

        [Tooltip("分解温度 (°C)：加热到该温度时固体发生分解反应（如碳酸钙→氧化钙+二氧化碳）。不适用请填 NaN")]
        public float decompositionTemp = float.NaN;

        [Tooltip("分解产物试剂名（对应数据库中的 ChemicalReagent.reagentName / englishName / chemicalFormula）。分解后本固体变为该产物；留空表示无固体产物（仅产气）。例如碳酸钙分解填 \"CaO\"")]
        public string decompositionProductName;

        [Tooltip("分解产生的气体名（仅用于视觉上喷气泡表示逸出，不参与化学计量）。例如碳酸钙分解填 \"CO2\"")]
        public string decompositionGasName;

        [Header("在水中的溶解性（溶解度，相对水）")]
        [Tooltip("溶解度以「份数比」表示：solubilityWaterParts 份水可溶解 solubilitySoluteParts 份该物质。例如 100 份水溶解 32 份 → 填 water=100, solute=32；完全混溶可填较大值（如 100）")]
        public float solubilityWaterParts = 100f;
        [Tooltip("可溶解的该物质份数（对应 solubilityWaterParts 份水）。0 表示不溶/不适用")]
        public float solubilitySoluteParts = 0f;

        [Header("水溶液默认浓度（作为初始溶液时）")]
        [Tooltip("该试剂作为「可溶于水的固体溶质」时勾选（例如无水硫酸铜 CuSO₄）。勾选后：把它直接配成液体即视为其水溶液、液体标签自动补 (aq) 后缀，并按 defaultSolutionConcentration 显示初始浓度；配成液体时浓度数值的来源就是本试剂（不取自任何「溶液态」资产）。纯水/纯乙醇等真正纯液体不应勾选。")]
        public bool isAqueousSolution = false;
        [Tooltip("该试剂配成水溶液时的「最常见浓度」(质量分数 %)。当容器开局就装此溶液（而非运行时溶解产生）时显示此值，但不会超过该物质在水中的真实最大浓度（由下方 solubilitySoluteParts/solubilityWaterParts 推算）。运行时再往里加溶质，浓度会在该值基础上继续上涨。常见参考：硫酸铜约 10~24%、食盐约 5~26%。0 表示未指定初始浓度。")]
        [Range(0f, 100f)]
        public float defaultSolutionConcentration = 10f;

        [Header("化学性质")]
        [Tooltip("酸碱性分类")]
        public AcidityType acidityType = AcidityType.Neutral;

        [Tooltip("pH值（如果是溶液），不适用请填 NaN")]
        public float pHValue = float.NaN;

        [Tooltip("氧化还原性")]
        public RedoxProperty redoxProperty = RedoxProperty.None;

        [Header("安全信息")]
        [Tooltip("是否易燃")]
        public bool isFlammable = false;

        [Tooltip("是否有毒")]
        public bool isToxic = false;

        [Tooltip("是否有腐蚀性")]
        public bool isCorrosive = false;

        [Tooltip("是否易挥发")]
        public bool isVolatile = false;

        [Tooltip("安全注意事项")]
        [TextArea(2, 5)]
        public string safetyNotes;

        [Header("描述")]
        [Tooltip("试剂详细描述")]
        [TextArea(3, 8)]
        public string description;

        /// <summary>
        /// 获取显示用的颜色，若未设置则返回默认水色
        /// </summary>
        public Color GetDisplayColor()
        {
            if (displayColor.a > 0.001f)
                return displayColor;
            return ChemistryConstants.DefaultLiquidColor;
        }

        /// <summary>
        /// 检查名称是否匹配（支持主名称、英文名称、别名）
        /// </summary>
        public bool MatchesName(string query)
        {
            if (string.IsNullOrEmpty(query)) return false;
            query = query.Trim();
            if (reagentName == query) return true;
            if (englishName == query) return true;
            if (chemicalFormula == query) return true;
            if (aliases != null)
            {
                foreach (var alias in aliases)
                {
                    if (alias == query) return true;
                }
            }
            return false;
        }
    }
}
