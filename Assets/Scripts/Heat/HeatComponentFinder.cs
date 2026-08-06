using UnityEngine;

/// <summary>
/// 在碰撞体所在物体上查找火焰相关组件（FlammableObject / IgniterController）的共享工具。
/// 统一 "优先 GetComponent，否则 GetComponentInParent" 的查找顺序，
/// 避免 IgniterController / ExtinguishTrigger / HeatableObject 重复同一段逻辑。
/// </summary>
public static class HeatComponentFinder
{
    /// <summary>
    /// 从碰撞体所在物体查找可燃/点火组件。
    /// </summary>
    /// <param name="other">被检测的碰撞体</param>
    /// <param name="flammable">找到的可燃物体（未找到为 null）</param>
    /// <param name="igniter">找到的点火器（未找到为 null）</param>
    public static void Find(Collider2D other, out FlammableObject flammable, out IgniterController igniter)
    {
        flammable = null;
        igniter = null;
        if (other == null) return;

        flammable = other.GetComponent<FlammableObject>();
        if (flammable == null)
            flammable = other.GetComponentInParent<FlammableObject>();

        igniter = other.GetComponent<IgniterController>();
        if (igniter == null)
            igniter = other.GetComponentInParent<IgniterController>();
    }
}
