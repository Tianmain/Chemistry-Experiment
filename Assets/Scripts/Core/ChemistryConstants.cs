using UnityEngine;

/// <summary>
/// 化学实验项目的跨脚本共享常量。
/// 把在多个脚本中重复出现的「同义默认值」集中到一处，
/// 避免魔法值散落各处、改一处却漏改另一处。
/// </summary>
public static class ChemistryConstants
{
    /// <summary>
    /// 默认液体 / 水颜色（淡蓝半透明）。
    /// 同时作为以下默认值的统一来源：
    ///   - LayerGridPainter.waterColor（水格渲染色）
    ///   - LiquidSource.liquidColor（液体源默认色）
    ///   - ChemicalReagent.displayColor 及其 GetDisplayColor() 回退值
    /// 若想统一调整默认水色，只改这里即可。
    /// </summary>
    public static readonly Color DefaultLiquidColor = new Color(0.2f, 0.5f, 1f, 0.6f);
}
