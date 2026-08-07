using UnityEngine;
using System.Collections.Generic;
using Chemistry;

/// <summary>
/// 拖拽 / 旋转控制子系统：处理左键拖拽物体、倾倒、右键 90° 旋转杯体并让内部液体随杯刚性旋转。
/// 自身持有大部分拖拽/旋转状态字段；与渲染共享的少数状态（m_isDragging / m_draggedObject /
/// m_dragOffsetX/Y / m_draggedCoverage）保留在协调器上（internal），渲染器据此做视觉偏移。
/// 直接读写 LiquidGrid 提交拖拽/旋转结果。
/// </summary>
public class DragController
{
    private readonly LayerGridPainter m_owner;
    private readonly LiquidGrid m_grid;

    // 以下为拖拽/旋转自身状态（渲染器不读取，故内聚于此）
    private bool m_isPouring = false;       // 是否处于倾倒中（已松开粘合，交给水模拟）
    private bool m_pourStarted = false;     // 本次拖拽是否已经触发过一次倾倒

    private Vector3 m_dragStartMousePos;
    private Vector3 m_dragStartObjPos;
    private Quaternion m_dragStartObjRot;
    private Collider2D m_draggedCollider;
    private Vector3 m_lastValidDragPos;
    private Bounds m_draggedObjOriginalBounds;

    private bool m_isRotating = false;      // 是否正在右键拖拽旋转
    private GameObject m_rotTarget;        // 右键旋转目标（即左键拖拽的物体）
    private Quaternion m_rotStartObjRot;    // 右键旋转开始时的物体姿态（旋转基准）
    private float m_rotStartAngle = 0f;     // 右键按下时鼠标相对物体中心的角度（度）

    private GameObject m_lastDraggedObject;  // 最近一次被左键拖拽的物体（右键旋转结束即清空）

    // 右键旋转期间：把烧杯内容物（水+杯壁障碍）随杯体一起刚性旋转所需的快照与状态
    private List<int> m_rotSnapCol = new List<int>();
    private List<int> m_rotSnapRow = new List<int>();
    private List<CellState> m_rotSnapState = new List<CellState>();
    private List<Color> m_rotSnapColor = new List<Color>();
    private List<Vector2Int> m_rotLastWritten = new List<Vector2Int>();
    private float m_rotCenterColF = 0f;
    private float m_rotCenterRowF = 0f;

    private Rigidbody2D m_draggedRigidbody;
    private bool m_wasKinematic = false;
    private int m_combinedCollisionMask;

    private Collider2D[] m_draggedObjColliders; // 被拖拽物体的碰撞器缓存（用于精确偏移格子）

    private struct RigidbodyState
    {
        public Rigidbody2D rb;
        public bool wasKinematic;
    }
    private List<RigidbodyState> m_otherRigidbodyStates = new List<RigidbodyState>();

    public DragController(LayerGridPainter owner)
    {
        m_owner = owner;
        m_grid = owner.gridData;
    }

    /// <summary>当前是否正在右键旋转（旋转期间暂停水模拟）</summary>
    public bool IsRotating => m_isRotating;

    /// <summary>当前是否处于倾倒中（已松开粘合，由水模拟接管流出）</summary>
    public bool IsPouring => m_isPouring;

    /// <summary>
    /// 判断碰撞物体的 Tag 是否应该被忽略（可穿透）
    /// 优先级：绝对不可穿透 > 互阻规则 > 拖拽物体本身可穿透 > 障碍物可穿透
    /// </summary>
    private bool ShouldIgnoreCollision(string obstacleTag)
    {
        if (string.IsNullOrEmpty(obstacleTag)) return false;

        // 1. 绝对不可穿透：任何物体都不能穿过的障碍物
        if (m_owner.m_impassableTagSet != null && m_owner.m_impassableTagSet.Contains(obstacleTag))
            return false;

        // 2. 互阻规则：两个 Tag 之间互相阻碍
        if (m_owner.m_draggedObject != null && m_owner.mutualBlockRules != null)
        {
            string draggedTag = m_owner.m_draggedObject.tag;
            for (int i = 0; i < m_owner.mutualBlockRules.Length; i++)
            {
                if (m_owner.mutualBlockRules[i] == null) continue;
                if ((m_owner.mutualBlockRules[i].tagA == draggedTag && m_owner.mutualBlockRules[i].tagB == obstacleTag)
                 || (m_owner.mutualBlockRules[i].tagA == obstacleTag && m_owner.mutualBlockRules[i].tagB == draggedTag))
                {
                    return false; // 这两个 Tag 之间互相阻碍
                }
            }
        }

        // 4. 如果被拖拽的物体本身是可穿透的，那么它也可以穿过其他非绝对不可穿透的物体
        if (m_owner.m_draggedObject != null && m_owner.m_penetrableTagSet != null && m_owner.m_penetrableTagSet.Contains(m_owner.m_draggedObject.tag))
            return true;

        // 5. 障碍物是可穿透的：别人穿过它时允许
        if (m_owner.m_penetrableTagSet != null && m_owner.m_penetrableTagSet.Contains(obstacleTag))
            return true;

        return false;
    }

    /// <summary>
    /// 获取当前拖拽物体对指定障碍物 Tag 的碰撞容差
    /// 容差越大越难触发碰撞（需要更深的重叠才会被阻挡）
    /// </summary>
    private float GetCollisionTolerance(string obstacleTag)
    {
        if (m_owner.m_draggedObject != null && m_owner.mutualBlockRules != null)
        {
            string draggedTag = m_owner.m_draggedObject.tag;
            for (int i = 0; i < m_owner.mutualBlockRules.Length; i++)
            {
                if (m_owner.mutualBlockRules[i] == null) continue;
                if ((m_owner.mutualBlockRules[i].tagA == draggedTag && m_owner.mutualBlockRules[i].tagB == obstacleTag)
                 || (m_owner.mutualBlockRules[i].tagA == obstacleTag && m_owner.mutualBlockRules[i].tagB == draggedTag))
                {
                    return m_owner.mutualBlockRules[i].collisionTolerance;
                }
            }
        }
        return 0f;
    }

