using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 民国风 IMGUI 主题 v2：拆层组件皮肤 + 1.33 中文 type scale + 印章/分隔线辅助。
/// 素材约定（Resources/UI/v2/ 下，generate_game_ui_kit 拆层产物后处理）：
/// panel_main / panel_tall / panel_small / hud_bar /
/// button_normal / button_hover / button_active / button_red /
/// seal / cloud_a / cloud_b / divider_1~3。
/// v2 缺失时回退 v1（Resources/UI/ 根，整图烘焙版），再缺回退纯色。
/// GUIStyle 依赖 GUI.skin，必须在 OnGUI 内首次访问（各面板 OnGUI 已满足）。
///
/// ══════════════════════════════════════════════════════════════════
/// 设计系统契约（Design System，2026-08-29 建立——新 UI 一律从这里取，
/// 答不全"每个属性取哪个 token"= 先补系统，禁止现场发明样式）：
/// · 色板 4 角色：主色=宣纸 Paper（背景基调）/ 辅助 1=墨 Ink（文字/线条）/
///   辅助 2=淡墨 InkSoft（次级文字）/ 强调=朱红 Vermilion（CTA/验收，每屏 ≤3 处）；
///   扩展位：深金 Gold（奖励专用）、深绿 Green/深蓝 Blue（限定语义，系统内声明）
/// · 按钮 3 态：BtnPrimary（主操作，每屏 ≤1）/ Btn（次/常规）/
///   BtnLocked（禁用态，配 GUI.enabled=false）；
///   尺寸变体：BtnGrid（网格密排）/ BtnIcon（幽灵图标）——不是新角色
/// · 圆角：全局直角 + 细墨框（民国语言，禁大圆角 SaaS 风）
/// · 字体 2 角色：标题=KaiTi Bold（Title/Head/SecHead）/ 正文=KaiTi Regular
///   （Body/Hint/Field）——单字体，层级靠字重+1.33 字阶，不靠加字体
/// · 小组件底：PaperCard（素纸卡）/ CardFlat（票据卡）——大面板 9-slice
///   （Panel/Hud/Card）padding 按大面板设计，小组件禁用（吃空内容判例）
/// ══════════════════════════════════════════════════════════════════
///
/// 排版规范（classical-aesthetics-ui skill）：
/// - 字号只用 1.33 档位 12/14/16/20/28，层级靠字重+颜色不靠加档
/// - 间距全 4/8 倍数；组内 < 组间
/// - 强调色（朱红）≤3 处/屏：印章 / 酬劳数字 / 主按钮
/// </summary>
public static class UiTheme
{
    // ── 民国配色 ────────────────────────────────────────────────────────
    public static readonly Color Ink = new Color32(0x2B, 0x26, 0x20, 0xFF);       // 墨
    public static readonly Color InkSoft = new Color32(0x5A, 0x50, 0x42, 0xFF);   // 淡墨
    public static readonly Color Paper = new Color32(0xF0, 0xE6, 0xCE, 0xFF);     // 宣纸
    public static readonly Color Vermilion = new Color32(0x9E, 0x2B, 0x25, 0xFF); // 朱红
    public static readonly Color Brass = new Color32(0xC9, 0xA2, 0x27, 0xFF);     // 铜金
    public static readonly Color Gold = new Color32(0x8A, 0x5A, 0x00, 0xFF);      // 深金（奖励，压纸合格）
    public static readonly Color Green = new Color32(0x1E, 0x7A, 0x1E, 0xFF);     // 深绿（限定词）
    public static readonly Color Blue = new Color32(0x2E, 0x5F, 0x8A, 0xFF);      // 深蓝

