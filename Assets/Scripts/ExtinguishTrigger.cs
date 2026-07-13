using UnityEngine;

/// <summary>
/// 熄灭触发器：当任何正在燃烧的火焰触发器进入该碰撞体区域时，熄灭对应火焰。
/// 挂载到熄灭区域对象上，并配置 extinguishCollider 引用。
/// </summary>
public class ExtinguishTrigger : MonoBehaviour
{
    [Tooltip("熄灭区域碰撞体（任意 Collider2D，建议设置为触发器）")]
    [SerializeField] private Collider2D extinguishCollider;

    [Tooltip("中心距离阈值：当 extinguishCollider 与目标碰撞体中心距离超过此值时，不执行熄灭。默认 0.08")]
    [SerializeField] private float centerDistanceThreshold = 0.08f;

    private Collider2D[] m_overlapBuffer = new Collider2D[16];

    private void Update()
    {
        if (extinguishCollider == null) return;

        ContactFilter2D filter = new ContactFilter2D();
        filter.useTriggers = true;
        filter.SetLayerMask(Physics2D.AllLayers);

        int count = Physics2D.OverlapCollider(extinguishCollider, filter, m_overlapBuffer);
        // if (count > 0)
        // {
        //     Debug.Log($"[ExtinguishTrigger] {name} 检测到 {count} 个碰撞体:");
        //     for (int i = 0; i < count; i++)
        //     {
        //         if (m_overlapBuffer[i] != null)
        //             Debug.Log($"  - [{i}] {m_overlapBuffer[i].name} (on {m_overlapBuffer[i].transform.root.name})");
        //     }
        // }
        for (int i = 0; i < count; i++)
        {
            Collider2D other = m_overlapBuffer[i];
            if (other == null || other == extinguishCollider) continue;

            // 只有当 extinguishCollider 中心与目标碰撞体中心足够接近时才熄灭
            // 防止灯帽稍微偏离时（视觉上已移开但仍重叠）误熄灭
            // 默认 Alcohol_Lamp 上：ExtinguishRegion 中心 Y=1.28，FireTrigger 中心 Y=1.3，距离仅 0.02
            // 水平移开 1 格(0.25)后，中心距离约 0.25，但碰撞体仍可能重叠，因此阈值必须足够小
            float centerDist = Vector2.Distance(extinguishCollider.bounds.center, other.bounds.center);
            if (centerDist > centerDistanceThreshold)
            {
                // Debug.Log($"[ExtinguishTrigger] 跳过 {other.name}，中心距离 {centerDist:F3} > {centerDistanceThreshold}");
                continue;
            }

            // 优先查找 FlammableObject（酒精灯等可燃物体）
            FlammableObject flammable = other.GetComponent<FlammableObject>();
            if (flammable == null)
            {
                flammable = other.GetComponentInParent<FlammableObject>();
            }

            if (flammable != null && flammable.IsIgnited)
            {
                // Debug.Log($"[ExtinguishTrigger] 熄灭 {flammable.name}");
                flammable.Extinguish();
                continue;
            }

            // 再查找 IgniterController（点火器）
            IgniterController igniter = other.GetComponent<IgniterController>();
            if (igniter == null)
            {
                igniter = other.GetComponentInParent<IgniterController>();
            }

            if (igniter != null && igniter.IsIgnited)
            {
                // Debug.Log($"[ExtinguishTrigger] 熄灭 {igniter.name}");
                igniter.Extinguish();
            }
        }
    }
}
