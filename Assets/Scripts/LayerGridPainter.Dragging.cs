using UnityEngine;
using System.Collections.Generic;

public partial class LayerGridPainter
{
    /// <summary>
    /// 获取被拖拽物体自身及其所有子物体的碰撞器
    /// </summary>
    private Collider2D[] GetDraggedColliders()
    {
        if (m_draggedObject == null) return System.Array.Empty<Collider2D>();
        return m_draggedObject.GetComponentsInChildren<Collider2D>();
    }

    /// <summary>
    /// 检测当前拖拽物体的任意碰撞器是否与障碍物发生穿透（distance < -0.001f）
    /// 遍历自身及所有子碰撞器，确保不同结构（如子碰撞器/自身碰撞器）行为一致
    /// </summary>
    private bool IsOverlappingWithObstacles()
    {
        Collider2D[] colliders = GetDraggedColliders();
        if (colliders.Length == 0) return false;

        foreach (var col in colliders)
        {
            if (col == null) continue;
            ContactFilter2D filter = new ContactFilter2D();
            filter.SetLayerMask(m_combinedCollisionMask);
            int count = Physics2D.OverlapCollider(col, filter, m_colliderBuffer);
            for (int i = 0; i < count; i++)
            {
                if (m_colliderBuffer[i] != null && m_colliderBuffer[i].gameObject != m_draggedObject)
                {
                    if (m_colliderBuffer[i].transform.IsChildOf(m_draggedObject.transform)) continue;

                    var dist = Physics2D.Distance(col, m_colliderBuffer[i]);
                    if (dist.isValid && dist.distance < -0.001f)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private Rigidbody2D m_draggedRigidbody;
    private bool m_wasKinematic = false;

    private int m_combinedCollisionMask;

    private struct RigidbodyState
    {
        public Rigidbody2D rb;
        public bool wasKinematic;
    }
    private List<RigidbodyState> m_otherRigidbodyStates = new List<RigidbodyState>();

    /// <summary>
    /// 获取所有可拖拽物体所在的 Layer 掩码
    /// </summary>
    private int GetDraggableLayerMask()
    {
        int mask = 0;
        foreach (string tag in draggableTags)
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
        foreach (string tag in draggableTags)
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
                if (obj == null || obj == m_draggedObject) continue;
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

    private void HandleDrag()
    {
        Camera cam = GetCachedCamera();
        if (cam == null) return;

        if (Input.GetMouseButtonDown(0))
        {
            Vector2 mousePos = cam.ScreenToWorldPoint(Input.mousePosition);
            // 遍历鼠标位置下的所有碰撞器，找到可拖拽的物体
            Collider2D[] hits = Physics2D.OverlapPointAll(mousePos);
            Collider2D targetHit = null;
            foreach (var h in hits)
            {
                if (h != null && m_draggableTagSet.Contains(h.tag))
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
                while (draggedRoot.transform.parent != null)
                {
                    GameObject parent = draggedRoot.transform.parent.gameObject;
                    if (m_draggableTagSet.Contains(parent.tag))
                    {
                        draggedRoot = parent;
                    }
                    else
                    {
                        break;
                    }
                }

                m_isDragging = true;
                m_draggedObject = draggedRoot;
                m_dragStartMousePos = mousePos;
                m_dragStartObjPos = m_draggedObject.transform.position;
                m_dragStartObjRot = m_draggedObject.transform.rotation;
                m_draggedCollider = targetHit;
                m_lastValidDragPos = m_dragStartObjPos;

                // 计算被拖拽物体及其所有子碰撞器的总 Bounds，确保所有子物体网格都参与偏移
                Collider2D[] allColliders = m_draggedObject.GetComponentsInChildren<Collider2D>();
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

                // 构建碰撞检测用的组合 LayerMask（障碍物 + 其他可拖拽物体）
                m_combinedCollisionMask = m_obstacleLayerMask | GetDraggableLayerMask();

                // 拖拽期间将被拖拽物体的 Rigidbody2D 设为 Kinematic，防止物理引擎干扰
                m_draggedRigidbody = m_draggedObject.GetComponent<Rigidbody2D>();
                if (m_draggedRigidbody != null)
                {
                    m_wasKinematic = m_draggedRigidbody.isKinematic;
                    m_draggedRigidbody.isKinematic = true;
                }

                // 冻结其他可拖拽物体，防止被碰撞推开
                FreezeOtherDraggableObjects();
            }
        }
        else if (Input.GetMouseButton(0) && m_isDragging)
        {
            Vector2 currentMousePos = cam.ScreenToWorldPoint(Input.mousePosition);
            Vector3 delta = currentMousePos - (Vector2)m_dragStartMousePos;
            // 吸附到整格
            delta.x = Mathf.Round(delta.x / cellSize) * cellSize;
            delta.y = Mathf.Round(delta.y / cellSize) * cellSize;
            delta.z = 0;

            Vector3 targetPos = new Vector3(
                m_dragStartObjPos.x + delta.x,
                m_dragStartObjPos.y + delta.y,
                m_dragStartObjPos.z);

            // 速度限制：平滑移动到目标位置
            Vector3 currentPos = m_draggedObject.transform.position;
            float maxDelta = dragSpeed * Time.deltaTime;
            Vector3 smoothedPos = Vector3.MoveTowards(currentPos, targetPos, maxDelta);

            // 障碍物碰撞检测
            bool shouldCheckCollision = m_combinedCollisionMask != 0 && GetDraggedColliders().Length > 0;
            if (shouldCheckCollision)
            {
                // 先尝试平滑目标位置
                m_draggedObject.transform.position = smoothedPos;
                if (!IsOverlappingWithObstacles())
                {
                    m_lastValidDragPos = smoothedPos;
                }
                else
                {
                    // 计算移动方向
                    Vector3 moveDir = smoothedPos - currentPos;

                    // 收集阻挡方向：遍历所有子碰撞器
                    Vector2 blockedNormal = Vector2.zero;
                    Collider2D[] draggedCols = GetDraggedColliders();
                    foreach (var draggedCol in draggedCols)
                    {
                        if (draggedCol == null) continue;
                        ContactFilter2D filter2 = new ContactFilter2D();
                        filter2.SetLayerMask(m_combinedCollisionMask);
                        int count = Physics2D.OverlapCollider(draggedCol, filter2, m_colliderBuffer);
                        for (int i = 0; i < count; i++)
                        {
                            if (m_colliderBuffer[i] != null && m_colliderBuffer[i].gameObject != m_draggedObject)
                            {
                                if (m_colliderBuffer[i].transform.IsChildOf(m_draggedObject.transform)) continue;

                                var dist = Physics2D.Distance(draggedCol, m_colliderBuffer[i]);
                                if (dist.isValid && dist.distance < -0.001f)
                                {
                                    // 使用障碍物中心到物体中心的方向，不受穿透深度影响
                                    Vector2 obstacleCenter = m_colliderBuffer[i].bounds.center;
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
                        m_draggedObject.transform.position = allowedPos;
                    }
                    else
                    {
                        // 对法线未阻挡的方向做回退验证
                        if (!blockX)
                        {
                            Vector3 testX = new Vector3(smoothedPos.x, currentPos.y, currentPos.z);
                            m_draggedObject.transform.position = testX;
                            if (IsOverlappingWithObstacles()) allowedPos.x = currentPos.x;
                        }
                        if (!blockY)
                        {
                            Vector3 testY = new Vector3(allowedPos.x, smoothedPos.y, allowedPos.z);
                            m_draggedObject.transform.position = testY;
                            if (IsOverlappingWithObstacles()) allowedPos.y = currentPos.y;
                        }
                        m_draggedObject.transform.position = allowedPos;
                    }
                    m_lastValidDragPos = allowedPos;
                }

                m_draggedObject.transform.rotation = m_dragStartObjRot;

                // 用实际有效偏移量更新（而非原始鼠标偏移）
                m_dragOffsetX = m_lastValidDragPos.x - m_dragStartObjPos.x;
                m_dragOffsetY = m_lastValidDragPos.y - m_dragStartObjPos.y;
            }
            else
            {
                m_draggedObject.transform.position = smoothedPos;
                m_lastValidDragPos = smoothedPos;
                m_draggedObject.transform.rotation = m_dragStartObjRot;

                m_dragOffsetX = m_lastValidDragPos.x - m_dragStartObjPos.x;
                m_dragOffsetY = m_lastValidDragPos.y - m_dragStartObjPos.y;
            }

            m_isDirty = true;
        }
        else if (Input.GetMouseButtonUp(0))
        {
            if (m_isDragging && m_draggedObject != null)
            {
                ApplyDragOffsetToGrid();
            }
            // 恢复 Rigidbody2D 的原始状态
            if (m_draggedRigidbody != null)
            {
                m_draggedRigidbody.isKinematic = m_wasKinematic;
                m_draggedRigidbody = null;
            }

            // 恢复其他可拖拽物体的物理状态
            RestoreOtherDraggableObjects();
            m_combinedCollisionMask = 0;

            m_isDragging = false;
            m_draggedObject = null;
            if (fillObj != null)
                fillObj.transform.localPosition = Vector3.zero;
        }
    }

    private void ApplyDragOffsetToGrid()
    {
        if (m_dragOffsetX == 0 && m_dragOffsetY == 0) return;

        int offsetCol = Mathf.RoundToInt(m_dragOffsetX / cellSize);
        int offsetRow = Mathf.RoundToInt(m_dragOffsetY / cellSize);

        if (offsetCol == 0 && offsetRow == 0) return;

        Bounds originalBounds = m_draggedObjOriginalBounds;

        // 复制 m_grid → m_nextGrid
        for (int col = 0; col < m_columns; col++)
            for (int row = 0; row < m_rows; row++)
                m_nextGrid[col, row] = m_grid[col, row];

        // 复制颜色网格
        if (m_liquidColorGrid != null && m_nextLiquidColorGrid != null)
        {
            System.Array.Copy(m_liquidColorGrid, m_nextLiquidColorGrid, m_liquidColorGrid.Length);
        }

        // 逆运动方向遍历：靠近目标的格子先移，腾出位置给后面的格子
        // 这样连续的水/障碍块可以整体平移，不会因前面的格子挡住而丢失
        int colStart = offsetCol > 0 ? m_columns - 1 : 0;
        int colEnd   = offsetCol > 0 ? -1 : m_columns;
        int colStep  = offsetCol > 0 ? -1 : 1;

        int rowStart = offsetRow > 0 ? m_rows - 1 : 0;
        int rowEnd   = offsetRow > 0 ? -1 : m_rows;
        int rowStep  = offsetRow > 0 ? -1 : 1;

        for (int col = colStart; col != colEnd; col += colStep)
        {
            for (int row = rowStart; row != rowEnd; row += rowStep)
            {
                if (m_grid[col, row] == CellState.Empty) continue;

                Vector2 cellCenter = GetWorldPosition(col, row);
                if (!originalBounds.Contains(cellCenter)) continue;

                int newCol = col + offsetCol;
                int newRow = row + offsetRow;

                if (newCol < 0 || newCol >= m_columns || newRow < 0 || newRow >= m_rows)
                    continue;

                // 目标为空才移动，否则保留原位（不覆盖、不丢失）
                if (m_nextGrid[newCol, newRow] == CellState.Empty)
                {
                    m_nextGrid[col, row] = CellState.Empty;
                    m_nextGrid[newCol, newRow] = m_grid[col, row];
                    m_nextLiquidColorGrid[newCol, newRow] = m_liquidColorGrid[col, row];
                }
            }
        }

        // 交换：结果写回 m_grid
        var temp = m_grid;
        m_grid = m_nextGrid;
        m_nextGrid = temp;

        var tempColor = m_liquidColorGrid;
        m_liquidColorGrid = m_nextLiquidColorGrid;
        m_nextLiquidColorGrid = tempColor;

        m_dragOffsetX = 0;
        m_dragOffsetY = 0;
        m_isDirty = true;
    }
}