    // ── 1.33 中文 type scale（12/14/16/20/28；层级靠字重+颜色）──────────
    // 2026-08-29 U2 可读性修正：Hint 12 在 960 高屏实际渲染 ~11px 低于汉字可读下限，
    // 且按钮 36 高装 16 号字=字高占容器 44%（低于 50~60% 法则）——
    // Hint 提到 14，按钮字号提 Emph 16→18（档位仍属 1.33 体系的 Emph 槽，只是按钮槽专用加大）。
    public const int SizeHint = 14;    // 弱提示/单位（12→14 可读性下限修正）
    public const int SizeBody = 14;    // 正文
    public const int SizeEmph = 16;    // 强调正文/委托名
    public const int SizeBtn = 18;     // 按钮字（16→18，字高=按钮 44px 高的 ~41%+padding≈55%）
    public const int SizeNum = 20;     // 状态数字（面板锚点）
    public const int SizeTitle = 20;   // 面板标题（与数字同级，靠粗细区分）
    public const int SizeHead = 24;    // 面板大标题（2026-08-29 用户反馈"标题加大加粗"：20 号在主面板层级不足）
    public const int SizeDisplay = 28; // 大字（闪屏/结算）

    private static bool _loaded;
    // v2 拆层组件
    private static Texture2D _v2PanelMain, _v2PanelTall, _v2PanelSmall, _v2Hud;
    private static Texture2D _v2Btn, _v2BtnHover, _v2BtnActive, _v2BtnRed;
    private static Texture2D _seal, _cloudA, _cloudB, _divider;
    // v1 兜底（整图烘焙版，回退保险）
    private static Texture2D _panel, _hud, _btn, _btnHover, _btnActive, _btnRed, _card;
    private static Texture2D _solidPaper, _solidRed;
    private static Font _kaiFont;

    private static GUIStyle _panelBox, _panelTallBox, _hudBox, _cardBox,
        _btnStyle, _btnLocked, _btnPrimary, _title, _body, _hint, _field, _secHead, _richStyle,
        _head, _btnGrid, _btnIcon, _cardFlat;

    // ── 全局缩放（高分屏 IMGUI 字号过小的根治）─────────────────────────
    /// <summary>GUI 全局缩放：参照 1080p=1.0（2026-08-29 从 840 提到 1080——用户实测
    /// 840 参照下面板占屏过大、字相对太小，违反"字高=容器 50~60%"法则；整体缩到 78%
    /// 后字号相对放大约 27%）。</summary>
    public static float Scale => Mathf.Clamp(Screen.height / 1080f, 0.9f, 1.8f);
    /// <summary>缩放后坐标系里的虚拟屏宽（布局定位一律用它，别直接用 Screen.width）。</summary>
    public static float VW => Screen.width / Scale;
    /// <summary>缩放后坐标系里的虚拟屏高。</summary>
    public static float VH => Screen.height / Scale;

    /// <summary>右上角右缘留白：编辑器里避开 Tuanjie Cowork 悬浮侧条（约 30 屏幕像素宽，外挂
    /// 覆盖层游戏无法隐藏只能让位）；打包构建后无此覆盖层，正常贴边。</summary>
    public static float RightMargin => Application.isEditor ? 48f : 16f;

