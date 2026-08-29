using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 民国风 IMGUI 主题：宣纸底 + 墨线描边 + 朱红点缀。
/// 素材约定（Resources/UI/ 下，来自 generate_game_ui_kit 拆层产物重命名）：
/// panel_bg / hud_bg / button / button_hover / button_active / button_red / card_bg。
/// 任一素材缺失时回退纯色染色纹理，保证无素材也可用。
/// GUIStyle 依赖 GUI.skin，必须在 OnGUI 内首次访问（各面板 OnGUI 已满足）。
/// </summary>
public static class UiTheme
{
    // ── 民国配色 ────────────────────────────────────────────────────────
    public static readonly Color Ink = new Color32(0x2B, 0x26, 0x20, 0xFF);       // 墨
    public static readonly Color InkSoft = new Color32(0x5A, 0x50, 0x42, 0xFF);   // 淡墨
    public static readonly Color Paper = new Color32(0xF0, 0xE6, 0xCE, 0xFF);     // 宣纸
    public static readonly Color Vermilion = new Color32(0x9E, 0x2B, 0x25, 0xFF); // 朱红
    public static readonly Color Brass = new Color32(0xC9, 0xA2, 0x27, 0xFF);     // 铜金

    private static bool _loaded;
    private static Texture2D _panel, _hud, _btn, _btnHover, _btnActive, _btnRed, _card;
    private static Texture2D _solidInk, _solidPaper, _solidRed;
    private static Font _kaiFont;

    private static GUIStyle _panelBox, _hudBox, _cardBox, _btnStyle, _btnPrimary, _title, _body, _hint, _field;

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
    /// <summary>宽面板底（对话 / 委托大厅 / 建筑面板）。</summary>
    public static GUIStyle Panel { get { EnsureStyles(); return _panelBox; } }
    /// <summary>顶部 HUD 窄条。</summary>
    public static GUIStyle Hud { get { EnsureStyles(); return _hudBox; } }
    /// <summary>卡片 / 结果小盒。</summary>
    public static GUIStyle Card { get { EnsureStyles(); return _cardBox; } }
    /// <summary>常规按钮（纸底墨字）。</summary>
    public static GUIStyle Btn { get { EnsureStyles(); return _btnStyle; } }
    /// <summary>主操作按钮（朱红底纸字）。</summary>
    public static GUIStyle BtnPrimary { get { EnsureStyles(); return _btnPrimary; } }
    /// <summary>面板标题（加粗墨字）。</summary>
    public static GUIStyle Title { get { EnsureStyles(); return _title; } }
    /// <summary>正文。</summary>
    public static GUIStyle Body { get { EnsureStyles(); return _body; } }
    /// <summary>弱提示（淡墨小字）。</summary>
    public static GUIStyle Hint { get { EnsureStyles(); return _hint; } }
    /// <summary>输入框。</summary>
    public static GUIStyle Field { get { EnsureStyles(); return _field; } }

    /// <summary>民国楷体（Resources/Fonts/KaiTi.ttf，simkai）。缺失时为 null，
    /// 各样式自行回退默认字体。</summary>
    public static Font KaiFont { get { EnsureLoaded(); return _kaiFont; } }

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
    /// </summary>
    public static GUIStyle Text(int size = 16)
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

    // ── 资源与样式构建 ──────────────────────────────────────────────────
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _panel = Resources.Load<Texture2D>("UI/panel_bg");
        _hud = Resources.Load<Texture2D>("UI/hud_bg");
        _btn = Resources.Load<Texture2D>("UI/button");
        _btnHover = Resources.Load<Texture2D>("UI/button_hover");
        _btnActive = Resources.Load<Texture2D>("UI/button_active");
        _btnRed = Resources.Load<Texture2D>("UI/button_red");
        _card = Resources.Load<Texture2D>("UI/card_bg");
        _kaiFont = Resources.Load<Font>("Fonts/KaiTi");
        _solidInk = Solid(new Color32(0x33, 0x2C, 0x24, 0xF0));
        _solidPaper = Solid(new Color32(0xEF, 0xE4, 0xCB, 0xF2));
        _solidRed = Solid(Vermilion);
    }

    private static void EnsureStyles()
    {
        EnsureLoaded();
        if (_panelBox != null) return;

        // 9-slice 边框：贴图边缘按固定像素拉伸，中部平铺质感
        // 素材为设计稿 ~2.9x 导出，木框实际约 30px
        var border24 = new RectOffset(32, 32, 32, 32);
        var border16 = new RectOffset(28, 28, 28, 28);
        var border12 = new RectOffset(12, 12, 12, 12);

        // 内边距必须大于木框在屏上的实际厚度（约 32px），且 ≥1 字高——否则文字压木纹上看不清
        _panelBox = MakeBox(_panel != null ? _panel : _solidPaper, border24, 36,
            Ink, 15);
        _hudBox = MakeBox(_hud != null ? _hud : _solidPaper, border16, 20,
            Ink, 14);
        _cardBox = MakeBox(_card != null ? _card : _solidPaper, border12, 18,
            Ink, 14);

        _btnStyle = MakeButton(_btn, _btnHover, _btnActive, border12,
            Ink, 16);
        _btnPrimary = MakeButton(_btnRed != null ? _btnRed : _solidRed, null, null, border12,
            Paper, 16);

        _title = InkAllStates(new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 20,
            font = _kaiFont,
        }, Ink);

        _body = InkAllStates(new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16, font = _kaiFont }, Ink);

        _hint = InkAllStates(new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14, font = _kaiFont }, InkSoft);

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
        _field.fontSize = 16;
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
            if (active == null) s.active.background = normal;
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
    /// 装饰纹理（设计稿残影），深色文字不再压在木框/花纹上。alpha 越大越素净。
    /// 用内置 whiteTexture+GUI.color 染色：运行时 new 的 Texture2D 在团结引擎下会被
    /// 置空（实测 Wash 报 null texture），内置贴图永不失效。
    /// **契约：只能在 GUILayout.BeginArea 内调用**——内部从 (0,0) 画，依赖 BeginArea
    /// 的局部坐标系。裸调（无 BeginArea）会把纸画到屏幕左上角（信笺 bug 判例 2026-08-29），
    /// 自绘场景一律直接 GUI.DrawTexture(rect, Texture2D.whiteTexture)。
    /// </summary>
    public static void Wash(Rect areaRect, float alpha = 0.88f)
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
