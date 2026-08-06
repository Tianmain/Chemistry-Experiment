using UnityEngine;

/// <summary>
/// 液体渲染子系统：负责把网格状态绘制成一张填充纹理（FillArea 子物体上的 Sprite）。
/// 持有纹理/精灵/羽化查找表等全部渲染资源，与模拟逻辑解耦。
/// 读取 LiquidGrid 的元胞/颜色，读取 LayerGridPainter 的显示颜色选项与拖拽视觉偏移。
/// </summary>
public class LiquidVisualizer
{
    private readonly LayerGridPainter m_owner;
    private readonly LiquidGrid m_grid;

    // 每个单元格在纹理中占多少像素。
    // 注意：填充纹理尺寸 = 网格列数×本值 × 网格行数×本值。
    // 原先是 16，配合 400×200 的细网格会得到 6400×3200（≈80MB）的纹理，
    // 每帧（~100Hz）整张重新上传 GPU 会直接拖垮帧率，表现为水“一顿一顿”。
    // 降到 4 后纹理仅 1600×800（≈5MB），配合双线性插值 + 边缘羽化既流畅又柔和。
    // 若设备性能充裕、想让水面更细腻，可上调到 6（纹理约 2400×1200）。
    private const int PIXELS_PER_CELL = 4;

    // 可视化变量（填充显示）
    private Texture2D fillTexture;          // 填充纹理
    private Sprite fillSprite;              // 填充精灵
    private GameObject fillObj;
    private SpriteRenderer fillRenderer;
    private Color[] fillCache;
    private int cachedColumns = -1;
    private int cachedRows = -1;
    private bool m_isRebuilding = false;

    // 边缘羽化查找表：把每个水格按「四周是否临空」量化成 16 种 mask，
    // 预存每个像素的 alpha 衰减系数（仅暴露的水面边缘羽化，内部相邻水面保持不透明以连成一片液体）。
    // 配合双线性插值即可消除格子锯齿感。
    private float[] m_featherAlpha;

    public LiquidVisualizer(LayerGridPainter owner)
    {
        m_owner = owner;
        m_grid = owner.gridData;
    }

    /// <summary>
    /// 创建/复用 FillArea 可视化子物体（承载填充 Sprite）。
    /// </summary>
    public void CreateVisualizerObject(Transform parent)
    {
        if (fillObj != null && fillRenderer != null)
        {
            fillObj.transform.localPosition = Vector3.zero;
            fillObj.transform.localRotation = Quaternion.identity;
        }
        else
        {
            Transform existing = parent.Find("FillArea");
            if (existing == null)
            {
                fillObj = new GameObject("FillArea");
                fillObj.transform.SetParent(parent);
                fillObj.transform.localPosition = Vector3.zero;
                fillObj.transform.localRotation = Quaternion.identity;
                fillRenderer = fillObj.AddComponent<SpriteRenderer>();
                fillRenderer.sortingOrder = 99;
            }
            else
            {
                fillObj = existing.gameObject;
                fillRenderer = fillObj.GetComponent<SpriteRenderer>();
                if (fillRenderer == null)
                    fillRenderer = fillObj.AddComponent<SpriteRenderer>();
            }
        }
    }

