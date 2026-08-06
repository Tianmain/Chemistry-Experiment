using UnityEngine;

/// <summary>
/// 拖拽时自动脱离父子关系：当该物体的位置发生变化（被拖拽）时，
/// 立即将 parent 设为 null，使其成为独立对象，避免带动父物体或受父物体碰撞影响。
/// 挂载到需要独立拖拽的子对象上（如 Lamp_Cap）。
/// </summary>
public class DetachOnDrag : MonoBehaviour
{
    private Vector3 m_lastPosition;
    private bool m_detached = false;

    private void Start()
    {
        m_lastPosition = transform.position;
    }

    private void Update()
    {
        if (m_detached) return;

        if (Vector3.Distance(transform.position, m_lastPosition) > 0.001f)
        {
            Vector3 worldPos = transform.position;
            transform.SetParent(null);
            transform.position = worldPos;
            m_detached = true;
        }
    }
}
