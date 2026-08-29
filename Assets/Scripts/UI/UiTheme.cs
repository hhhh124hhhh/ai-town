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
    public const int SizeHint = 12;    // 弱提示/单位
    public const int SizeBody = 14;    // 正文
    public const int SizeEmph = 16;    // 强调正文/委托名
    public const int SizeNum = 20;     // 状态数字（面板锚点）
    public const int SizeTitle = 20;   // 面板标题（与数字同级，靠粗细区分）
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
        _btnStyle, _btnLocked, _btnPrimary, _title, _body, _hint, _field, _secHead, _richStyle;

    // ── 全局缩放（高分屏 IMGUI 字号过小的根治）─────────────────────────
    /// <summary>GUI 全局缩放：1080p≈1.2、1440p≈1.6、720p=1。IMGUI 字号不随分辨率变，
    /// 2K/4K 下 12px 字小到不可读，统一按渲染高度放大整套 UI。</summary>
    public static float Scale => Mathf.Clamp(Screen.height / 840f, 1f, 2.2f);
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
    /// <summary>锁定态按钮（淡墨字，GUI.enabled=false 配合）：同底板换 InkSoft 字色。</summary>
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
        var borderBtn = new RectOffset(32, 32, 32, 32);       // button_*
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
        _btnStyle = MakeButton(btnNormal, btnHover, btnActive, btnBorder, Ink, SizeEmph);
        // 锁定态：同底板、淡墨字（灰显靠颜色不靠字号档）
        _btnLocked = MakeButton(btnNormal, null, null, btnBorder, InkSoft, SizeEmph);
        _btnPrimary = MakeButton(
            _v2BtnRed != null ? _v2BtnRed : (_btnRed != null ? _btnRed : _solidRed),
            null, null, btnBorder, Paper, SizeEmph);

        _title = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = SizeTitle,
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
    /// 宣纸衬底：BeginArea(style) 后立刻调用，用半透明宣纸盖住面板贴图内侧的
    /// 装饰纹理。v2 组件内容区本来干净，默认 alpha 降到 0.5 只统一纸色；
    /// v1 整图烘焙版才需要 0.88 重盖。
    /// 用内置 whiteTexture+GUI.color 染色：运行时 new 的 Texture2D 在团结引擎下会被
    /// 置空（实测 Wash 报 null texture），内置贴图永不失效。
    /// **契约：只能在 GUILayout.BeginArea 内调用**——内部从 (0,0) 画，依赖 BeginArea
    /// 的局部坐标系。裸调（无 BeginArea）会把纸画到屏幕左上角（信笺 bug 判例 2026-08-29），
    /// 自绘场景一律直接 GUI.DrawTexture(rect, Texture2D.whiteTexture)。
    /// </summary>
    public static void Wash(Rect areaRect, float alpha = 0.5f)
    {
        var prev = GUI.color;
        GUI.color = new Color(0.94f, 0.89f, 0.80f, alpha); // 宣纸色
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
