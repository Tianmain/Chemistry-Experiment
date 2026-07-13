using UnityEngine;

/// <summary>
/// 点火器控制器：点击 Trigger 碰撞体区域并抬起鼠标时，切换火焰的显示/隐藏状态。
/// 挂载到 Igniter 根对象上，需要在 Inspector 中配置 triggerCollider 和 fireObject。
/// </summary>
public class IgniterController : MonoBehaviour
{
    [Tooltip("Trigger 子对象的碰撞体（isTrigger = true 的多边形碰撞体）")]
    [SerializeField] private Collider2D triggerCollider;

    [Tooltip("火焰特效对象（Fire 子对象），将被 SetActive 切换显示")]
    [SerializeField] private GameObject fireObject;

    [Tooltip("火焰区域碰撞体（表示火焰影响范围的触发器，可单独设置为一个子对象的碰撞体）")]
    [SerializeField] private Collider2D fireAreaCollider;

    private bool m_isMouseDownOnTrigger = false;
    private Vector3 m_positionOnMouseDown;

    private Collider2D[] m_fireOverlapBuffer = new Collider2D[16];

    private void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Vector2 mouseWorldPos = GetMouseWorldPos();
            if (IsPointInTrigger(mouseWorldPos))
            {
                m_isMouseDownOnTrigger = true;
                m_positionOnMouseDown = transform.position;
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (m_isMouseDownOnTrigger)
            {
                Vector2 mouseWorldPos = GetMouseWorldPos();
                // 若物体在按下到抬起期间发生了位移，说明本次操作是拖拽，不执行点燃
                bool hasDragged = Vector3.Distance(transform.position, m_positionOnMouseDown) > 0.001f;
                if (!hasDragged && IsPointInTrigger(mouseWorldPos))
                {
                    ToggleFire();
                }
            }
            m_isMouseDownOnTrigger = false;
        }

        // 火焰传递：若自身处于点燃状态，检测火焰区域是否触碰到其他可燃物体
        if (fireObject != null && fireObject.activeSelf && fireAreaCollider != null)
        {
            TransferFire();
        }
    }

    /// <summary>
    /// 检测火焰区域与其他可燃物体的 FireTrigger 是否重叠，若重叠则点燃对方。
    /// </summary>
    private void TransferFire()
    {
        ContactFilter2D filter = new ContactFilter2D();
        filter.useTriggers = true;
        filter.SetLayerMask(Physics2D.AllLayers);

        int count = Physics2D.OverlapCollider(fireAreaCollider, filter, m_fireOverlapBuffer);
        // if (count > 0)
        // {
        //     Debug.Log($"[Igniter] OverlapCollider 检测到 {count} 个碰撞体:");
        //     for (int i = 0; i < count; i++)
        //     {
        //         if (m_fireOverlapBuffer[i] != null)
        //             Debug.Log($"  - [{i}] {m_fireOverlapBuffer[i].name} (on {m_fireOverlapBuffer[i].transform.root.name})");
        //     }
        // }
        for (int i = 0; i < count; i++)
        {
            Collider2D other = m_fireOverlapBuffer[i];
            if (other == null || other == fireAreaCollider) continue;
            if (other.transform.IsChildOf(transform)) continue;

            // 优先在碰撞体所在物体上查找 FlammableObject，否则向上查找
            FlammableObject flammable = other.GetComponent<FlammableObject>();
            if (flammable == null)
            {
                flammable = other.GetComponentInParent<FlammableObject>();
            }

            if (flammable != null)
            {
                // Debug.Log($"[Igniter] 找到 FlammableObject: {flammable.name}, IsIgnited={flammable.IsIgnited}");
                if (!flammable.IsIgnited)
                {
                    // Debug.Log($"[Igniter] 调用 {flammable.name}.Ignite()");
                    flammable.Ignite();
                }
            }
        }
    }

    private Vector2 GetMouseWorldPos()
    {
        if (Camera.main != null)
        {
            return Camera.main.ScreenToWorldPoint(Input.mousePosition);
        }
        return Vector2.zero;
    }

    private bool IsPointInTrigger(Vector2 point)
    {
        if (triggerCollider == null) return false;
        return triggerCollider.OverlapPoint(point);
    }

    public bool IsIgnited => fireObject != null && fireObject.activeSelf;

    /// <summary>
    /// 熄灭火焰（仅可由 ExtinguishTrigger 等外部系统调用）
    /// </summary>
    public void Extinguish()
    {
        if (fireObject != null && fireObject.activeSelf)
        {
            fireObject.SetActive(false);
        }
    }

    private void ToggleFire()
    {
        if (fireObject != null)
        {
            fireObject.SetActive(!fireObject.activeSelf);
        }
    }

    private void OnDisable()
    {
        m_isMouseDownOnTrigger = false;
    }
}
