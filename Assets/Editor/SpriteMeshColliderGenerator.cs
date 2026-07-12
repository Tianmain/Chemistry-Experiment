using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace ChemistryExperiment.EditorTools
{
    public static class SpriteMeshColliderUtility
    {
        public enum OutlineMode
        {
            PhysicsShape,
            MeshOutline,
            ConvexHull
        }

        #region 核心入口

        /// <summary>
        /// 为指定物体生成基于 Sprite 网格的 PolygonCollider2D
        /// </summary>
        public static bool GenerateCollider(GameObject go, OutlineMode mode,
            bool removeExisting = true, bool simplify = true, float tolerance = 0.005f)
        {
            SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
            MeshFilter mf = go.GetComponent<MeshFilter>();

            Vector2[] path = null;

            if (sr != null && sr.sprite != null)
            {
                path = ExtractPathFromSprite(sr.sprite, mode);
            }
            else if (mf != null && mf.sharedMesh != null)
            {
                path = ExtractPathFromMesh(mf.sharedMesh, mode);
            }
            else
            {
                Debug.LogWarning($"[{go.name}] 未找到 SpriteRenderer 或 MeshFilter，跳过。");
                return false;
            }

            if (path == null || path.Length < 3)
            {
                Debug.LogWarning($"[{go.name}] 无法提取有效轮廓（顶点数不足），跳过。");
                return false;
            }

            if (simplify && mode != OutlineMode.PhysicsShape)
            {
                path = SimplifyPath(path, tolerance);
                if (path.Length < 3)
                {
                    Debug.LogWarning($"[{go.name}] 简化后顶点数不足，跳过。");
                    return false;
                }
            }

            ApplyPolygonCollider(go, path, removeExisting);
            Debug.Log($"[{go.name}] 成功生成 PolygonCollider2D，顶点数: {path.Length}");
            return true;
        }

        #endregion

        #region 轮廓提取

        private static Vector2[] ExtractPathFromSprite(Sprite sprite, OutlineMode mode)
        {
            switch (mode)
            {
                case OutlineMode.PhysicsShape:
                    return GetPhysicsShape(sprite);
                case OutlineMode.MeshOutline:
                    return ExtractMeshOutline(sprite.vertices, sprite.triangles);
                case OutlineMode.ConvexHull:
                    return ComputeConvexHull(sprite.vertices);
                default:
                    return null;
            }
        }

        private static Vector2[] ExtractPathFromMesh(Mesh mesh, OutlineMode mode)
        {
            Vector2[] vertices = mesh.vertices.Select(v => new Vector2(v.x, v.y)).ToArray();
            int[] triangles = mesh.triangles;

            switch (mode)
            {
                case OutlineMode.MeshOutline:
                    return ExtractMeshOutline(vertices, triangles);
                case OutlineMode.ConvexHull:
                    return ComputeConvexHull(vertices);
                default:
                    return ExtractMeshOutline(vertices, triangles);
            }
        }

        /// <summary>
        /// 获取 Sprite 的 Physics Shape（需在 Sprite Editor 中预先设置）
        /// </summary>
        public static Vector2[] GetPhysicsShape(Sprite sprite)
        {
            if (sprite.GetPhysicsShapeCount() == 0)
            {
                Debug.LogWarning($"[{sprite.name}] 未定义 Physics Shape，请在 Sprite Editor 中设置物理形状。");
                return null;
            }

            List<Vector2> shape = new List<Vector2>();
            sprite.GetPhysicsShape(0, shape);
            return shape.ToArray();
        }

        /// <summary>
        /// 从网格三角面提取外轮廓（支持凹多边形）
        /// </summary>
        public static Vector2[] ExtractMeshOutline(Vector2[] vertices, ushort[] triangles)
        {
            if (triangles == null || triangles.Length == 0) return null;

            // 直接使用ushort遍历，避免复制为int[]
            var edgeCount = new Dictionary<(int a, int b), int>();

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];

                AddUndirectedEdge(edgeCount, a, b);
                AddUndirectedEdge(edgeCount, b, c);
                AddUndirectedEdge(edgeCount, c, a);
            }

            var boundaryEdges = new List<(int a, int b)>();
            foreach (var kvp in edgeCount)
            {
                if (kvp.Value == 1)
                    boundaryEdges.Add(kvp.Key);
            }

            if (boundaryEdges.Count == 0)
                return null;

            return OrderBoundaryEdges(vertices, boundaryEdges);
        }

        /// <summary>
        /// 从网格三角面提取外轮廓（支持凹多边形）
        /// </summary>
        public static Vector2[] ExtractMeshOutline(Vector2[] vertices, int[] triangles)
        {
            if (vertices == null || vertices.Length < 3 || triangles == null || triangles.Length < 3)
                return null;

            // 统计每条无向边的出现次数
            var edgeCount = new Dictionary<(int a, int b), int>();

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];

                AddUndirectedEdge(edgeCount, a, b);
                AddUndirectedEdge(edgeCount, b, c);
                AddUndirectedEdge(edgeCount, c, a);
            }

            // 只出现一次的边为边界边
            var boundaryEdges = new List<(int a, int b)>();
            foreach (var kvp in edgeCount)
            {
                if (kvp.Value == 1)
                    boundaryEdges.Add(kvp.Key);
            }

            if (boundaryEdges.Count == 0)
                return null;

            return OrderBoundaryEdges(vertices, boundaryEdges);
        }

        private static void AddUndirectedEdge(Dictionary<(int a, int b), int> edgeCount, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!edgeCount.TryGetValue(key, out int count))
                count = 0;
            edgeCount[key] = count + 1;
        }

        private static Vector2[] OrderBoundaryEdges(Vector2[] vertices, List<(int a, int b)> edges)
        {
            // 构建邻接表
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var (a, b) in edges)
            {
                if (!adjacency.TryGetValue(a, out var listA)) { listA = new List<int>(); adjacency[a] = listA; }
                if (!adjacency.TryGetValue(b, out var listB)) { listB = new List<int>(); adjacency[b] = listB; }
                listA.Add(b);
                listB.Add(a);
            }

            var path = new List<Vector2>(edges.Count + 1);
            var visited = new bool[vertices.Length];

            int start = edges[0].a;
            int current = start;

            do
            {
                path.Add(vertices[current]);
                visited[current] = true;

                if (!adjacency.TryGetValue(current, out var neighbors))
                    break;

                int next = -1;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    if (!visited[neighbors[i]])
                    {
                        next = neighbors[i];
                        break;
                    }
                }

                if (next == -1) break;
                current = next;
            } while (current != start);

            return path.ToArray();
        }

        /// <summary>
        /// 凸包算法（Andrew's Monotone Chain）
        /// </summary>
        public static Vector2[] ComputeConvexHull(Vector2[] points)
        {
            if (points == null || points.Length <= 3)
                return points?.Distinct().ToArray();

            // 使用HashSet去重
            var uniqueSet = new HashSet<Vector2>(points);
            var unique = new List<Vector2>(uniqueSet);
            if (unique.Count <= 3)
                return unique.ToArray();

            // 按x排序，x相同时按y排序
            unique.Sort((a, b) =>
            {
                int cmp = a.x.CompareTo(b.x);
                return cmp == 0 ? a.y.CompareTo(b.y) : cmp;
            });

            var hull = new List<Vector2>();

            // 下凸包
            foreach (var p in unique)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            // 上凸包
            int t = hull.Count + 1;
            for (int i = unique.Count - 2; i >= 0; i--)
            {
                Vector2 p = unique[i];
                while (hull.Count >= t && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1); // 移除重复的起点
            return hull.ToArray();
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        #endregion

        #region 路径简化 (Ramer-Douglas-Peucker)

        /// <summary>
        /// 使用 RDP 算法简化多边形路径
        /// </summary>
        public static Vector2[] SimplifyPath(Vector2[] points, float tolerance)
        {
            if (points == null || points.Length <= 3)
                return points;

            bool[] keep = new bool[points.Length];
            for (int i = 0; i < keep.Length; i++) keep[i] = true;

            RDPRecursive(points, 0, points.Length - 1, tolerance, keep);

            // 预分配结果列表，初始容量为估计值
            var result = new List<Vector2>(points.Length / 2);
            for (int i = 0; i < points.Length; i++)
            {
                if (keep[i])
                    result.Add(points[i]);
            }

            return result.ToArray();
        }

        private static void RDPRecursive(Vector2[] points, int start, int end, float tolerance, bool[] keep)
        {
            float maxDist = 0f;
            int maxIndex = 0;

            Vector2 a = points[start];
            Vector2 b = points[end];

            for (int i = start + 1; i < end; i++)
            {
                float dist = PointToLineDistance(points[i], a, b);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIndex = i;
                }
            }

            if (maxDist > tolerance)
            {
                keep[maxIndex] = true;
                RDPRecursive(points, start, maxIndex, tolerance, keep);
                RDPRecursive(points, maxIndex, end, tolerance, keep);
            }
        }

        private static float PointToLineDistance(Vector2 point, Vector2 lineStart, Vector2 lineEnd)
        {
            float lineLengthSqr = (lineEnd - lineStart).sqrMagnitude;
            if (lineLengthSqr == 0f)
                return Vector2.Distance(point, lineStart);

            float t = Mathf.Clamp01(Vector2.Dot(point - lineStart, lineEnd - lineStart) / lineLengthSqr);
            Vector2 projection = lineStart + t * (lineEnd - lineStart);
            return Vector2.Distance(point, projection);
        }

        #endregion

        #region 应用碰撞体

        /// <summary>
        /// 将路径应用到物体的 PolygonCollider2D
        /// </summary>
        public static void ApplyPolygonCollider(GameObject go, Vector2[] path, bool removeExisting)
        {
            Undo.RecordObject(go, "添加/修改碰撞体");

            if (removeExisting)
            {
                var existing = go.GetComponents<Collider2D>();
                foreach (var col in existing)
                {
                    Undo.DestroyObjectImmediate(col);
                }
            }

            PolygonCollider2D poly = go.GetComponent<PolygonCollider2D>();
            if (poly == null)
            {
                poly = Undo.AddComponent<PolygonCollider2D>(go);
            }
            else
            {
                Undo.RecordObject(poly, "更新碰撞体路径");
            }

            poly.pathCount = 1;
            poly.SetPath(0, path);
        }

        #endregion
    }

    /// <summary>
    /// 编辑器窗口：提供可视化界面生成碰撞体
    /// </summary>
    public class SpriteMeshColliderWindow : EditorWindow
    {
        private SpriteMeshColliderUtility.OutlineMode outlineMode =
            SpriteMeshColliderUtility.OutlineMode.MeshOutline;

        private float simplificationTolerance = 0.005f;
        private bool simplifyPath = true;
        private bool removeExistingColliders = true;
        private bool autoCloseWindow = false;

        [MenuItem("Tools/Sprite网格碰撞体生成器", false, 100)]
        public static void ShowWindow()
        {
            var window = GetWindow<SpriteMeshColliderWindow>("Sprite碰撞体生成器");
            window.minSize = new Vector2(400, 360);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(12);

            // 标题
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14
            };
            EditorGUILayout.LabelField("不规则多边形精灵网格碰撞体生成器", titleStyle);

            EditorGUILayout.Space(4);

            var subStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10
            };
            subStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
            EditorGUILayout.LabelField("基于网格顶点自动创建 PolygonCollider2D", subStyle);

            EditorGUILayout.Space(16);
            EditorGUILayout.LabelField("生成设置", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            outlineMode = (SpriteMeshColliderUtility.OutlineMode)EditorGUILayout.EnumPopup(
                "轮廓提取模式", outlineMode);

            // 模式说明
            switch (outlineMode)
            {
                case SpriteMeshColliderUtility.OutlineMode.PhysicsShape:
                    EditorGUILayout.HelpBox(
                        "使用 Sprite Editor 中定义的 Physics Shape 生成碰撞体。"
                        + "最准确，但需要预先在 Sprite Editor 中设置物理形状。",
                        MessageType.Info);
                    break;
                case SpriteMeshColliderUtility.OutlineMode.MeshOutline:
                    EditorGUILayout.HelpBox(
                        "从 Sprite 网格三角面提取外轮廓。支持凹多边形，能够准确还原不规则形状。",
                        MessageType.Info);
                    break;
                case SpriteMeshColliderUtility.OutlineMode.ConvexHull:
                    EditorGUILayout.HelpBox(
                        "使用凸包算法提取外轮廓。计算速度快，但不保留凹陷部分。",
                        MessageType.Info);
                    break;
            }

            EditorGUILayout.Space(4);

            if (outlineMode != SpriteMeshColliderUtility.OutlineMode.PhysicsShape)
            {
                simplifyPath = EditorGUILayout.Toggle("启用路径简化", simplifyPath);
                if (simplifyPath)
                {
                    EditorGUI.indentLevel++;
                    simplificationTolerance = EditorGUILayout.Slider(
                        "简化容差", simplificationTolerance, 0.001f, 0.1f);
                    EditorGUI.indentLevel--;
                }
            }

            removeExistingColliders = EditorGUILayout.Toggle("移除现有 2D 碰撞体", removeExistingColliders);
            autoCloseWindow = EditorGUILayout.Toggle("生成后自动关闭窗口", autoCloseWindow);

            EditorGUI.indentLevel--;

            EditorGUILayout.Space(16);

            // 选中物体统计
            int validCount = 0;
            int totalCount = Selection.gameObjects.Length;
            foreach (var go in Selection.gameObjects)
            {
                if (go.GetComponent<SpriteRenderer>() != null || go.GetComponent<MeshFilter>() != null)
                    validCount++;
            }

            if (totalCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "请在场景中选中至少一个带有 SpriteRenderer 或 MeshFilter 的物体。",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"选中物体: {totalCount} 个  |  有效目标: {validCount} 个",
                    MessageType.Info);
            }

            EditorGUILayout.Space(12);

            // 生成按钮
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.enabled = validCount > 0;
            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(20, 20, 10, 10)
            };

            if (GUILayout.Button("生成碰撞体", buttonStyle, GUILayout.Width(200), GUILayout.Height(42)))
            {
                GenerateColliders();
            }

            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // 使用提示
            EditorGUILayout.LabelField("使用提示", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("1. 选中场景中的目标物体（可多选）", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("2. 推荐先用 MeshOutline 模式生成", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("3. 生成后可手动编辑 PolygonCollider2D 微调", EditorStyles.wordWrappedLabel);
            EditorGUI.indentLevel--;
        }

        private void GenerateColliders()
        {
            int successCount = 0;
            int failCount = 0;

            Undo.SetCurrentGroupName("生成 Sprite 网格碰撞体");
            int group = Undo.GetCurrentGroup();

            foreach (GameObject go in Selection.gameObjects)
            {
                bool result = SpriteMeshColliderUtility.GenerateCollider(
                    go, outlineMode, removeExistingColliders, simplifyPath, simplificationTolerance);

                if (result) successCount++;
                else failCount++;
            }

            Undo.CollapseUndoOperations(group);

            if (successCount > 0)
            {
                EditorUtility.DisplayDialog(
                    "生成完成",
                    $"成功: {successCount} 个\n失败: {failCount} 个",
                    "确定");

                if (autoCloseWindow)
                    Close();
            }
            else if (failCount > 0)
            {
                EditorUtility.DisplayDialog(
                    "生成失败",
                    "没有选中有效的 SpriteRenderer 或 MeshFilter 物体，或轮廓提取失败。",
                    "确定");
            }
        }
    }

    /// <summary>
    /// 快捷菜单：在组件上下文菜单中直接生成碰撞体
    /// </summary>
    public static class SpriteMeshColliderMenus
    {
        [MenuItem("CONTEXT/SpriteRenderer/生成网格碰撞体 (MeshOutline)")]
        private static void GenerateFromSpriteRenderer(MenuCommand command)
        {
            SpriteRenderer sr = (SpriteRenderer)command.context;
            if (sr == null || sr.sprite == null) return;

            bool result = SpriteMeshColliderUtility.GenerateCollider(
                sr.gameObject,
                SpriteMeshColliderUtility.OutlineMode.MeshOutline,
                removeExisting: true,
                simplify: true,
                tolerance: 0.005f);

            if (result)
                ShowNotification(sr.gameObject, "MeshOutline 碰撞体已生成");
            else
                EditorUtility.DisplayDialog("失败", "无法提取有效网格轮廓。", "确定");
        }

        [MenuItem("CONTEXT/SpriteRenderer/生成凸包碰撞体 (ConvexHull)")]
        private static void GenerateConvexHullFromSpriteRenderer(MenuCommand command)
        {
            SpriteRenderer sr = (SpriteRenderer)command.context;
            if (sr == null || sr.sprite == null) return;

            bool result = SpriteMeshColliderUtility.GenerateCollider(
                sr.gameObject,
                SpriteMeshColliderUtility.OutlineMode.ConvexHull,
                removeExisting: true,
                simplify: false,
                tolerance: 0f);

            if (result)
                ShowNotification(sr.gameObject, "ConvexHull 碰撞体已生成");
            else
                EditorUtility.DisplayDialog("失败", "无法提取有效轮廓。", "确定");
        }

        [MenuItem("CONTEXT/MeshFilter/生成网格碰撞体 (MeshOutline)")]
        private static void GenerateFromMeshFilter(MenuCommand command)
        {
            MeshFilter mf = (MeshFilter)command.context;
            if (mf == null || mf.sharedMesh == null) return;

            bool result = SpriteMeshColliderUtility.GenerateCollider(
                mf.gameObject,
                SpriteMeshColliderUtility.OutlineMode.MeshOutline,
                removeExisting: true,
                simplify: true,
                tolerance: 0.005f);

            if (result)
                ShowNotification(mf.gameObject, "MeshOutline 碰撞体已生成");
            else
                EditorUtility.DisplayDialog("失败", "无法提取有效网格轮廓。", "确定");
        }

        private static void ShowNotification(GameObject go, string message)
        {
            EditorWindow.focusedWindow?.ShowNotification(new GUIContent(message), 2f);
            Debug.Log($"[{go.name}] {message}");
        }
    }
}