    /// <summary>
    /// 获取被拖拽物体自身及其所有子物体的非 Trigger 碰撞器
    /// </summary>
    private Collider2D[] GetDraggedColliders()
    {
        if (m_owner.m_draggedObject == null) return System.Array.Empty<Collider2D>();
        Collider2D[] all = m_owner.m_draggedObject.GetComponentsInChildren<Collider2D>();
        int count = 0;
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && !all[i].isTrigger) count++;
        if (count == 0) return System.Array.Empty<Collider2D>();
        Collider2D[] result = new Collider2D[count];
        int idx = 0;
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && !all[i].isTrigger) result[idx++] = all[i];
        return result;
    }

    /// <summary>
    /// 判断两个 Transform 是否拥有同一个直接父物体（用于识别同组器具，如酒精灯与灯帽）
    /// </summary>
    private bool HasSameParent(Transform a, Transform b)
    {
        if (a == null || b == null) return false;
        if (a.parent == null || b.parent == null) return false;
        return a.parent == b.parent;
    }

    /// <summary>
    /// 在拖拽开始时缓存每个格子是否被被拖拽物体覆盖（基于原始位置）。
    /// 碰撞器直接覆盖的格子（容器壁）始终缓存；
    /// 容器内的水通过 LiquidSource 的区域碰撞器检测，使液体跟随拖拽同步移动。
    /// </summary>
    private void CacheDraggedCoverage()
    {
        if (m_draggedObjColliders == null || m_grid.Columns <= 0 || m_grid.Rows <= 0) return;

        m_owner.m_draggedCoverage = new bool[m_grid.Columns * m_grid.Rows];

        // 1. 缓存碰撞器直接覆盖的格子（容器壁）
        for (int row = 0; row < m_grid.Rows; row++)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                Vector2 cellCenter = m_grid.GetWorldPosition(col, row);
                foreach (var draggedCol in m_draggedObjColliders)
                {
                    if (draggedCol != null && !draggedCol.isTrigger && draggedCol.OverlapPoint(cellCenter))
                    {
                        m_owner.m_draggedCoverage[col + row * m_grid.Columns] = true;
                        break;
                    }
                }
            }
        }

        // 2. 查找物体及其子物体中的所有 LiquidSource
        if (m_owner.m_draggedObject == null) return;
        LiquidSource[] liquidSources = m_owner.m_draggedObject.GetComponentsInChildren<LiquidSource>();
        if (liquidSources == null || liquidSources.Length == 0) return;

        // 3. 通过 LiquidSource 的区域碰撞器检测容器内的水格子
        //    同时：如果 Water 格子正下方是被拖拽物体覆盖的格子（杯底），也标记为跟随
        //    （防止最底层水因 LiquidSource 碰撞器边界漏检而留在原地）
        for (int row = 1; row < m_grid.Rows - 1; row++)
        {
            for (int col = 1; col < m_grid.Columns - 1; col++)
            {
                // 水和气泡都需要跟随容器拖拽
                if (m_grid.Cells[col, row] != CellState.Water && m_grid.Cells[col, row] != CellState.Bubble) continue;
                int idx = col + row * m_grid.Columns;
                if (m_owner.m_draggedCoverage[idx]) continue;

                Vector2 cellCenter = m_grid.GetWorldPosition(col, row);
                bool shouldMark = false;

                // LiquidSource 区域检测
                foreach (var ls in liquidSources)
                {
                    if (ls != null && ls.ContainsPoint(cellCenter))
                    {
                        shouldMark = true;
                        break;
                    }
                }

                // 正下方是被拖拽物体覆盖的格子（杯底）
                if (!shouldMark && row > 0)
                {
                    int belowIdx = col + (row - 1) * m_grid.Columns;
                    if (m_owner.m_draggedCoverage[belowIdx])
                        shouldMark = true;
                }

                if (shouldMark)
                    m_owner.m_draggedCoverage[idx] = true;
            }
        }

        // 4. 向上连通泛洪：从任意被覆盖格开始，向上把连续的 水/气泡 格也标记为跟随，
        //    使“搁在杯壁/网格上的整柱水”随物体一起平移，而不只贴表面一格。
        //    这样拖动三脚架（带动石棉网及其上的水）时，网面上的水能整体跟随，不会留在原地。
        for (int col = 1; col < m_grid.Columns - 1; col++)
        {
            bool chainActive = false;
            for (int row = 1; row < m_grid.Rows - 1; row++)
            {
                int idx = col + row * m_grid.Columns;
                if (m_owner.m_draggedCoverage[idx])
                {
                    chainActive = true;   // 遇到被覆盖格，启动向上链路
                    continue;
                }
                CellState st = m_grid.Cells[col, row];
                bool isLiquid = st == CellState.Water || st == CellState.Bubble;
                if (chainActive && isLiquid)
                    m_owner.m_draggedCoverage[idx] = true;
                else if (!isLiquid)
                    chainActive = false;  // 遇到非液体且非覆盖格 → 链路断开
            }
        }
    }

    /// <summary>
    /// 检测当前拖拽物体的任意碰撞器是否与障碍物发生穿透
    /// 父子关系物体之间使用宽松阈值（允许轻微套合），独立物体之间使用严格阈值
    /// </summary>
    private bool IsOverlappingWithObstacles()
    {
        // 复用拖拽开始时缓存的碰撞器，避免每帧重新分配数组（拖拽期间碰撞器集合不变）
        Collider2D[] colliders = m_draggedObjColliders ?? System.Array.Empty<Collider2D>();
        if (colliders.Length == 0) return false;

        foreach (var col in colliders)
        {
            if (col == null || col.isTrigger) continue;
            m_owner.m_contactFilter.SetLayerMask(m_combinedCollisionMask);
            int count = Physics2D.OverlapCollider(col, m_owner.m_contactFilter, m_owner.m_colliderBuffer);
            for (int i = 0; i < count; i++)
            {
                if (m_owner.m_colliderBuffer[i] != null && m_owner.m_colliderBuffer[i].gameObject != m_owner.m_draggedObject)
                {
                    if (m_owner.m_colliderBuffer[i].isTrigger)
                        continue;
                    if (ShouldIgnoreCollision(m_owner.m_colliderBuffer[i].tag))
                        continue;

                    bool isRelated = m_owner.m_colliderBuffer[i].transform.IsChildOf(m_owner.m_draggedObject.transform)
                                  || m_owner.m_draggedObject.transform.IsChildOf(m_owner.m_colliderBuffer[i].transform)
                                  || HasSameParent(m_owner.m_draggedObject.transform, m_owner.m_colliderBuffer[i].transform);
                    float baseThreshold = isRelated ? -m_owner.parentChildCollisionTolerance : -0.001f;
                    float tolerance = GetCollisionTolerance(m_owner.m_colliderBuffer[i].tag);
                    float threshold = baseThreshold - tolerance;

                    var dist = Physics2D.Distance(col, m_owner.m_colliderBuffer[i]);
                    if (dist.isValid && dist.distance < threshold)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 获取所有可拖拽物体所在的 Layer 掩码
    /// </summary>
    private int GetDraggableLayerMask()
    {
        int mask = 0;
        foreach (string tag in m_owner.draggableTags)
        {
            GameObject[] objs = null;
            try
            {
                objs = GameObject.FindGameObjectsWithTag(tag);
            }
            catch (UnityException)
            {
                continue;
            }
            foreach (GameObject obj in objs)
            {
                if (obj == null) continue;
                mask |= 1 << obj.layer;
            }
        }
        return mask;
    }

    /// <summary>
    /// 冻结场景中除当前拖拽物体外的其他可拖拽物体
    /// </summary>
    private void FreezeOtherDraggableObjects()
    {
        m_otherRigidbodyStates.Clear();
        foreach (string tag in m_owner.draggableTags)
        {
            GameObject[] objs = null;
            try
            {
                objs = GameObject.FindGameObjectsWithTag(tag);
            }
            catch (UnityException)
            {
                continue;
            }
            foreach (GameObject obj in objs)
            {
                if (obj == null || obj == m_owner.m_draggedObject) continue;
                Rigidbody2D rb = obj.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    m_otherRigidbodyStates.Add(new RigidbodyState { rb = rb, wasKinematic = rb.isKinematic });
                    rb.isKinematic = true;
                }
            }
        }
    }

    /// <summary>
    /// 恢复被冻结的其他可拖拽物体的物理状态
    /// </summary>
    private void RestoreOtherDraggableObjects()
    {
        foreach (var state in m_otherRigidbodyStates)
        {
            if (state.rb != null)
            {
                state.rb.isKinematic = state.wasKinematic;
            }
        }
        m_otherRigidbodyStates.Clear();
    }

    /// <summary>
    /// 每帧处理拖拽输入（由协调器 Update 调用）
    /// </summary>
    public void HandleDrag()
    {
        Camera cam = m_owner.GetCachedCamera();
        if (cam == null) return;

        if (Input.GetMouseButtonDown(0))
        {
            Vector2 mousePos = cam.ScreenToWorldPoint(Input.mousePosition);
            // 遍历鼠标位置下的所有碰撞器，找到可拖拽的物体
            Collider2D[] hits = Physics2D.OverlapPointAll(mousePos);
            Collider2D targetHit = null;
            foreach (var h in hits)
            {
                if (h != null && !h.isTrigger && m_owner.m_draggableTagSet.Contains(h.tag))
                {
                    targetHit = h;
                    break;
                }
            }

            if (targetHit != null)
            {
                // 从被点击的碰撞器向上查找可拖拽的根物体
                // 防止子碰撞器被当作 m_draggedObject，导致父物体和子物体双重移动
                GameObject draggedRoot = targetHit.gameObject;

                // 向上查找可拖拽父对象时，若当前对象本身也有可拖拽 tag 且与父对象不同，
                // 说明这是一个独立的可拖拽子对象（如灯帽），不再继续向上查找
                while (draggedRoot.transform.parent != null)
                {
                    GameObject parent = draggedRoot.transform.parent.gameObject;
                    if (m_owner.m_draggableTagSet.Contains(parent.tag))
                    {
                        if (m_owner.m_draggableTagSet.Contains(draggedRoot.tag) && draggedRoot.tag != parent.tag)
                        {
                            break;
                        }
                        draggedRoot = parent;
                    }
                    else
                    {
                        break;
                    }
                }

                m_owner.m_isDragging = true;
                m_owner.m_draggedObject = draggedRoot;
                m_lastDraggedObject = draggedRoot; // 跨拖拽保留，供松手后右键旋转使用
                m_pourStarted = false;
                m_isPouring = false;
                m_dragStartMousePos = mousePos;
                m_dragStartObjPos = m_owner.m_draggedObject.transform.position;
                m_dragStartObjRot = m_owner.m_draggedObject.transform.rotation;
                m_draggedCollider = targetHit;
                m_lastValidDragPos = m_dragStartObjPos;

                // 计算被拖拽物体及其所有子物体的非 Trigger 碰撞器的总 Bounds，确保所有子物体网格都参与偏移
                Collider2D[] allColliders = GetDraggedColliders();
                m_draggedObjColliders = allColliders;
                if (allColliders.Length > 0)
                {
                    Bounds totalBounds = allColliders[0].bounds;
                    for (int i = 1; i < allColliders.Length; i++)
                    {
                        if (allColliders[i] != null)
                            totalBounds.Encapsulate(allColliders[i].bounds);
                    }
                    m_draggedObjOriginalBounds = totalBounds;
                }
                else
                {
                    m_draggedObjOriginalBounds = targetHit.bounds;
                }

                // 缓存拖拽开始时每个格子是否被被拖拽物体覆盖（用于倾倒判定 / 渲染偏移）
                CacheDraggedCoverage();

                // 构建碰撞检测用的组合 LayerMask（障碍物 + 其他可拖拽物体）
                m_combinedCollisionMask = m_owner.m_obstacleLayerMask | GetDraggableLayerMask();

                // 拖拽期间将被拖拽物体的 Rigidbody2D 设为 Kinematic，防止物理引擎干扰
                m_draggedRigidbody = m_owner.m_draggedObject.GetComponent<Rigidbody2D>();
                if (m_draggedRigidbody != null)
                {
                    m_wasKinematic = m_draggedRigidbody.isKinematic;
                    m_draggedRigidbody.isKinematic = true;
                }

                // 冻结其他可拖拽物体，防止被碰撞推开
                FreezeOtherDraggableObjects();
            }
        }
        else if (Input.GetMouseButton(0) && m_owner.m_isDragging)
        {
            Vector2 currentMousePos = cam.ScreenToWorldPoint(Input.mousePosition);
            Vector3 delta = currentMousePos - (Vector2)m_dragStartMousePos;
            // 吸附到整格
            delta.x = Mathf.Round(delta.x / m_grid.CellSize) * m_grid.CellSize;
            delta.y = Mathf.Round(delta.y / m_grid.CellSize) * m_grid.CellSize;
            delta.z = 0;

            Vector3 targetPos = new Vector3(
                m_dragStartObjPos.x + delta.x,
                m_dragStartObjPos.y + delta.y,
                m_dragStartObjPos.z);

            // 整格吸附：直接跳到整格目标位置（一整格一整格拖动，不再平滑滑动）
            Vector3 currentPos = m_owner.m_draggedObject.transform.position;
            Vector3 smoothedPos = targetPos;

            // 障碍物碰撞检测（复用缓存碰撞器，避免每帧分配）
            bool shouldCheckCollision = m_combinedCollisionMask != 0 && m_draggedObjColliders != null && m_draggedObjColliders.Length > 0;
            if (shouldCheckCollision)
            {
                // 先尝试整格目标位置
                m_owner.m_draggedObject.transform.position = smoothedPos;
                if (!IsOverlappingWithObstacles())
                {
                    m_lastValidDragPos = smoothedPos;
                }
                else
                {
                    // 计算移动方向
                    Vector3 moveDir = smoothedPos - currentPos;

                    // 收集阻挡方向：遍历所有子碰撞器（复用缓存）
                    Vector2 blockedNormal = Vector2.zero;
                    Collider2D[] draggedCols = m_draggedObjColliders;
                    foreach (var draggedCol in draggedCols)
                    {
                        if (draggedCol == null || draggedCol.isTrigger) continue;
                        m_owner.m_contactFilter.SetLayerMask(m_combinedCollisionMask);
                        int count = Physics2D.OverlapCollider(draggedCol, m_owner.m_contactFilter, m_owner.m_colliderBuffer);
                        for (int i = 0; i < count; i++)
                        {
                            if (m_owner.m_colliderBuffer[i] != null && m_owner.m_colliderBuffer[i].gameObject != m_owner.m_draggedObject)
                            {
                                if (m_owner.m_colliderBuffer[i].isTrigger)
                                    continue;
                                if (ShouldIgnoreCollision(m_owner.m_colliderBuffer[i].tag))
                                    continue;

                                bool isRelated = m_owner.m_colliderBuffer[i].transform.IsChildOf(m_owner.m_draggedObject.transform)
                                              || m_owner.m_draggedObject.transform.IsChildOf(m_owner.m_colliderBuffer[i].transform)
                                              || HasSameParent(m_owner.m_draggedObject.transform, m_owner.m_colliderBuffer[i].transform);
                                float baseThreshold = isRelated ? -m_owner.parentChildCollisionTolerance : -0.001f;
                                float tol = GetCollisionTolerance(m_owner.m_colliderBuffer[i].tag);
                                float threshold = baseThreshold - tol;

                                var dist = Physics2D.Distance(draggedCol, m_owner.m_colliderBuffer[i]);
                                if (dist.isValid && dist.distance < threshold)
                                {
                                    // 使用障碍物中心到物体中心的方向，不受穿透深度影响
                                    Vector2 obstacleCenter = m_owner.m_colliderBuffer[i].bounds.center;
                                    Vector2 objectCenter = draggedCol.bounds.center;
                                    Vector2 dirFromObstacle = (objectCenter - obstacleCenter).normalized;
                                    if (dirFromObstacle.magnitude < 0.1f)
                                    {
                                        dirFromObstacle = dist.normal;
                                    }

                                    // dot < 0 表示物体正朝向障碍物中心移动，阻挡该方向
                                    if (Vector2.Dot(moveDir, dirFromObstacle) < 0)
                                    {
                                        blockedNormal.x += Mathf.Abs(dirFromObstacle.x);
                                        blockedNormal.y += Mathf.Abs(dirFromObstacle.y);
                                    }
                                }
                            }
                        }
                    }

                    // 法线判断明确阻挡的方向直接阻止
                    Vector3 allowedPos = smoothedPos;
                    bool blockX = blockedNormal.x > 0.01f;
                    bool blockY = blockedNormal.y > 0.01f;
                    if (blockX) allowedPos.x = currentPos.x;
                    if (blockY) allowedPos.y = currentPos.y;

                    // 如果法线判断没有给出任何阻挡方向（切向或远离），直接允许移动
                    if (blockedNormal.magnitude < 0.01f)
                    {
                        m_owner.m_draggedObject.transform.position = allowedPos;
                    }
                    else
                    {
                        // 对法线未阻挡的方向做回退验证
                        if (!blockX)
                        {
                            Vector3 testX = new Vector3(smoothedPos.x, currentPos.y, currentPos.z);
                            m_owner.m_draggedObject.transform.position = testX;
                            if (IsOverlappingWithObstacles()) allowedPos.x = currentPos.x;
                        }
                        if (!blockY)
                        {
                            Vector3 testY = new Vector3(allowedPos.x, smoothedPos.y, allowedPos.z);
                            m_owner.m_draggedObject.transform.position = testY;
                            if (IsOverlappingWithObstacles()) allowedPos.y = currentPos.y;
                        }
                        m_owner.m_draggedObject.transform.position = allowedPos;
                    }
                    m_lastValidDragPos = allowedPos;
                }

                // 用实际有效偏移量更新（而非原始鼠标偏移）
                m_owner.m_dragOffsetX = m_lastValidDragPos.x - m_dragStartObjPos.x;
                m_owner.m_dragOffsetY = m_lastValidDragPos.y - m_dragStartObjPos.y;
            }
            else
            {
                m_owner.m_draggedObject.transform.position = smoothedPos;
                m_lastValidDragPos = smoothedPos;

                m_owner.m_dragOffsetX = m_lastValidDragPos.x - m_dragStartObjPos.x;
                m_owner.m_dragOffsetY = m_lastValidDragPos.y - m_dragStartObjPos.y;
            }

            // 倾倒由右键拖拽旋转触发（见 HandleRotateInput），不再使用滚轮

            m_owner.m_isDirty = true;
        }
        else if (Input.GetMouseButtonUp(0))
        {
            bool hadPour = m_pourStarted;
            if (m_owner.m_isDragging && m_owner.m_draggedObject != null)
            {
                ApplyDragOffsetToGrid();
            }
            m_owner.m_dragOffsetX = 0;
            m_owner.m_dragOffsetY = 0;
            // 未发生真实倾倒时把杯体转回直立，避免空杯一直歪着
            if (!hadPour && m_owner.m_draggedObject != null)
                m_owner.m_draggedObject.transform.rotation = m_dragStartObjRot;
            m_isPouring = false;
            m_pourStarted = false;
            // 恢复 Rigidbody2D 的原始状态
            if (m_draggedRigidbody != null)
            {
                m_draggedRigidbody.isKinematic = m_wasKinematic;
                m_draggedRigidbody = null;
            }

            // 恢复其他可拖拽物体的物理状态
            RestoreOtherDraggableObjects();
            m_combinedCollisionMask = 0;
            m_draggedObjColliders = null;
            m_owner.m_draggedCoverage = null;

            m_owner.m_isDragging = false;
            m_owner.m_draggedObject = null;
            // 拖拽结束：强制下一帧重扫障碍物，确保物体最终落点的杯壁障碍准确无误
            m_owner.m_sceneDirty = true;
            m_owner.ResetFillObject();
        }

        // 右键拖拽旋转：作用于左键拖拽的物体，量化到 90° 倍数（左键仍按住时也能旋转）
        HandleRotateInput(cam);
    }

    /// <summary>
    /// 把左键拖拽产生的整格偏移提交到网格：将被拖拽物体覆盖的格子（水/气泡/障碍）
    /// 整体平移 offsetCol/offsetRow。仅在松手时调用一次。
    /// 逆运动方向遍历，保证连续块可整体平移而不丢失。
    /// </summary>
    private void ApplyDragOffsetToGrid()
    {
        if (m_owner.m_dragOffsetX == 0 && m_owner.m_dragOffsetY == 0) return;

        int offsetCol = Mathf.RoundToInt(m_owner.m_dragOffsetX / m_grid.CellSize);
        int offsetRow = Mathf.RoundToInt(m_owner.m_dragOffsetY / m_grid.CellSize);

        if (offsetCol == 0 && offsetRow == 0) return;

        // 复制 m_grid → m_nextGrid（Array.Copy 比嵌套循环更快）
        m_grid.CopyCurrentToNext();

        // 逆运动方向遍历：靠近目标的格子先移，腾出位置给后面的格子
        // 这样连续的水/障碍块可以整体平移，不会因前面的格子挡住而丢失
        int colStart = offsetCol > 0 ? m_grid.Columns - 1 : 0;
        int colEnd   = offsetCol > 0 ? -1 : m_grid.Columns;
        int colStep  = offsetCol > 0 ? -1 : 1;

        int rowStart = offsetRow > 0 ? m_grid.Rows - 1 : 0;
        int rowEnd   = offsetRow > 0 ? -1 : m_grid.Rows;
        int rowStep  = offsetRow > 0 ? -1 : 1;

        for (int col = colStart; col != colEnd; col += colStep)
        {
            for (int row = rowStart; row != rowEnd; row += rowStep)
            {
                if (m_grid.Cells[col, row] == CellState.Empty) continue;

                // 使用拖拽开始时缓存的覆盖状态（包含碰撞器覆盖 + 容器内部封闭的水）
                int idx = col + row * m_grid.Columns;
                bool isInsideDraggedObject = m_owner.m_draggedCoverage != null && idx >= 0 && idx < m_owner.m_draggedCoverage.Length && m_owner.m_draggedCoverage[idx];
                if (!isInsideDraggedObject) continue;

                int newCol = col + offsetCol;
                int newRow = row + offsetRow;

                if (newCol < 0 || newCol >= m_grid.Columns || newRow < 0 || newRow >= m_grid.Rows)
                    continue;

                // 目标为空才移动，否则保留原位（不覆盖、不丢失）
                if (m_grid.NextCells[newCol, newRow] == CellState.Empty)
                {
                    m_grid.NextCells[col, row] = CellState.Empty;
                    m_grid.NextCells[newCol, newRow] = m_grid.Cells[col, row];
                    m_grid.NextLiquidColors[newCol, newRow] = m_grid.LiquidColors[col, row];
                }
            }
        }

        // 交换：结果写回 m_grid
        m_grid.SwapSimulationBuffers();

        m_owner.m_dragOffsetX = 0;
        m_owner.m_dragOffsetY = 0;
        m_owner.m_isDirty = true;
    }

    /// <summary>
    /// 判断当前被拖拽物体（缓存的覆盖格）内是否含有液体/气泡
    /// </summary>
    private bool DraggedObjectContainsLiquid()
    {
        if (m_owner.m_draggedCoverage == null || m_grid.Cells == null) return false;
        for (int i = 0; i < m_owner.m_draggedCoverage.Length; i++)
        {
            if (!m_owner.m_draggedCoverage[i]) continue;
            int col = i % m_grid.Columns;
            int row = i / m_grid.Columns;
            if (m_grid.Cells[col, row] == CellState.Water || m_grid.Cells[col, row] == CellState.Bubble)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 开始倾倒：把粘在源杯里的液体（连同杯壁障碍）按杯体当前姿态一次性刚性变换到新位置，
    /// 然后解除液体与杯子的“粘合”，交还给水模拟。倾斜的杯口会让元胞自动机把液体自然流出。
    /// 仅在本次拖拽中第一次触发倾倒时调用一次。
    /// </summary>
    private void StartPour()
    {
        if (m_owner.m_draggedObject == null || m_owner.m_draggedCoverage == null || m_grid.Cells == null || m_grid.NextCells == null)
            return;

        Matrix4x4 startM = Matrix4x4.TRS(m_dragStartObjPos, m_dragStartObjRot, Vector3.one);
        Matrix4x4 curM = m_owner.m_draggedObject.transform.localToWorldMatrix;
        Matrix4x4 T = curM * startM.inverse;

        m_grid.CopyCurrentToNext();

        // 1. 清空被拖拽物体覆盖的格子（杯壁障碍 + 内部液体），稍后按新姿态重新放置液体
        for (int row = 0; row < m_grid.Rows; row++)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                int idx = col + row * m_grid.Columns;
                if (!m_owner.m_draggedCoverage[idx]) continue;
                CellState s = m_grid.NextCells[col, row];
                if (s == CellState.Water || s == CellState.Bubble || s == CellState.Obstacle)
                {
                    m_grid.NextCells[col, row] = CellState.Empty;
                    m_grid.NextLiquidColors[col, row] = Color.clear;
                }
            }
        }

        // 2. 把液体格子按杯体当前姿态（平移 + 旋转）重新放回网格
        for (int row = 0; row < m_grid.Rows; row++)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                int idx = col + row * m_grid.Columns;
                if (!m_owner.m_draggedCoverage[idx]) continue;
                CellState s = m_grid.Cells[col, row];
                if (s != CellState.Water && s != CellState.Bubble) continue;

                Vector2 startWorld = m_grid.GetWorldPosition(col, row);
                Vector3 tw = T.MultiplyPoint((Vector3)startWorld);
                int nc = Mathf.FloorToInt((tw.x - m_grid.OriginX) / m_grid.CellSize);
                int nr = Mathf.FloorToInt((tw.y - m_grid.OriginY) / m_grid.CellSize);
                if (nc < 0 || nc >= m_grid.Columns || nr < 0 || nr >= m_grid.Rows) continue;

                m_grid.NextCells[nc, nr] = s;
                m_grid.NextLiquidColors[nc, nr] = m_grid.LiquidColors[col, row];
            }
        }

        // 交换网格
        m_grid.SwapSimulationBuffers();

        // 3. 解除粘合：液体成为自由模拟水，由水模拟负责从杯口流出
        m_owner.m_draggedCoverage = null;
        m_owner.m_isDirty = true;

        // 把溶液里溶着的固体随液体一起搬移到承接容器：
        // 否则倒空后源容器水为 0、却被误判为「蒸干」而在源里析出固体。
        LiquidSource pouredSrc = m_owner.m_draggedObject.GetComponentInChildren<LiquidSource>();
        if (pouredSrc != null) TransferDissolvedOnPour(pouredSrc);
    }

    /// <summary>
    /// 倾倒时把源容器溶液里「溶着的固体」随液体一起搬走：
    /// 统计被倒出的水格落到了哪个承接容器的区域里（按落点水格数最多者），
    /// 把溶解质量整体转移过去；若倒在了地上（无承接容器）则随溶液一起丢弃。
    /// 这样可避免「倒空后源容器水为 0、却被误判为蒸干、在源里析出固体」的错误。
    /// </summary>
    private void TransferDissolvedOnPour(LiquidSource src)
    {
        if (src == null || src.dissolvedMass <= 0.0001f) return;

        LiquidSource bestTarget = null;
        int bestCount = 0;

        var solids = (SolidSystem.Instance != null) ? SolidSystem.Instance.GetAllSolids() : null;
        if (solids != null)
        {
            foreach (var solid in solids)
            {
                LiquidSource ls = (solid != null) ? solid.GetPairedLiquidSource() : null;
                if (ls == null || ls == src) continue;
                Collider2D[] cols = ls.regionColliders;
                if (cols == null || cols.Length == 0) continue;

                int cnt = 0;
                for (int row = 1; row < m_grid.Rows - 1; row++)
                {
                    for (int col = 1; col < m_grid.Columns - 1; col++)
                    {
                        if (m_grid.Cells[col, row] != CellState.Water) continue;
                        m_grid.SetTempToCell(col, row);
                        foreach (var c in cols)
                        {
                            if (c != null && c.OverlapPoint(m_grid.TempPoint)) { cnt++; break; }
                        }
                    }
                }
                if (cnt > bestCount) { bestCount = cnt; bestTarget = ls; }
            }
        }

        if (bestTarget != null && bestCount > 0)
        {
            bestTarget.dissolvedReagentData = src.dissolvedReagentData;
            bestTarget.dissolvedForm = src.dissolvedForm;
            bestTarget.dissolvedMass += src.dissolvedMass;
            // 倾倒转移 → 承接容器浓度改由实际溶解质量计算，解除初始溶液锁定
            bestTarget.initialSolutionActive = false;

            // 让承接容器显示为溶液（若原本是空 / 纯水）
            if (string.IsNullOrEmpty(bestTarget.liquidType) || bestTarget.liquidType == "Water"
                || bestTarget.liquidType == LiquidSource.EMPTY_MARKER)
            {
                bestTarget.isEmptyContainer = false;
                string baseName = (src.dissolvedReagentData != null)
                    ? (!string.IsNullOrEmpty(src.dissolvedReagentData.englishName)
                        ? src.dissolvedReagentData.englishName
                        : src.dissolvedReagentData.reagentName)
                    : "Solute";
                bestTarget.liquidType = baseName + "(aq)";
                bestTarget.useReagentColor = false;
                if (src.dissolvedReagentData != null)
                    bestTarget.liquidColor = src.dissolvedReagentData.GetDisplayColor();
            }
        }

        // 源容器不再持有溶解质量（无论是否找到承接容器：倒到地上就随溶液一起丢弃）
        src.dissolvedReagentData = null;
        src.dissolvedForm = SolidForm.Powder;
        src.dissolvedMass = 0f;
        src.crystalTicks = 0;
        src.initialSolutionActive = false;
    }

    /// <summary>
    /// 快照当前被旋转烧杯的内容物（水/气泡/杯壁障碍），供旋转期间随杯体刚性旋转。
    /// 旋转中心取杯体世界中心，并换算为格子坐标（用于整数 90° 旋转）。
    /// </summary>
    private void CaptureRotSnapshot(Vector3 centerWorld)
    {
        m_rotSnapCol.Clear();
        m_rotSnapRow.Clear();
        m_rotSnapState.Clear();
        m_rotSnapColor.Clear();
        m_rotLastWritten.Clear();

        // 旋转中心量化到「半格」：这样 90° 整数旋转在网格上仍是严格双射，
        // 不会出现两个格子塌缩到同一个目标格导致水量凭空丢失（杯子越大越明显）。
        float centerColF = (centerWorld.x - m_grid.OriginX) / m_grid.CellSize - 0.5f;
        float centerRowF = (centerWorld.y - m_grid.OriginY) / m_grid.CellSize - 0.5f;
        m_rotCenterColF = Mathf.RoundToInt(centerColF * 2f) * 0.5f;
        m_rotCenterRowF = Mathf.RoundToInt(centerRowF * 2f) * 0.5f;

        if (m_owner.m_draggedCoverage == null || m_grid.Cells == null) return;
        for (int row = 0; row < m_grid.Rows; row++)
        {
            for (int col = 0; col < m_grid.Columns; col++)
            {
                int idx = col + row * m_grid.Columns;
                if (!m_owner.m_draggedCoverage[idx]) continue;
                CellState s = m_grid.Cells[col, row];
                if (s == CellState.Empty) continue; // 只快照非空（水/气泡/障碍）
                m_rotSnapCol.Add(col);
                m_rotSnapRow.Add(row);
                m_rotSnapState.Add(s);
                m_rotSnapColor.Add(m_grid.LiquidColors != null ? m_grid.LiquidColors[col, row] : Color.clear);
            }
        }
    }

    /// <summary>
    /// 把快照内容物按 90° 整数倍旋转（绕杯心）写回网格，使水随杯体同步旋转。
    /// 每帧先清除上一帧写入的格子，再从快照按当前角度重排，避免累积/残留。
    /// </summary>
    private void TransformDraggedContents(float angleDeg)
    {
        if (m_grid.Cells == null) return;

        // 0. 把快照对应的「原始格子」从网格中抬起（清空）。
        //    关键修复：CaptureRotSnapshot 只复制了状态、并未清除原格子，
        //    若不在此清除，原位置会一直残留一份静止的“幽灵”水/杯壁，
        //    旋转时水看起来不跟随，旋转结束后幽灵水脱离杯子被桌面吸收或漏走，导致水量莫名减少。
        for (int i = 0; i < m_rotSnapCol.Count; i++)
        {
            int c = m_rotSnapCol[i];
            int r = m_rotSnapRow[i];
            if (c >= 0 && c < m_grid.Columns && r >= 0 && r < m_grid.Rows)
            {
                m_grid.Cells[c, r] = CellState.Empty;
                if (m_grid.LiquidColors != null) m_grid.LiquidColors[c, r] = Color.clear;
            }
        }

        // 1. 清除上一帧写入的格子
        for (int i = 0; i < m_rotLastWritten.Count; i++)
        {
            Vector2Int p = m_rotLastWritten[i];
            if (p.x >= 0 && p.x < m_grid.Columns && p.y >= 0 && p.y < m_grid.Rows)
            {
                m_grid.Cells[p.x, p.y] = CellState.Empty;
                if (m_grid.LiquidColors != null) m_grid.LiquidColors[p.x, p.y] = Color.clear;
            }
        }
        m_rotLastWritten.Clear();

        // 2. 计算 90° 整数倍对应的旋转（与 Quaternion.Euler(0,0,angleDeg) 同向：逆时针），用整数矩阵保证格子对齐
        int q = ((Mathf.RoundToInt(angleDeg / 90f) % 4) + 4) % 4;

        // 3. 逐格重排：在世界空间绕杯心旋转后映射回网格（杯心与 transform 旋转轴一致，水与杯体视觉同步）
        for (int i = 0; i < m_rotSnapCol.Count; i++)
        {
            // 纯整数网格旋转：绕量化后的（半格）中心做 90° 整数旋转，是严格双射，
            // 任意两个格子不会塌缩到同一目标格，水量必然守恒。
            int cc = Mathf.RoundToInt(m_rotCenterColF);
            int cr = Mathf.RoundToInt(m_rotCenterRowF);
            int dc = m_rotSnapCol[i] - cc;
            int dr = m_rotSnapRow[i] - cr;
            int ndc, ndr;
            switch (q)
            {
                case 1:  ndc = -dr; ndr = dc;  break;  // 逆时针 90°
                case 2:  ndc = -dc; ndr = -dr; break;  // 180°
                case 3:  ndc = dr;  ndr = -dc; break;  // 逆时针 270°（=顺时针 90°）
                default: ndc = dc;  ndr = dr;  break;  // 0°
            }
            int nc = cc + ndc;
            int nr = cr + ndr;
            if (nc < 0 || nc >= m_grid.Columns || nr < 0 || nr >= m_grid.Rows)
            {
                // 旋转后越界：尽量保留在原格，避免凭空丢失水量（仅当原格仍为空）
                int oc = m_rotSnapCol[i];
                int or = m_rotSnapRow[i];
                if (oc >= 0 && oc < m_grid.Columns && or >= 0 && or < m_grid.Rows
                    && m_grid.Cells[oc, or] == CellState.Empty)
                {
                    m_grid.Cells[oc, or] = m_rotSnapState[i];
                    if (m_grid.LiquidColors != null) m_grid.LiquidColors[oc, or] = m_rotSnapColor[i];
                    m_rotLastWritten.Add(new Vector2Int(oc, or));
                }
                continue;
            }

            m_grid.Cells[nc, nr] = m_rotSnapState[i];
            if (m_grid.LiquidColors != null) m_grid.LiquidColors[nc, nr] = m_rotSnapColor[i];
            m_rotLastWritten.Add(new Vector2Int(nc, nr));
        }
        m_owner.m_isDirty = true;
    }

    private void ClearRotSnapshot()
    {
        m_rotSnapCol.Clear();
        m_rotSnapRow.Clear();
        m_rotSnapState.Clear();
        m_rotSnapColor.Clear();
        m_rotLastWritten.Clear();
    }

    /// <summary>
    /// 右键拖拽旋转：作用于左键拖拽的物体（m_draggedObject），把旋转量量化到 90° 的整数倍。
    /// 旋转期间水物理模拟已暂停（见协调器 Update），杯内液体随杯体刚性同步旋转（不流动、不倒）；
    /// 松开右键后物理恢复，倾斜/倒置的杯子会自然把液体倒出。
    /// 杯体保持旋转后的姿态，不自动回正（属于有意旋转）。
    /// </summary>
    private void HandleRotateInput(Camera cam)
    {
        // 右键按下：确定旋转目标
        if (Input.GetMouseButtonDown(1))
        {
            // 优先旋转「最近一次被左键拖拽的物体」（即先松手、再用右键转的那只烧杯），
            // 不要求鼠标必须压在杯子上；鼠标下没有可拖拽物体时再退回拾取。
            GameObject target = m_lastDraggedObject;
            if (target == null)
            {
                target = PickDraggableUnderMouse(cam);
            }

            if (target != null)
            {
                // 重新接入旋转目标所需的内部状态：
                // 左键松手时已清空 m_draggedObject / m_draggedObjColliders / m_draggedCoverage，
                // 这里必须把它们按当前（静止）姿态重建，否则 CacheDraggedCoverage 会因碰撞器为 null 直接返回，倾倒不可用。
                m_owner.m_draggedObject = target;
                m_draggedObjColliders = GetDraggedColliders();
                m_dragStartObjPos = target.transform.position;
                m_dragStartObjRot = target.transform.rotation;
                CacheDraggedCoverage();

                // 快照当前烧杯内容物（水+杯壁），供旋转期间随杯体刚性旋转
                CaptureRotSnapshot(target.transform.position);

                m_rotTarget = target;
                m_rotStartObjRot = target.transform.rotation;
                Vector2 center = target.transform.position;
                Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
                m_rotStartAngle = Mathf.Atan2(mouse.y - center.y, mouse.x - center.x) * Mathf.Rad2Deg;
                m_isRotating = true;
            }
            else
            {
                m_isRotating = false;
            }
        }

        // 右键持续拖拽：把累计角度量化到 90° 倍数并应用（水随杯体刚性旋转，整数双射不丢水）
        if (m_isRotating && m_rotTarget != null && Input.GetMouseButton(1))
        {
            Vector2 center = m_rotTarget.transform.position;
            Vector2 mouse = cam.ScreenToWorldPoint(Input.mousePosition);
            float curAngle = Mathf.Atan2(mouse.y - center.y, mouse.x - center.x) * Mathf.Rad2Deg;
            float delta = curAngle - m_rotStartAngle;
            // 归一化到 [-180, 180]，避免跨越 ±180° 时跳变
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            float snapped = Mathf.Round(delta / 90f) * 90f; // 量化到 90° 倍数
            m_rotTarget.transform.rotation = m_rotStartObjRot * Quaternion.Euler(0f, 0f, snapped);
            m_owner.m_isDirty = true;
            if (m_rotSnapCol.Count > 0)
                TransformDraggedContents(snapped);
        }

        // 右键松开：结束旋转（杯体保持当前 90° 姿态，不回正）
        if (Input.GetMouseButtonUp(1))
        {
            m_isRotating = false;
            ClearRotSnapshot(); // 释放快照，水已按最终姿态留在网格中，物理恢复后自然倾倒
            // 若并非左键拖拽中（说明是单独右键旋转），清理目标引用，避免遗留
            if (!m_owner.m_isDragging)
            {
                m_owner.m_draggedObject = null;
                m_owner.m_draggedCoverage = null;
                // 单独右键旋转结束：强制下一帧重扫障碍物，确保旋转后杯壁障碍落点准确
                m_owner.m_sceneDirty = true;
            }

            // 取消选中：右键松开后清空旋转目标与「最近拖拽物体」，
            // 物体回到未选中状态，左键可直接去拖拽其他物体（不会残留旧的选中项）。
            m_rotTarget = null;
            m_lastDraggedObject = null;
        }
    }

    /// <summary>
    /// 拾取鼠标位置下的可拖拽物体（向上查找可拖拽根），用于右键单独旋转时的目标确定。
    /// </summary>
    private GameObject PickDraggableUnderMouse(Camera cam)
    {
        Vector2 mousePos = cam.ScreenToWorldPoint(Input.mousePosition);
        Collider2D[] hits = Physics2D.OverlapPointAll(mousePos);
        Collider2D targetHit = null;
        foreach (var h in hits)
        {
            if (h != null && !h.isTrigger && m_owner.m_draggableTagSet.Contains(h.tag))
            {
                targetHit = h;
                break;
            }
        }
        if (targetHit == null) return null;

        GameObject root = targetHit.gameObject;
        while (root.transform.parent != null)
        {
            GameObject parent = root.transform.parent.gameObject;
            if (m_owner.m_draggableTagSet.Contains(parent.tag))
            {
                if (m_owner.m_draggableTagSet.Contains(root.tag) && root.tag != parent.tag)
                    break;
                root = parent;
            }
            else break;
        }
        return root;
    }
}
