using UnityEngine;
using TMPro;
using System.Text;

/// <summary>
/// 烧杯容量 / 液位标签。
/// 显示：试剂名、体积(mL)、液位百分比，并在容器右侧绘制竖直液位条。
/// 所有标签样式配置（字体大小、每格毫升数、偏移、底板、液位% 开关、液位条等）都只在本组件内定义一次，
/// 由 LayerGridPainter 自动创建并只传入功能引用（painter / source），不重复定义任何样式。
/// </summary>
[DisallowMultipleComponent]
public class LiquidVolumeUI : MonoBehaviour
{
    [Header("功能引用（由 LayerGridPainter 自动填充，通常无需手动设）")]
    [Tooltip("网格绘制器，用于统计区域内水量。留空则自动查找场景中的 LayerGridPainter")]
    public LayerGridPainter painter;
    [Tooltip("关联的液体源。留空则向上查找 LiquidSource")]
    public LiquidSource source;

    [Header("标签配置（仅在此处定义一次）")]
    [Tooltip("每格水代表的液体体积（mL），用于把水格数换算成 mL")]
    public float mLPerCell = 1f;
    [Tooltip("字号相对底板可用区域的微调系数：1=恰好填满底板留白区，>1 更大，<1 更小")]
    public float labelFontSize = 1f;
    [Tooltip("标签自容器顶部向下的偏移量（世界单位）。越大标签在容器内垂得越低")]
    public float labelOffsetY = 0.3f;
    [Tooltip("是否在黑字后面显示白色矩形底板（关闭则只剩黑字、无底块）")]
    public bool showLabelBackdrop = true;
    [Tooltip("白色底块宽度相对容器宽度的系数（底块宽 = 容器宽 × 此值）")]
    public float backdropWidthScale = 1.1f;
    [Tooltip("白色底块高度相对容器高度的系数（底块高 = 容器高 × 此值）")]
    public float backdropHeightScale = 0.55f;
    [Tooltip("是否在标签上显示液位百分比（液位% = 水格数 / 容器容量格数）")]
    public bool showFillPercent = true;
    [Tooltip("是否显示容器右侧的竖直液位条")]
    public bool showFillBar = true;
    [Tooltip("竖直液位条宽度（世界单位）")]
    public float fillBarWidth = 0.06f;
    [Tooltip("竖直液位条颜色")]
    public Color fillBarColor = new Color(0.2f, 0.7f, 1f, 0.9f);

    [Tooltip("标签刷新间隔（秒）。降低刷新频率可减少每帧的整网格扫描次数")]
    public float refreshInterval = 0.2f;

    private TextMeshPro m_label;
    private SpriteRenderer m_back;
    private SpriteRenderer m_fillBar;
    private Renderer m_labelRenderer;
    private StringBuilder m_textBuilder = new StringBuilder(64);
    private float m_refreshTimer;
    private bool m_initialized;
    private int m_cachedWater;   // 缓存水量格数，拖拽/旋转时沿用，避免毫升数跳动
    private int m_cachedCap;     // 缓存容量格数，拖拽/旋转时沿用

    // 共享白色贴图（所有标签共用一张，避免重复分配）
    private static Texture2D s_whiteTex;
    private static Sprite s_whiteSprite;

