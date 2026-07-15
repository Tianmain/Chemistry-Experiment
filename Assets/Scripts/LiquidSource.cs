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
    public Color liquidColor = new Color(0.2f, 0.5f, 1f, 0.6f);

    [Tooltip("是否优先使用关联试剂的颜色")]
    public bool useReagentColor = true;

    [Tooltip("用于确定液体区域的多边形碰撞器（留空则自动获取自身及子物体的所有 Collider2D）")]
    public Collider2D[] regionColliders;

    /// <summary>
    /// 碰撞器是否已收集过（避免 ContainsPoint 重复调用 AutoCollectColliders）
    /// </summary>
    private bool m_collidersCollected = false;

    private void OnValidate()
    {
        AutoCollectColliders();
        SyncFromReagent();
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