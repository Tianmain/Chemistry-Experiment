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
    private ContactFilter2D m_contactFilter;

    private void Awake()
    {
        m_contactFilter = new ContactFilter2D();
        m_contactFilter.useTriggers = true;
        m_contactFilter.SetLayerMask(Physics2D.AllLayers);
    }

    private void Update()
    {
        if (extinguishCollider == null) return;

        int count = Physics2D.OverlapCollider(extinguishCollider, m_contactFilter, m_overlapBuffer);
        for (int i = 0; i < count; i++)
        {
            Collider2D other = m_overlapBuffer[i];
            if (other == null || other == extinguishCollider) continue;

            // 只有当 extinguishCollider 中心与目标碰撞体中心足够接近时才熄灭
            // 防止灯帽稍微偏离时（视觉上已移开但仍重叠）误熄灭
            float centerDist = Vector2.Distance(extinguishCollider.bounds.center, other.bounds.center);
            if (centerDist > centerDistanceThreshold)
                continue;

            // 优先查找 FlammableObject（酒精灯等可燃物体）
            FlammableObject flammable = other.GetComponent<FlammableObject>();
            if (flammable == null)
                flammable = other.GetComponentInParent<FlammableObject>();

            if (flammable != null && flammable.IsIgnited)
            {
                flammable.Extinguish();
                continue;
            }

            // 再查找 IgniterController（点火器）
            IgniterController igniter = other.GetComponent<IgniterController>();
            if (igniter == null)
                igniter = other.GetComponentInParent<IgniterController>();

            if (igniter != null && igniter.IsIgnited)
                igniter.Extinguish();
        }
    }
}