    private static Matrix4x4 _prevMatrix;
    /// <summary>OnGUI 开头调用：之后所有绘制按 Scale 放大，结尾配对 EndScale。</summary>
    public static void BeginScale()
    {
        _prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(Scale, Scale, 1f));
    }
    /// <summary>OnGUI 结尾调用，与 BeginScale 配对。</summary>
    public static void EndScale() => GUI.matrix = _prevMatrix;

    // ── 公开样式 ────────────────────────────────────────────────────────
    /// <summary>主面板（委托大厅/建筑面板）：panel_main 9-slice，border 78 四边。</summary>
    public static GUIStyle Panel { get { EnsureStyles(); return _panelBox; } }
    /// <summary>高面板（对话）：panel_tall 9-slice，border 140。</summary>
    public static GUIStyle PanelTall { get { EnsureStyles(); return _panelTallBox; } }
    /// <summary>顶部 HUD 窄条：hud_bar 9-slice，L/R 70、T/B 55。</summary>
    public static GUIStyle Hud { get { EnsureStyles(); return _hudBox; } }
    /// <summary>卡片 / 结果小盒：panel_small 9-slice，border 70。</summary>
    public static GUIStyle Card { get { EnsureStyles(); return _cardBox; } }
    /// <summary>常规按钮（纸底墨字）：v2 页签底板，border 32，hover/active 有专属贴图。</summary>
    public static GUIStyle Btn { get { EnsureStyles(); return _btnStyle; } }
    /// <summary>禁用态按钮（设计系统按钮 3 态之一，语义=锁定/禁用统一走它）：
    /// 淡墨字，配 GUI.enabled=false 使用；禁用即"看得见但明确不可点"。</summary>
    public static GUIStyle BtnLocked { get { EnsureStyles(); return _btnLocked; } }
    /// <summary>主操作按钮（朱红底纸字）。</summary>
    public static GUIStyle BtnPrimary { get { EnsureStyles(); return _btnPrimary; } }
    /// <summary>面板标题（加粗墨字 20）。</summary>
    public static GUIStyle Title { get { EnsureStyles(); return _title; } }
    /// <summary>正文（14）。</summary>
    public static GUIStyle Body { get { EnsureStyles(); return _body; } }
    /// <summary>弱提示（淡墨 12）。</summary>
    public static GUIStyle Hint { get { EnsureStyles(); return _hint; } }
    /// <summary>区块头（菱形点+细墨线由面板自绘，这里给 16 加粗字）。</summary>
    public static GUIStyle SecHead { get { EnsureStyles(); return _secHead; } }
    /// <summary>输入框。</summary>
    public static GUIStyle Field { get { EnsureStyles(); return _field; } }

    /// <summary>面板大标题（加粗 24）——主面板层级锚点。</summary>
    public static GUIStyle Head { get { EnsureStyles(); return _head; } }

    /// <summary>密集网格按钮（14 号小字，模板选择网格等高密度区）。</summary>
    public static GUIStyle BtnGrid { get { EnsureStyles(); return _btnGrid; } }

    /// <summary>幽灵图标按钮（无底板，悬停字变朱红；面板右上角关闭 × 等）。</summary>
    public static GUIStyle BtnIcon { get { EnsureStyles(); return _btnIcon; } }

    /// <summary>扁平票据卡（酬劳行/结果盒等小组件）：复用按钮纸底贴图+16 padding。
    /// Card/Panel 的大 padding（88/96）按大面板内容区设计，小组件用它会被吃空——
    /// 2026-08-29 实测 Hud(84)/Card(88) 小卡文字被挤出判例后新增。</summary>
    public static GUIStyle CardFlat { get { EnsureStyles(); return _cardFlat; } }

    /// <summary>富文本单行样式（不换行，数字/行内混排用；字色墨、四态钉）。</summary>
    public static GUIStyle Rich { get { EnsureStyles(); return _richStyle; } }

    /// <summary>民国楷体（Resources/Fonts/KaiTi.ttf，simkai）。缺失时为 null，
    /// 各样式自行回退默认字体。</summary>
    public static Font KaiFont { get { EnsureLoaded(); return _kaiFont; } }

    /// <summary>印章贴图（z11 拆层，带透明通道）。缺失为 null，调用方跳过绘制。</summary>
    public static Texture2D SealTex { get { EnsureLoaded(); return _seal; } }

    private static readonly Dictionary<int, GUIStyle> _textCache = new Dictionary<int, GUIStyle>();

    /// <summary>
    /// 把字色钉进全部交互态（normal/hover/active/focused）。IMGUI 控件——包括 Label——
    /// 鼠标悬停/按下时会切换 hover/active 态，只设 normal 会继承默认皮肤的白字
    /// （默认皮肤按深色编辑器底设计），悬停那行瞬间变白压宣纸隐形。
    /// </summary>
    private static GUIStyle InkAllStates(GUIStyle s, Color color)
    {
        s.normal.textColor = color;
        s.hover.textColor = color;
        s.active.textColor = color;
        s.focused.textColor = color;
        return s;
    }

    /// <summary>
    /// 指定字号的墨色正文（按字号缓存）。IMGUI 的 GUI.skin.label 默认白字，
    /// 压宣纸底会隐形——面板内容文字一律用本方法，禁止裸 new GUIStyle(GUI.skin.label)。
    /// 字号传 1.33 档位常量（SizeHint/SizeBody/SizeEmph/SizeNum/SizeDisplay）。
    /// </summary>
    public static GUIStyle Text(int size = 14)
    {
        EnsureStyles();
        if (_textCache.TryGetValue(size, out var s)) return s;
        var style = new GUIStyle(GUI.skin.label)
        {
            richText = true,
            fontSize = size,
            wordWrap = true,
            font = _kaiFont,
        };
        _textCache[size] = InkAllStates(style, Ink);
        return style;
    }

    // ── 绘制辅助：印章 / 细墨线 / 目标梯度进度条 ────────────────────────

    /// <summary>
    /// 印章落款（民国"盖章=确认"隐喻）。OnGUI 内任意位置可调（自绘，不依赖 BeginArea）。
    /// size 为虚拟坐标系边长；缺失贴图时画朱红描边方框+文字兜底。
    /// </summary>
    public static void DrawSeal(Rect rect, float rotationDeg = -6f)
    {
        var prev = GUI.matrix;
        var pivot = new Vector2(rect.x + rect.width / 2f, rect.y + rect.height / 2f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, rotationDeg),
            Vector3.one) * Matrix4x4.TRS(new Vector3(pivot.x, pivot.y, 0f), Quaternion.identity, Vector3.one)
            * Matrix4x4.TRS(new Vector3(-pivot.x, -pivot.y, 0f), Quaternion.identity, Vector3.one);
        if (_seal != null)
        {
            GUI.DrawTexture(rect, _seal, ScaleMode.StretchToFill, true);
        }
        else
        {
            // 兜底：朱红描边空框
            var c = GUI.color;
            GUI.color = Vermilion;
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            GUI.color = c;
        }
        GUI.matrix = prev;
    }

    /// <summary>
    /// 细墨分隔线（报纸栏线）。rect.x~rect.xMax 横贯，粗细由 rect.height 控制（建议 1~2）。
    /// GUILayout 里用 GUILayoutUtility.GetRect 拿行矩形再调本方法。
    /// </summary>
    public static void DrawRule(Rect rect, float alpha = 0.35f)
    {
        var prev = GUI.color;
        GUI.color = new Color(Ink.r, Ink.g, Ink.b, alpha);
        GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
        GUI.color = prev;
    }

    /// <summary>
    /// 矩形描边（输入框聚焦高亮等）。thickness 建议 1.5~2.5，OnGUI 任意坐标系可调。
    /// </summary>
    public static void DrawFrame(Rect r, Color c, float thickness)
    {
        var prev = GUI.color;
        GUI.color = c;
        var t = Texture2D.whiteTexture;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, thickness), t);
        GUI.DrawTexture(new Rect(r.x, r.yMax - thickness, r.width, thickness), t);
        GUI.DrawTexture(new Rect(r.x, r.y, thickness, r.height), t);
        GUI.DrawTexture(new Rect(r.xMax - thickness, r.y, thickness, r.height), t);
        GUI.color = prev;
    }

    /// <summary>
    /// 面板投影：三层半透黑偏移矩形叠加，把浅色宣纸面板从暖色背景里"托"出来
    /// （2026-08-29 用户反馈"面板与背景融合度高、焦点不明"）。在 BeginArea 之前用
    /// 屏幕坐标调用（多层模拟软阴影，成本远低于动态贴图模糊）。
    /// </summary>
    public static void DrawShadow(Rect r)
    {
        var prev = GUI.color;
        var t = Texture2D.whiteTexture;
        GUI.color = new Color(0f, 0f, 0f, 0.20f);
        GUI.DrawTexture(new Rect(r.x + 3f, r.y + 4f, r.width, r.height), t);
        GUI.color = new Color(0f, 0f, 0f, 0.11f);
        GUI.DrawTexture(new Rect(r.x + 7f, r.y + 9f, r.width, r.height), t);
        GUI.color = new Color(0f, 0f, 0f, 0.05f);
        GUI.DrawTexture(new Rect(r.x + 12f, r.y + 15f, r.width, r.height), t);
        GUI.color = prev;
    }

    /// <summary>
    /// 素纸小卡（HUD 状态卡/键位提示卡等小件专用）：纸白填充+细墨描边，在本卡的
    /// GUILayout.BeginArea 内调用（从 (0,0) 画，同 Wash 局部坐标系契约）。
    /// 小卡禁用 9-slice 样式（Hud padding 84/每边会把 ~300px 宽小卡的内容区吃成负数，
    /// 文字被挤出卡外——2026-08-29 左右 HUD 文字不可见判例）。
    /// </summary>
    public static void PaperCard(Rect areaRect, float alpha = 0.92f)
    {
        var prev = GUI.color;
        GUI.color = new Color(0.97f, 0.95f, 0.91f, alpha);
        GUI.DrawTexture(new Rect(0f, 0f, areaRect.width, areaRect.height), Texture2D.whiteTexture);
        DrawFrame(new Rect(0.5f, 0.5f, areaRect.width - 1f, areaRect.height - 1f),
            new Color(Ink.r, Ink.g, Ink.b, 0.45f), 1f);
        GUI.color = prev;
    }

    /// <summary>
    /// 自绘关闭 ×（两根对角短棒）。楷体缺 ×/✕ 字形时文字方案渲染成实心方块
    /// （2026-08-29 建筑面板关闭钮判例），图形方案零字体依赖。OnGUI 任意坐标系可调。
    /// </summary>
    public static void DrawX(Rect r, Color c, float thickness = 2f)
    {
        var prev = GUI.matrix;
        var prevColor = GUI.color;
        var pivot = new Vector2(r.x + r.width / 2f, r.y + r.height / 2f);
        float bar = Mathf.Min(r.width, r.height) * 1.1f;
        GUI.color = c;
        foreach (var ang in new[] { 45f, -45f })
        {
            GUI.matrix = Matrix4x4.TRS(pivot, Quaternion.Euler(0f, 0f, ang), Vector3.one)
                       * Matrix4x4.TRS(new Vector3(-pivot.x, -pivot.y, 0f), Quaternion.identity, Vector3.one);
            GUI.DrawTexture(new Rect(pivot.x - bar / 2f, pivot.y - thickness / 2f, bar, thickness),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
        }
        GUI.matrix = prev;
        GUI.color = prevColor;
    }

    /// <summary>
    /// 目标梯度进度条（游戏心理学：进度必须可视化，不只数字）。
    /// value/max ∈[0,1]；6px 细墨轨道 + 深墨填充 + 朱红游标。OnGUI 自绘，任意位置可调。
    /// </summary>
    public static void DrawProgress(Rect rect, float value, float max)
    {
        float t = max <= 0f ? 0f : Mathf.Clamp01(value / max);
        // 轨道
        var prev = GUI.color;
        GUI.color = new Color(Ink.r, Ink.g, Ink.b, 0.18f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
        // 填充
        if (t > 0.001f)
        {
            GUI.color = InkSoft;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * t, rect.height),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            // 朱红游标（当前进度位置）
            GUI.color = Vermilion;
            GUI.DrawTexture(new Rect(rect.x + rect.width * t - 1f, rect.y - 2f, 2f, rect.height + 4f),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
        }
        GUI.color = prev;
    }

    // ── 资源与样式构建 ──────────────────────────────────────────────────
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // v2 拆层组件（首选）
        _v2PanelMain = Resources.Load<Texture2D>("UI/v2/panel_main");
        _v2PanelTall = Resources.Load<Texture2D>("UI/v2/panel_tall");
        _v2PanelSmall = Resources.Load<Texture2D>("UI/v2/panel_small");
        _v2Hud = Resources.Load<Texture2D>("UI/v2/hud_bar");
        _v2Btn = Resources.Load<Texture2D>("UI/v2/button_normal");
        _v2BtnHover = Resources.Load<Texture2D>("UI/v2/button_hover");
        _v2BtnActive = Resources.Load<Texture2D>("UI/v2/button_active");
        _v2BtnRed = Resources.Load<Texture2D>("UI/v2/button_red");
        _seal = Resources.Load<Texture2D>("UI/v2/seal");
        _cloudA = Resources.Load<Texture2D>("UI/v2/cloud_a");
        _cloudB = Resources.Load<Texture2D>("UI/v2/cloud_b");
        _divider = Resources.Load<Texture2D>("UI/v2/divider_1");
        // v1 兜底（回退保险）
        _panel = Resources.Load<Texture2D>("UI/panel_bg");
        _hud = Resources.Load<Texture2D>("UI/hud_bg");
        _btn = Resources.Load<Texture2D>("UI/button");
        _btnHover = Resources.Load<Texture2D>("UI/button_hover");
        _btnActive = Resources.Load<Texture2D>("UI/button_active");
        _btnRed = Resources.Load<Texture2D>("UI/button_red");
        _card = Resources.Load<Texture2D>("UI/card_bg");
        _kaiFont = Resources.Load<Font>("Fonts/KaiTi");
        _solidPaper = Solid(new Color32(0xEF, 0xE4, 0xCB, 0xF2));
        _solidRed = Solid(Vermilion);
    }

    private static void EnsureStyles()
    {
        EnsureLoaded();
        if (_panelBox != null) return;

        // v2 9-slice 实测参数（docs/ui-kit-v2/index.html 第四节）
        var borderPanel = new RectOffset(78, 78, 78, 78);     // panel_main
        var borderTall = new RectOffset(140, 140, 140, 140);  // panel_tall
        var borderHud = new RectOffset(70, 70, 55, 55);       // hud_bar
        var borderSmall = new RectOffset(70, 70, 70, 70);     // panel_small
        // 按钮已裁掉外圈 29px 毛边带（551×183），边框按比例 32→24（留内圈干净双线）
        var borderBtn = new RectOffset(24, 24, 24, 24);       // button_*
        // v1 兜底边框
        var borderV1Panel = new RectOffset(32, 32, 32, 32);
        var borderV1Hud = new RectOffset(28, 28, 28, 28);
        var borderV1Card = new RectOffset(12, 12, 12, 12);

        // v2 面板 padding = border + 1 字高（14）+ 安全余量 → 96/154/84
        // v1 面板 padding 沿用旧值（border 32 + 4）
        _panelBox = _v2PanelMain != null
            ? MakeBox(_v2PanelMain, borderPanel, 96, Ink, SizeBody)
            : MakeBox(_panel != null ? _panel : _solidPaper, borderV1Panel, 36, Ink, SizeBody);
        _panelTallBox = _v2PanelTall != null
            ? MakeBox(_v2PanelTall, borderTall, 154, Ink, SizeBody)
            : MakeBox(_panel != null ? _panel : _solidPaper, borderV1Panel, 36, Ink, SizeBody);
        _hudBox = _v2Hud != null
            ? MakeBox(_v2Hud, borderHud, 84, Ink, SizeBody)
            : MakeBox(_hud != null ? _hud : _solidPaper, borderV1Hud, 20, Ink, SizeBody);
        _cardBox = _v2PanelSmall != null
            ? MakeBox(_v2PanelSmall, borderSmall, 88, Ink, SizeBody)
            : MakeBox(_card != null ? _card : _solidPaper, borderV1Card, 18, Ink, SizeBody);

        // 按钮：v2 有专属 hover/active 贴图；v1 只有 normal（MakeButton 内沿用）
        var btnNormal = _v2Btn != null ? _v2Btn : _btn;
        var btnHover = _v2BtnHover != null ? _v2BtnHover : _btnHover;
        var btnActive = _v2BtnActive != null ? _v2BtnActive : _btnActive;
        var btnBorder = _v2Btn != null ? borderBtn : borderV1Card;
        // 按钮字用专用 18 号（SizeBtn）——16 号在 44px 高按钮里占比不足
        _btnStyle = MakeButton(btnNormal, btnHover, btnActive, btnBorder, Ink, SizeBtn);
        // 锁定态：同底板、淡墨字（灰显靠颜色不靠字号档）
        _btnLocked = MakeButton(btnNormal, null, null, btnBorder, InkSoft, SizeBtn);
        _btnPrimary = MakeButton(
            _v2BtnRed != null ? _v2BtnRed : (_btnRed != null ? _btnRed : _solidRed),
            null, null, btnBorder, Paper, SizeBtn);
        // 状态反馈增强（2026-08-29 用户反馈"按钮无状态暗示"）：悬停字变色——
        // 常规按钮墨字→朱红，主按钮纸字→铜金；锁定态保持灰显不变色
        _btnStyle.hover.textColor = Vermilion;
        _btnPrimary.hover.textColor = Brass;

        // 密集网格按钮：14 号（3 字模板名+窄 padding 装得进 ~64px 列宽）
        _btnGrid = MakeButton(btnNormal, btnHover, btnActive, btnBorder, Ink, SizeBody);
        _btnGrid.padding = new RectOffset(4, 4, 4, 4);
        _btnGrid.hover.textColor = Vermilion;
        _btnGrid.active.textColor = Vermilion;

        // 幽灵按钮：无底板纯文字，悬停朱红（关闭 × 等小图标位）
        _btnIcon = new GUIStyle(GUI.skin.button)
        {
            border = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0),
            fontSize = SizeEmph,
            font = _kaiFont,
            alignment = TextAnchor.MiddleCenter,
        };
        _btnIcon.normal.background = null;
        _btnIcon.hover.background = null;
        _btnIcon.active.background = null;
        _btnIcon.focused.background = null;
        _btnIcon.normal.textColor = InkSoft;
        _btnIcon.hover.textColor = Vermilion;
        _btnIcon.active.textColor = Vermilion;
        _btnIcon.focused.textColor = InkSoft;

        // 扁平票据卡：按钮纸底 + 中等 padding（小组件专用，见属性注释）
        _cardFlat = MakeBox(btnNormal != null ? btnNormal : _solidPaper,
            btnBorder, 16, Ink, SizeBody);

        _title = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = SizeTitle,
            font = _kaiFont,
        }, Ink);

        _head = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = SizeHead,
            font = _kaiFont,
        }, Ink);

        _body = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            richText = true,
            fontSize = SizeBody,
            font = _kaiFont,
        }, Ink);

        _hint = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            richText = true,
            fontSize = SizeHint,
            font = _kaiFont,
        }, InkSoft);

        _secHead = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            richText = true,
            fontStyle = FontStyle.Bold,
            fontSize = SizeEmph,
            font = _kaiFont,
        }, Ink);

        _richStyle = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            richText = true,
            wordWrap = false,
            fontSize = SizeEmph,
            font = _kaiFont,
        }, Ink);

        _field = new GUIStyle(GUI.skin.textField);
        // 输入框底用内置 whiteTexture（运行时自建贴图在团结下会失效跌回深色默认皮肤）
        _field.normal.background = Texture2D.whiteTexture;
        _field.focused.background = Texture2D.whiteTexture;
        _field.hover.background = Texture2D.whiteTexture;
        _field.active.background = Texture2D.whiteTexture;
        _field.border = new RectOffset(2, 2, 2, 2);
        _field.normal.textColor = Ink;
        _field.focused.textColor = Ink;
        // IMGUI 状态优先级 active > hover > focused：点击/悬停输入框走 hover/active 态，
        // 不覆盖的话会落回默认皮肤白字，压宣纸底看不清
        _field.hover.textColor = Ink;
        _field.active.textColor = Ink;
        _field.fontSize = SizeEmph;
        _field.font = _kaiFont;
    }

    private static GUIStyle MakeBox(Texture2D bg, RectOffset border, int padding, Color textColor, int fontSize)
    {
        var s = new GUIStyle(GUI.skin.box)
        {
            border = border,
            padding = new RectOffset(padding, padding, padding, padding),
            font = _kaiFont,
        };
        if (bg != null) s.normal.background = bg;
        InkAllStates(s, textColor);
        s.fontSize = fontSize;
        return s;
    }

    private static GUIStyle MakeButton(Texture2D normal, Texture2D hover, Texture2D active,
        RectOffset border, Color textColor, int fontSize)
    {
        var s = new GUIStyle(GUI.skin.button)
        {
            border = border,
            padding = new RectOffset(10, 10, 6, 6),
            fontSize = fontSize,
            font = _kaiFont,
        };
        // hover/active 未提供贴图时固定沿用 normal 背景：
        // 留空会继承默认皮肤的灰白高亮，鼠标悬停时整块跳变（民国底色全丢）
        if (normal != null)
        {
            s.normal.background = normal;
            if (hover == null) s.hover.background = normal;
            if (active == null) s.active.background = active != null ? active : normal;
            else s.active.background = active;
            if (hover != null) s.hover.background = hover;
        }
        else
        {
            if (hover != null) s.hover.background = hover;
            if (active != null) s.active.background = active;
        }
        s.normal.textColor = textColor;
        s.hover.textColor = textColor;
        s.active.textColor = textColor;
        s.focused.textColor = textColor;
        return s;
    }

    /// <summary>
    /// 宣纸衬底：BeginArea(style) 后立刻调用，轻盖面板贴图内侧统一纸色。
    /// 2026-08-29 U2 去 AI 味修正：v2 贴图内容区已程序化均化（deai.ps1，std<3），
    /// Wash 只需 0.30 轻透统一——原 0.5 双重暖色叠加=「油纸闷感」（AI 味来源之一）；
    /// 色从暖宣纸(0.94,0.89,0.80)改中性纸白(0.96,0.94,0.90)去黄提透。
    /// HUD/特殊需求仍可传 0.9+ 近实底。
    /// 用内置 whiteTexture+GUI.color 染色：运行时 new 的 Texture2D 在团结引擎下会被
    /// 置空（实测 Wash 报 null texture），内置贴图永不失效。
    /// **契约：只能在 GUILayout.BeginArea 内调用**——内部从 (0,0) 画，依赖 BeginArea
    /// 的局部坐标系。裸调（无 BeginArea）会把纸画到屏幕左上角（信笺 bug 判例 2026-08-29），
    /// 自绘场景一律直接 GUI.DrawTexture(rect, Texture2D.whiteTexture)。
    /// </summary>
    public static void Wash(Rect areaRect, float alpha = 0.30f)
    {
        var prev = GUI.color;
        GUI.color = new Color(0.96f, 0.94f, 0.90f, alpha); // 中性纸白（去黄提透）
        GUI.DrawTexture(new Rect(0f, 0f, areaRect.width, areaRect.height), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    private static Texture2D Solid(Color c)
    {
        var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        t.name = "UiThemeSolid";
        var px = new Color[16];
        for (int i = 0; i < 16; i++) px[i] = c;
        t.SetPixels(px);
        t.Apply(false, true);
        return t;
    }
}
