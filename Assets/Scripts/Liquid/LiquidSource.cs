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

    /// <summary>
    /// 空容器标记词：当试剂名或液体类型为 "none" 时，视为「可装液体但目前无液体」的空容器。
    /// 这类容器不参与初始灌水，标签只显示 "none"。
    /// </summary>
    public const string EMPTY_MARKER = "none";

    /// <summary>
    /// 碰撞器是否已收集过（避免 ContainsPoint 重复调用 AutoCollectColliders）
    /// </summary>
    private bool m_collidersCollected = false;

    private void OnValidate()
    {
        AutoCollectColliders();
        SyncFromReagent();
        // 若试剂名 / 类型 / 资产文件名为 "none"，自动标记为空容器（持久化到序列化字段，运行时稳定）
        if (!isEmptyContainer && NameLooksEmpty())
            isEmptyContainer = true;
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