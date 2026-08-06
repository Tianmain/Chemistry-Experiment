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
    private ContactFilter2D m_fireContactFilter;
    private Camera m_cachedCamera;

    private void Awake()
    {
        m_fireContactFilter = new ContactFilter2D();
        m_fireContactFilter.useTriggers = true;
        m_fireContactFilter.SetLayerMask(Physics2D.AllLayers);
    }

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
        int count = Physics2D.OverlapCollider(fireAreaCollider, m_fireContactFilter, m_fireOverlapBuffer);
        for (int i = 0; i < count; i++)
        {
            Collider2D other = m_fireOverlapBuffer[i];
            if (other == null || other == fireAreaCollider) continue;
            if (other.transform.IsChildOf(transform)) continue;

            // 优先在碰撞体所在物体上查找 FlammableObject，否则向上查找
            HeatComponentFinder.Find(other, out FlammableObject flammable, out _);

            if (flammable != null && !flammable.IsIgnited)
                flammable.Ignite();
        }
    }

    private Vector2 GetMouseWorldPos()
    {
        if (m_cachedCamera == null)
            m_cachedCamera = Camera.main;
        if (m_cachedCamera == null)
            return Vector2.zero;
        return m_cachedCamera.ScreenToWorldPoint(Input.mousePosition);
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
            fireObject.SetActive(false);
    }

    private void ToggleFire()
    {
        if (fireObject != null)
            fireObject.SetActive(!fireObject.activeSelf);
    }

    private void OnDisable()
    {
        m_isMouseDownOnTrigger = false;
    }
}