    /// <summary>
    /// 重建填充纹理（尺寸变化时才重建；羽化 LUT 一并刷新）。
    /// 由协调器在初始化 / 尺寸变化时调用。
    /// </summary>
    public void Rebuild(float gridWidth, float gridHeight)
    {
        if (m_grid.CellSize <= 0 || gridWidth <= 0 || gridHeight <= 0) return;
        if (m_isRebuilding) return;

        m_isRebuilding = true;

        // 预计算边缘羽化查找表（纹理尺寸相关，重建时一并刷新）
        BuildFeatherLUT();

        int columns = m_grid.Columns;
        int rows = m_grid.Rows;
        int texWidth = columns * PIXELS_PER_CELL;
        int texHeight = rows * PIXELS_PER_CELL;

        if (fillCache == null || cachedColumns != columns || cachedRows != rows)
        {
            fillCache = new Color[texWidth * texHeight];
            cachedColumns = columns;
            cachedRows = rows;
        }

        // 填充 Sprite
        if (fillObj != null && fillRenderer != null && fillCache != null)
        {
            // 尺寸变化时才重建纹理
            if (fillTexture == null || fillTexture.width != texWidth || fillTexture.height != texHeight)
            {
                if (fillTexture != null) UnityEngine.Object.DestroyImmediate(fillTexture);
                fillTexture = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
                // 双线性插值：消除格子硬边，配合边缘羽化让水看起来柔和连续
                fillTexture.filterMode = FilterMode.Bilinear;
                fillTexture.wrapMode = TextureWrapMode.Clamp;
            }

            // Sprite 需要引用新纹理时重建
            if (fillSprite == null)
            {
                Rect fillRect = new Rect(0, 0, texWidth, texHeight);
                Vector2 fillPivot = new Vector2(0.5f, 0.5f);
                fillSprite = Sprite.Create(fillTexture, fillRect, fillPivot, 1f);
                fillRenderer.sprite = fillSprite;
            }

            float scaleX = gridWidth / texWidth;
            float scaleY = gridHeight / texHeight;
            fillObj.transform.localScale = new Vector3(scaleX, scaleY, 1f);
            fillObj.transform.localPosition = Vector3.zero;
            fillObj.transform.localRotation = Quaternion.identity;
        }

        m_isRebuilding = false;

        RefreshColors();
    }

    /// <summary>
    /// 销毁填充 Sprite（重新初始化前调用）。
    /// </summary>
    public void DestroySprite()
    {
        if (fillSprite != null)
        {
            UnityEngine.Object.DestroyImmediate(fillSprite);
            fillSprite = null;
        }
    }

    /// <summary>
    /// 重置缓存尺寸标记（强制下次重建纹理尺寸）。
    /// </summary>
    public void ResetCache()
    {
        cachedColumns = -1;
        cachedRows = -1;
    }

    /// <summary>
    /// 资源释放（协调器 OnDestroy 时调用）。
    /// </summary>
    public void Release()
    {
        if (fillTexture != null) UnityEngine.Object.DestroyImmediate(fillTexture);
        if (fillSprite != null) UnityEngine.Object.DestroyImmediate(fillSprite);
    }

    /// <summary>
    /// 拖拽结束时把 FillArea 子物体复位到本地原点（避免残留偏移）。
    /// </summary>
    public void ResetFillTransform()
    {
        if (fillObj != null) fillObj.transform.localPosition = Vector3.zero;
    }

    /// <summary>
    /// 预计算边缘羽化查找表（16 种 mask × 每格像素）。mask 位：
    /// bit0=左临空 bit1=右临空 bit2=下临空 bit3=上临空。
    /// 暴露的水面边缘 alpha 衰减到 FEATHER_MIN（保留一定不透明度，避免孤立水滴变透明消失），
    /// 内部相连处 alpha=1，使液体连成一片。
    /// </summary>
    private void BuildFeatherLUT()
    {
        int n = PIXELS_PER_CELL;
        int cells = n * n;
        if (m_featherAlpha == null || m_featherAlpha.Length != 16 * cells)
            m_featherAlpha = new float[16 * cells];

        int feather = Mathf.Max(1, n / 3);   // 羽化过渡宽度（像素）
        const float FEATHER_MIN = 0.3f;       // 边缘最低不透明度，保证水滴可见

        for (int mask = 0; mask < 16; mask++)
        {
            bool left = (mask & 1) != 0;
            bool right = (mask & 2) != 0;
            bool bottom = (mask & 4) != 0;
            bool top = (mask & 8) != 0;

            for (int py = 0; py < n; py++)
            {
                for (int px = 0; px < n; px++)
                {
                    float a = 1f;
                    if (left) a *= EdgeFalloff(px, feather, FEATHER_MIN);
                    if (right) a *= EdgeFalloff(n - 1 - px, feather, FEATHER_MIN);
                    if (bottom) a *= EdgeFalloff(py, feather, FEATHER_MIN);
                    if (top) a *= EdgeFalloff(n - 1 - py, feather, FEATHER_MIN);
                    m_featherAlpha[mask * cells + py * n + px] = a;
                }
            }
        }
    }

