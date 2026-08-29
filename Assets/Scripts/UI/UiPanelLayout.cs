using UnityEngine;

/// <summary>
/// 面板互斥协调器（2026-08-29 用户实测三面板同屏重叠灾难后建立）：
/// 同屏最多一个"大面板"（建筑 Tab / 委托 C），对话（E）打开时强制隐藏大面板——
/// 整体美学布局规则：三面板专属区固定（建筑=左、委托=右上、对话=底部中央），
/// 互斥靠协调器而非各面板自查（各自为政判例：布局权责必须单一来源）。
/// 静态类懒创建，无需场景接线。
/// </summary>
public static class UiPanelLayout
{
    public enum Panel { None = 0, Building = 1, Commission = 2, Dialog = 3 }

    private static Panel _current = Panel.None;
    /// <summary>当前持有显示权的大面板（Dialog 打开时其他一律让位）。</summary>
    public static Panel Current => _current;

    /// <summary>建筑面板当前是否应显示（BuildingPanel.Tab 切换时调用 Request，读此值渲染）。</summary>
    public static bool BuildingVisible => _current == Panel.Building;
    /// <summary>委托面板当前是否应显示（CommissionSystem.C 切换时调用 Request，读此值渲染）。</summary>
    public static bool CommissionVisible => _current == Panel.Commission;

    /// <summary>
    /// 请求切换面板显示权（Tab/C/E 的切换入口统一走这里）。
    /// 规则：请求当前已显示的面板=关闭它（Toggle 语义）；否则关旧开新。
    /// Dialog 优先级最高：打开时无条件抢占。
    /// </summary>
    public static void Request(Panel p)
    {
        _current = _current == p ? Panel.None : p;
    }

    /// <summary>强制关闭指定面板（Esc 关对话/面板自关闭时同步状态，防止幽灵持有权）。</summary>
    public static void Close(Panel p)
    {
        if (_current == p) _current = Panel.None;
    }

    /// <summary>强制清空（对话关闭/放置模式开始等场景）。</summary>
    public static void Clear()
    {
        _current = Panel.None;
    }
}
