using UnityEngine;

/// <summary>
/// 可燃物体：管理火焰特效的显示/隐藏，以及火焰触发区域。
/// 挂载到可被点燃的物体根对象上（如 Alcohol_Lamp），并配置 Fire 和 FireTrigger 引用。
/// 若配置了 LiquidSource，燃烧时会按 burnRate 速度消耗液体，耗尽后自动熄灭。
/// </summary>
public class FlammableObject : MonoBehaviour
{
    [Tooltip("火焰特效对象（Fire 子对象），将被 SetActive 切换显示")]
    [SerializeField] private GameObject fireObject;

    [Tooltip("火焰触发区域碰撞体（FireTrigger 子对象的碰撞体），用于检测火焰传递")]
    [SerializeField] private Collider2D fireTriggerCollider;

    [Tooltip("液体源（用于燃烧时消耗液体），若为空则燃烧不消耗液体")]
    [SerializeField] private LiquidSource liquidSource;

    [Tooltip("燃烧时液体消耗速度（格/秒），0 表示不消耗")]
    [SerializeField] private float burnRate = 1f;

    private float m_burnTimer = 0f;
    private LayerGridPainter m_gridPainter;

    public bool IsIgnited => fireObject != null && fireObject.activeSelf;

    public Collider2D FireTriggerCollider => fireTriggerCollider;

    private void Start()
    {
        m_gridPainter = FindObjectOfType<LayerGridPainter>();
    }

    private void Update()
    {
        if (IsIgnited && liquidSource != null && burnRate > 0f && m_gridPainter != null)
        {
            m_burnTimer += Time.deltaTime;
            float interval = 1f / burnRate;
            while (m_burnTimer >= interval)
            {
                m_burnTimer -= interval;
                bool consumed = m_gridPainter.RemoveWaterFromRegion(liquidSource.regionColliders);
                if (!consumed)
                {
                    // 液体耗尽，自动熄灭
                    Extinguish();
                    break;
                }
            }
        }
    }

    public void Ignite()
    {
        if (fireObject != null && !fireObject.activeSelf)
        {
            // Debug.Log($"[FlammableObject] {name} 被点燃");
            fireObject.SetActive(true);
        }
    }

    public void Extinguish()
    {
        if (fireObject != null && fireObject.activeSelf)
        {
            // Debug.Log($"[FlammableObject] {name} 被熄灭");
            fireObject.SetActive(false);
        }
        m_burnTimer = 0f;
    }
}
