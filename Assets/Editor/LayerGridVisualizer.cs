using UnityEngine;
using UnityEditor;

/// <summary>
/// 在 Scene 视图中绘制网格，并根据 Layer 用不同颜色填充
/// 用于可视化检测网格单元格所在的 Layer
/// </summary>
[ExecuteInEditMode]
public class LayerGridVisualizer : MonoBehaviour
{
    /// <summary>
    /// 网格单元格大小
    /// </summary>
    [Header("网格设置")]
    [Tooltip("网格单元格的边长")]
    public float cellSize = 1f;

    /// <summary>
    /// 网格行数
    /// </summary>
    [Tooltip("网格行数")]
    public int gridRows = 10;

    /// <summary>
    /// 网格列数
    /// </summary>
    [Tooltip("网格列数")]
    public int gridColumns = 10;

    /// <summary>
    /// 是否显示网格线
    /// </summary>
    [Tooltip("是否显示网格线")]
    public bool showGridLines = true;

    /// <summary>
    /// 网格线颜色
    /// </summary>
    [Tooltip("网格线颜色")]
    public Color gridLineColor = Color.white;

    /// <summary>
    /// Layer 对应的颜色映射
    /// </summary>
    [Header("Layer 颜色设置")]
    [Tooltip("为每个 Layer 指定显示颜色")]
    public LayerColorMapping[] layerColors;

    /// <summary>
    /// 默认颜色（当 Layer 没有映射时使用）
    /// </summary>
    [Tooltip("默认颜色（当 Layer 没有映射时使用）")]
    public Color defaultColor = new Color(1f, 1f, 1f, 0.3f);

    /// <summary>
    /// 是否检测鼠标位置的 Layer
    /// </summary>
    [Header("交互设置")]
    [Tooltip("是否在 Scene 视图中检测鼠标位置的 Layer")]
    public bool detectMouseLayer = true;

    /// <summary>
    /// 当前鼠标位置的 Layer 名称
    /// </summary>
    [HideInInspector]
    public string currentMouseLayerName = "";

    /// <summary>
    /// 当前鼠标位置的单元格坐标
    /// </summary>
    [HideInInspector]
    public Vector2Int currentMouseCell = Vector2Int.zero;

    private void OnDrawGizmos()
    {
        DrawGrid();
    }

    /// <summary>
    /// 绘制网格
    /// </summary>
    private void DrawGrid()
    {
        if (cellSize <= 0 || gridRows <= 0 || gridColumns <= 0)
            return;

        // 计算网格起始位置（居中）
        Vector3 startPos = transform.position;
        startPos.x -= (gridColumns * cellSize) * 0.5f;
        startPos.z -= (gridRows * cellSize) * 0.5f;

        // 绘制每个单元格
        for (int row = 0; row < gridRows; row++)
        {
            for (int col = 0; col < gridColumns; col++)
            {
                DrawCell(row, col, startPos);
            }
        }

        // 绘制网格线
        if (showGridLines)
        {
            DrawGridLines(startPos);
        }
    }

    /// <summary>
    /// 绘制单个单元格
    /// </summary>
    /// <param name="row">行索引</param>
    /// <param name="col">列索引</param>
    /// <param name="startPos">网格起始位置</param>
    private void DrawCell(int row, int col, Vector3 startPos)
    {
        Vector3 cellCenter = new Vector3(
            startPos.x + col * cellSize + cellSize * 0.5f,
            startPos.y,
            startPos.z + row * cellSize + cellSize * 0.5f
        );

        // 检测该位置的对象 Layer
        Color cellColor = GetLayerColorAtPosition(cellCenter);

        // 绘制单元格
        Gizmos.color = cellColor;
        Vector3 cellSize3D = new Vector3(cellSize, 0.01f, cellSize);
        Gizmos.DrawCube(cellCenter, cellSize3D);
    }

    /// <summary>
    /// 绘制网格线
    /// </summary>
    /// <param name="startPos">网格起始位置</param>
    private void DrawGridLines(Vector3 startPos)
    {
        Gizmos.color = gridLineColor;

        float totalWidth = gridColumns * cellSize;
        float totalHeight = gridRows * cellSize;

        // 绘制垂直线
        for (int col = 0; col <= gridColumns; col++)
        {
            Vector3 start = new Vector3(startPos.x + col * cellSize, startPos.y, startPos.z);
            Vector3 end = new Vector3(startPos.x + col * cellSize, startPos.y, startPos.z + totalHeight);
            Gizmos.DrawLine(start, end);
        }

        // 绘制水平线
        for (int row = 0; row <= gridRows; row++)
        {
            Vector3 start = new Vector3(startPos.x, startPos.y, startPos.z + row * cellSize);
            Vector3 end = new Vector3(startPos.x + totalWidth, startPos.y, startPos.z + row * cellSize);
            Gizmos.DrawLine(start, end);
        }
    }

