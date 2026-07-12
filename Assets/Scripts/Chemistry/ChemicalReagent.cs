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
        public Color displayColor = new Color(0.2f, 0.5f, 1f, 0.6f);

        [Tooltip("常温常压下的物态")]
        public PhysicalState defaultState = PhysicalState.Liquid;

        [Tooltip("密度 (g/cm³)")]
        public float density = 1f;

        [Tooltip("沸点 (°C)，不适用请填 NaN")]
        public float boilingPoint = float.NaN;

        [Tooltip("熔点 (°C)，不适用请填 NaN")]
        public float meltingPoint = float.NaN;

        [Tooltip("在水中的溶解性描述")]
        public string solubilityInWater = "可溶";

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
            return new Color(0.2f, 0.5f, 1f, 0.6f);
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