    /// <summary>
    /// 距格子边缘 distFromEdge 个像素处的 alpha 系数（smoothstep 曲线）。
    /// </summary>
    private static float EdgeFalloff(int distFromEdge, int feather, float minAlpha)
    {
        float t = feather <= 0 ? 1f : (float)distFromEdge / feather;
        t = Mathf.Clamp01(t);
        float s = t * t * (3f - 2f * t);   // smoothstep
        return minAlpha + (1f - minAlpha) * s;
    }

    /// <summary>
    /// 按边缘羽化绘制一个水格（或气泡）。气泡会在羽化水色背景上叠加白色圆点。
    /// </summary>
    private void DrawFeatheredCell(int startX, int startY, int texWidth, Color baseColor, bool isBubble, int mask = 0)
    {
        if (m_featherAlpha == null) BuildFeatherLUT();

        int n = PIXELS_PER_CELL;
        int cells = n * n;
        int baseIdx = mask * cells;

        for (int py = 0; py < n; py++)
        {
            int rowOff = (startY + py) * texWidth + startX;
            int lutOff = baseIdx + py * n;
            for (int px = 0; px < n; px++)
            {
                float fa = m_featherAlpha[lutOff + px];
                Color pc = baseColor;
                pc.a *= fa;
                fillCache[rowOff + px] = pc;
            }
        }

        if (isBubble)
        {
            float cx = n * 0.5f;
            float cy = n * 0.5f;
            float radius = n * 0.35f;
            for (int py = 0; py < n; py++)
            {
                for (int px = 0; px < n; px++)
                {
                    float dx = px + 0.5f - cx;
                    float dy = py + 0.5f - cy;
                    if (dx * dx + dy * dy <= radius * radius)
                        fillCache[(startY + py) * texWidth + (startX + px)] = m_owner.bubbleColor;
                }
            }
        }
    }

    /// <summary>
    /// 刷新填充纹理（仅脏标记置位时由协调器调用）。
    /// </summary>
    public void RefreshColors()
    {
        if ((fillTexture == null || fillCache == null) && !m_isRebuilding)
        {
            m_owner.RebuildGrid();
            return;
        }

        if (fillTexture == null || fillCache == null) return;

        int columns = cachedColumns;
        int rows = cachedRows;
        int texWidth = columns * PIXELS_PER_CELL;
        int texHeight = rows * PIXELS_PER_CELL;

        // 清空填充缓存（Array.Clear 比手动循环更快）
        System.Array.Clear(fillCache, 0, fillCache.Length);

        // 绘制水和障碍物到填充缓存
        if (Application.isPlaying && m_grid.Cells != null)
        {
            RefreshColorsFromSimulation(columns, rows, texWidth, texHeight);
        }
        else
        {
            RefreshColorsFromLayer(columns, rows, texWidth, texHeight);
        }

        // 应用填充纹理
        if (fillTexture != null)
        {
            fillTexture.SetPixels(fillCache);
            fillTexture.Apply(false);
        }
    }