    /// <summary>
    /// 获取指定位置的 Layer 对应颜色
    /// </summary>
    /// <param name="position">世界坐标位置</param>
    /// <returns>对应的颜色</returns>
    private Color GetLayerColorAtPosition(Vector3 position)
    {
        // 检测该位置的碰撞体
        Collider2D collider = Physics2D.OverlapPoint(new Vector2(position.x, position.z));

        if (collider != null)
        {
            int layer = collider.gameObject.layer;
            return GetColorForLayer(layer);
        }

        return defaultColor;
    }

    /// <summary>
    /// 根据 Layer 获取对应颜色
    /// </summary>
    /// <param name="layer">Layer 索引</param>
    /// <returns>对应的颜色</returns>
    private Color GetColorForLayer(int layer)
    {
        if (layerColors != null)
        {
            foreach (var mapping in layerColors)
            {
                if (mapping.layerIndex == layer)
                {
                    return mapping.color;
                }
            }
        }

        return defaultColor;
    }

    /// <summary>
    /// 获取鼠标在网格中的位置
    /// </summary>
    /// <returns>网格单元格坐标，如果不在网格内则返回 null</returns>
    public Vector2Int? GetMouseGridPosition()
    {
        if (!detectMouseLayer)
            return null;

        Vector3 mousePos = Event.current.mousePosition;
        mousePos.y = SceneView.currentDrawingSceneView.camera.pixelHeight - mousePos.y;
        Ray ray = SceneView.currentDrawingSceneView.camera.ScreenPointToRay(mousePos);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit))
        {
            Vector3 localPos = hit.point - transform.position;
            int col = Mathf.FloorToInt(localPos.x / cellSize + gridColumns * 0.5f);
            int row = Mathf.FloorToInt(localPos.z / cellSize + gridRows * 0.5f);

            if (col >= 0 && col < gridColumns && row >= 0 && row < gridRows)
            {
                return new Vector2Int(col, row);
            }
        }

        return null;
    }
}

/// <summary>
/// Layer 与颜色的映射关系
/// </summary>
[System.Serializable]
public struct LayerColorMapping
{
    /// <summary>
    /// Layer 索引
    /// </summary>
    [Tooltip("Layer 索引")]
    public int layerIndex;

    /// <summary>
    /// Layer 名称（仅用于显示）
    /// </summary>
    [Tooltip("Layer 名称")]
    public string layerName;

    /// <summary>
    /// 对应的颜色
    /// </summary>
    [Tooltip("对应的颜色")]
    public Color color;

    public LayerColorMapping(int layerIndex, string layerName, Color color)
    {
        this.layerIndex = layerIndex;
        this.layerName = layerName;
        this.color = color;
    }
}

/// <summary>
/// LayerGridVisualizer 的自定义编辑器
/// </summary>
[CustomEditor(typeof(LayerGridVisualizer))]
public class LayerGridVisualizerEditor : Editor
{
    private LayerGridVisualizer visualizer;

    private void OnEnable()
    {
        visualizer = (LayerGridVisualizer)target;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Layer 颜色快速设置", EditorStyles.boldLabel);

        if (GUILayout.Button("自动添加所有 Layer"))
        {
            AutoSetupLayerColors();
        }

        // 显示当前鼠标位置信息
        if (visualizer.detectMouseLayer)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("鼠标位置信息", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Layer: " + visualizer.currentMouseLayerName);
            EditorGUILayout.LabelField("单元格: " + visualizer.currentMouseCell.ToString());
        }
    }

    /// <summary>
    /// 自动设置所有 Layer 的颜色
    /// </summary>
    private void AutoSetupLayerColors()
    {
        int layerCount = 32; // Unity 支持 32 个 Layer
        visualizer.layerColors = new LayerColorMapping[layerCount];

        for (int i = 0; i < layerCount; i++)
        {
            string layerName = LayerMask.LayerToName(i);
            Color randomColor = new Color(
                Random.value,
                Random.value,
                Random.value,
                0.5f
            );

            visualizer.layerColors[i] = new LayerColorMapping(i, layerName, randomColor);
        }

        EditorUtility.SetDirty(visualizer);
    }

    private void OnSceneGUI()
    {
        if (visualizer == null || !visualizer.detectMouseLayer)
            return;

        // 检测鼠标位置
        Event e = Event.current;
        if (e.type == EventType.MouseMove || e.type == EventType.Layout)
        {
            Vector2Int? mouseGridPos = visualizer.GetMouseGridPosition();
            if (mouseGridPos.HasValue)
            {
                visualizer.currentMouseCell = mouseGridPos.Value;
                // 这里可以添加获取鼠标位置 Layer 的逻辑
            }

            SceneView.RepaintAll();
        }
    }
}