    // 复用的角点缓冲：避免 GetLocalContainerBounds 每次调用都 new 一个数组（每刷新一次就分配一次）
    private static readonly Vector3[] s_cornerBuffer = new Vector3[4];
    private static Sprite WhiteSprite
    {
        get
        {
            if (s_whiteSprite == null)
            {
                s_whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                s_whiteTex.SetPixel(0, 0, Color.white);
                s_whiteTex.Apply();
                s_whiteSprite = Sprite.Create(s_whiteTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            }
            return s_whiteSprite;
        }
    }

    private void Awake()
    {
        ResolveReferences();
        BuildLabelObjects();
        m_initialized = true;
        Refresh();
    }

    private void ResolveReferences()
    {
        if (painter == null)
            painter = LayerGridPainter.Instance;
        if (source == null)
            source = GetComponentInParent<LiquidSource>();
    }

    private void BuildLabelObjects()
    {
        // 标签文字载体（按 labelFontSize 缩放）
        GameObject lab = new GameObject("VolumeLabel");
        lab.transform.SetParent(transform, false);
        m_label = lab.AddComponent<TextMeshPro>();
        TMP_FontAsset fa = TMP_Settings.defaultFontAsset;
        if (fa == null) fa = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (fa != null) m_label.font = fa;
        m_label.alignment = TextAlignmentOptions.Center;
        m_label.fontSize = 12;
        m_label.color = Color.black;
        m_labelRenderer = m_label.GetComponent<Renderer>();
        m_labelRenderer.sortingOrder = 102;

        // 白色底板（底块，不随字号缩放，按世界尺寸设置）
        GameObject back = new GameObject("VolumeLabelBack");
        back.transform.SetParent(transform, false);
        m_back = back.AddComponent<SpriteRenderer>();
        m_back.sprite = WhiteSprite;
        m_back.color = Color.white;
        m_back.sortingOrder = 101;

        // 竖直液位条（容器右侧，高度随液位缩放；不随字号缩放，按世界尺寸设置）
        GameObject fb = new GameObject("VolumeFillBar");
        fb.transform.SetParent(transform, false);
        m_fillBar = fb.AddComponent<SpriteRenderer>();
        m_fillBar.sprite = WhiteSprite;
        m_fillBar.color = fillBarColor;
        m_fillBar.sortingOrder = 100;
    }

    private void Update()
    {
        if (!m_initialized || painter == null || source == null) return;
        m_refreshTimer -= Time.deltaTime;
        if (m_refreshTimer > 0f) return;
        m_refreshTimer = refreshInterval;
        Refresh();
    }

    private void Refresh()
    {
        // 使用局部空间 bounds，标签位置不受容器旋转影响
        Bounds cb = GetLocalContainerBounds();

        // 锚点：容器顶部中心，再向下偏移 labelOffsetY（全部在局部空间，无需世界↔局部转换）
        Vector3 localAnchor = cb.center + Vector3.up * (cb.size.y * 0.5f - labelOffsetY);

        Transform labT = m_label.transform;
        labT.localPosition = localAnchor;
        // 底板位置（与标签同锚点，略靠后）
        m_back.transform.localPosition = localAnchor + Vector3.back * 0.01f;

        // 拖拽 / 旋转过程中不重新统计水量，沿用上次缓存值，避免毫升数频繁跳动
        bool suppressUpdate = painter.IsDragging || painter.IsRotating;
        if (!suppressUpdate)
        {
            m_cachedWater = (source != null && !source.IsEmpty())
                ? painter.GetWaterCountInRegion(source.regionColliders) : 0;
            m_cachedCap = (source != null)
                ? painter.GetCellCountInRegion(source.regionColliders) : 0;
        }
        int water = m_cachedWater;
        int cap = m_cachedCap;

        UpdateText(water, cap);
        UpdateFillBar(cb, water, cap);

        // ===== 尺寸依赖链：容器大小 → 白色底板尺寸 → 文字大小 =====
        m_back.gameObject.SetActive(showLabelBackdrop);

        // 1. 白色底板尺寸完全由容器大小决定（局部空间，旋转后保持不变）
        float desiredWidth  = Mathf.Max(cb.size.x * backdropWidthScale, 0.1f);
        float desiredHeight = Mathf.Max(cb.size.y * backdropHeightScale, 0.1f);

        if (showLabelBackdrop && m_back.sprite != null)
        {
            // localScale 是相对于父级的缩放：局部尺寸 = localScale × 精灵原生尺寸
            float spriteW = m_back.sprite.bounds.size.x;
            float spriteH = m_back.sprite.bounds.size.y;
            if (spriteW < 1e-4f) spriteW = 0.01f;
            if (spriteH < 1e-4f) spriteH = 0.01f;

            m_back.transform.localScale = new Vector3(
                desiredWidth  / spriteW,
                desiredHeight / spriteH,
                1f
            );
        }

        // 2. 文字缩放：测量 scale=1 下的自然世界尺寸，由底板内部可用区域决定字号，保证文字不超出底板
        labT.localScale = Vector3.one;
        m_label.ForceMeshUpdate();
        float naturalW = m_label.bounds.size.x;
        float naturalH = m_label.bounds.size.y;
        if (naturalW < 1e-4f) naturalW = 0.5f;
        if (naturalH < 1e-4f) naturalH = 0.5f;

        float marginX = desiredWidth  * 0.08f + 0.04f;
        float marginY = desiredHeight * 0.08f + 0.04f;
        float availW = Mathf.Max(0.01f, desiredWidth  - marginX * 2f);
        float availH = Mathf.Max(0.01f, desiredHeight - marginY * 2f);
        float scale = Mathf.Min(availW / naturalW, availH / naturalH) * labelFontSize;
        scale = Mathf.Max(scale, 0.02f);
        labT.localScale = Vector3.one * scale;
    }

    /// <summary>
    /// 获取容器在局部空间中的包围盒（不受容器旋转影响）。
    /// 将每个碰撞器的世界 AABB 角点逆变换回局部空间后取包围盒。
    /// 对于 90° 倍数旋转，这等价于原始局部 bounds，旋转后尺寸/中心保持稳定。
    /// </summary>
    private Bounds GetLocalContainerBounds()
    {
        Bounds b = new Bounds();
        bool has = false;
        Collider2D[] cols = (source != null) ? source.regionColliders : null;
        if (cols != null)
        {
            foreach (var c in cols)
            {
                if (c == null) continue;
                Bounds wb = c.bounds;
                // 取世界 AABB 的 4 个角点，逆变换回局部空间
                Vector3 min = wb.min, max = wb.max;
                s_cornerBuffer[0].Set(min.x, min.y, min.z);
                s_cornerBuffer[1].Set(max.x, min.y, min.z);
                s_cornerBuffer[2].Set(min.x, max.y, min.z);
                s_cornerBuffer[3].Set(max.x, max.y, min.z);
                foreach (var corner in s_cornerBuffer)
                {
                    Vector3 localPt = transform.InverseTransformPoint(corner);
                    if (!has) { b = new Bounds(localPt, Vector3.zero); has = true; }
                    else b.Encapsulate(localPt);
                }
            }
        }
        if (!has)
        {
            // 退化情况：没有任何区域碰撞器时，用原点当一点
            b = new Bounds(Vector3.zero, Vector3.one * 0.5f);
        }
        return b;
    }

    private void UpdateText(int water, int cap)
    {
        if (source == null) return;

        // 空容器（试剂/液体类型为 "none"）：此处可装液体但当前无液体，标签只显示 "none"
        if (source.IsEmpty())
        {
            m_label.text = LiquidSource.EMPTY_MARKER;
            return;
        }

        // 毫升数取整百（四舍五入）；液位条仍用精确比值，不受影响
        float vol = water * mLPerCell;
        int volRounded = Mathf.FloorToInt(vol / 100f + 0.5f) * 100;

        m_textBuilder.Length = 0;
        m_textBuilder.Append(source.liquidType);
        m_textBuilder.Append('\n');
        m_textBuilder.Append(volRounded.ToString());
        m_textBuilder.Append(" mL");
        if (showFillPercent && cap > 0)
        {
            float pct = (float)water / cap * 100f;
            m_textBuilder.Append('\n');
            m_textBuilder.Append(pct.ToString("F0"));
            m_textBuilder.Append('%');
        }
        m_label.text = m_textBuilder.ToString();
    }

    private void UpdateFillBar(Bounds cb, int water, int cap)
    {
        bool show = showFillBar && source != null && !source.IsEmpty() && cap > 0;
        m_fillBar.gameObject.SetActive(show);
        if (!show) return;

        float ratio = Mathf.Clamp01((float)water / cap);
        float h = cb.size.y * ratio;
        float w = Mathf.Max(fillBarWidth, 0.01f);

        // 容器右侧内壁，底边对齐容器底（全部在局部空间，旋转后位置稳定）
        float rightX = cb.center.x + cb.size.x * 0.5f - w * 0.5f;
        float bottomY = cb.center.y - cb.size.y * 0.5f;
        Vector3 localPos = new Vector3(rightX, bottomY + h * 0.5f, cb.center.z - 0.02f);

        m_fillBar.transform.localPosition = localPos;
        m_fillBar.transform.localScale = new Vector3(w, Mathf.Max(h, 0.001f), 1f);
    }
}