    private void RefreshColorsFromSimulation(int columns, int rows, int texWidth, int texHeight)
    {
        // 计算拖拽偏移对应的格子偏移（左键拖动时只做视觉偏移，松手才提交网格）
        int offsetCol = Mathf.RoundToInt(m_owner.m_dragOffsetX / m_grid.CellSize);
        int offsetRow = Mathf.RoundToInt(m_owner.m_dragOffsetY / m_grid.CellSize);

        // 只偏移被拖拽物体自身范围内的格子
        bool hasOriginalBounds = m_owner.m_isDragging && m_owner.m_draggedObject != null;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                Color c = Color.clear;
                bool isBubble = false;
                if (m_grid.Cells[col, row] == CellState.Water)
                {
                    // 优先使用 LiquidSource 定义的自定义颜色，未定义则回退到默认 waterColor
                    c = m_grid.LiquidColors[col, row];
                    if (c == Color.clear || c.a <= 0.001f)
                        c = m_owner.waterColor;
                }
                else if (m_grid.Cells[col, row] == CellState.Bubble)
                {
                    // 气泡：先绘制背景水色，再叠加白色气泡圆点
                    c = m_owner.waterColor;
                    isBubble = true;
                }
                else if (m_grid.Cells[col, row] == CellState.Obstacle)
                    c = m_owner.obstacleColor;
                else
                    continue;

                // 判断该网格是否被被拖拽物体的实际碰撞器覆盖（使用拖拽开始时缓存的原始覆盖状态）
                bool shouldOffset = false;
                if (hasOriginalBounds && m_owner.m_draggedCoverage != null)
                {
                    int index = col + row * m_grid.Columns;
                    if (index >= 0 && index < m_owner.m_draggedCoverage.Length)
                        shouldOffset = m_owner.m_draggedCoverage[index];
                }

                // 计算绘制位置
                int drawCol = shouldOffset ? col + offsetCol : col;
                int drawRow = shouldOffset ? row + offsetRow : row;

                // 确保在网格范围内
                if (drawCol < 0 || drawCol >= columns || drawRow < 0 || drawRow >= rows)
                    continue;

                int startX = drawCol * PIXELS_PER_CELL;
                int startY = drawRow * PIXELS_PER_CELL;

                if (isBubble)
                {
                    // 气泡：柔化水色背景 + 中心白色圆点
                    DrawFeatheredCell(startX, startY, texWidth, c, true);
                }
                else if (m_grid.Cells[col, row] == CellState.Obstacle)
                {
                    // 杯壁等障碍物保持硬边（实体）
                    for (int py = 0; py < PIXELS_PER_CELL; py++)
                    {
                        int offset = (startY + py) * texWidth + startX;
                        System.Array.Fill(fillCache, c, offset, PIXELS_PER_CELL);
                    }
                }
                else
                {
                    // 水：按四周是否临空做边缘羽化——暴露的水面柔化，内部相连处保持不透明
                    if (m_featherAlpha == null) BuildFeatherLUT();
                    int mask = 0;
                    if (col <= 0 || m_grid.Cells[col - 1, row] != CellState.Water) mask |= 1;
                    if (col >= columns - 1 || m_grid.Cells[col + 1, row] != CellState.Water) mask |= 2;
                    if (row <= 0 || m_grid.Cells[col, row - 1] != CellState.Water) mask |= 4;
                    if (row >= rows - 1 || m_grid.Cells[col, row + 1] != CellState.Water) mask |= 8;
                    DrawFeatheredCell(startX, startY, texWidth, c, false, mask);
                }
            }
        }
    }

    private void RefreshColorsFromLayer(int columns, int rows, int texWidth, int texHeight)
    {
        float originX = m_owner.transform.position.x - m_owner.gridWidth * 0.5f;
        float originY = m_owner.transform.position.y - m_owner.gridHeight * 0.5f;

        // 一次查询获取所有碰撞器
        Vector2 center = new Vector2(originX + m_owner.gridWidth * 0.5f, originY + m_owner.gridHeight * 0.5f);
        Vector2 boxSize = new Vector2(m_owner.gridWidth + m_grid.CellSize, m_owner.gridHeight + m_grid.CellSize);
        int count = Physics2D.OverlapBoxNonAlloc(center, boxSize, 0, m_owner.m_colliderBuffer);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                float worldX = originX + (col + 0.5f) * m_grid.CellSize;
                float worldY = originY + (row + 0.5f) * m_grid.CellSize;
                m_grid.TempPoint.Set(worldX, worldY);

                Color c = Color.clear;
                for (int i = 0; i < count; i++)
                {
                    var hit = m_owner.m_colliderBuffer[i];
                    if (!hit.OverlapPoint(m_grid.TempPoint)) continue;

                    if (hit.gameObject.layer == m_owner.m_waterLayer && m_owner.m_waterLayer >= 0)
                    {
                        c = m_owner.waterColor;
                        break;
                    }
                    if (m_owner.IsObstacleLayer(hit.gameObject.layer) && !hit.isTrigger)
                    {
                        c = m_owner.obstacleColor;
                        break;
                    }
                }
                if (c == Color.clear) continue;

                int startX = col * PIXELS_PER_CELL;
                int startY = row * PIXELS_PER_CELL;
                // Array.Fill 比嵌套循环更高效
                for (int py = 0; py < PIXELS_PER_CELL; py++)
                {
                    int offset = (startY + py) * texWidth + startX;
                    System.Array.Fill(fillCache, c, offset, PIXELS_PER_CELL);
                }
            }
        }
    }
}
