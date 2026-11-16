using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

namespace TeammateCheckMod
{
    /// <summary>
    /// 局内左下角的小浮窗：列出「疑似组队」的两两组合。
    ///
    /// 全部用 FairyGUI 在代码里搭，照 ForkDirectionMod 的做法：先往 GRoot 上挂一个
    /// 全屏、不吃点击、sortingOrder 很大的 GComponent 当图层，再往图层里加底板和文字。
    ///
    /// 为什么必须给图层设 sortingOrder：FairyGUI 的 GComponent.AddChildAt 里，
    /// sortingOrder != 0 的子对象会被插入到所有 0 序号子对象之后（即渲染在上层），
    /// 而之后新加的窗口只会插到 0 号区域里。只靠 SetChildIndex 压不住游戏自己的
    /// Window（关掉/重开窗口就会重新盖住我们），所以用 sortingOrder 才是稳定的。
    /// 任何一步失败都只是「没有浮窗」，不影响扫描本身。
    /// </summary>
    internal sealed class PairPanel
    {
        private const float Width = 340f;
        private const float LineHeight = 20f;

        /// <summary>远大于游戏窗口默认的 0，也大于常见的弹窗图层。</summary>
        private const int SortingOrder = 30000;

        private readonly Action<string> _warn;

        private GComponent _layer;
        private GGraph _bg;
        private GTextField _title;
        private GTextField _body;
        private GTextField _footer;

        private bool _failed;
        private bool _visible;

        public PairPanel(Action<string> warn)
        {
            _warn = warn;
        }

        public bool Visible { get { return _visible; } }

        /// <summary>更新并显示；lines 为空时显示 placeholder。</summary>
        public void Show(string title, List<string> lines, string placeholder, string footer)
        {
            try
            {
                if (!Ensure()) return;

                List<string> effective = (lines == null || lines.Count == 0)
                    ? new List<string> { placeholder ?? string.Empty }
                    : lines;

                float bodyHeight = Mathf.Max(1, effective.Count) * LineHeight;
                float footerHeight = string.IsNullOrEmpty(footer) ? 0f : LineHeight + 2f;
                float height = 34f + bodyHeight + footerHeight + 8f;

                float top = Mathf.Max(8f, GRoot.inst.height - 16f - height);

                _bg.DrawRect(Width, height, 1,
                    new Color(0.36f, 0.76f, 1f, 0.55f),
                    new Color(0.03f, 0.06f, 0.11f, 0.76f));
                _bg.SetXY(14f, top);

                _title.text = title ?? string.Empty;
                _title.SetXY(26f, top + 8f);

                _body.text = Join(effective);
                _body.SetSize(Width - 24f, bodyHeight);
                _body.SetXY(26f, top + 32f);

                _footer.text = footer ?? string.Empty;
                _footer.SetSize(Width - 24f, LineHeight);
                _footer.SetXY(26f, top + 32f + bodyHeight + 2f);
                _footer.visible = !string.IsNullOrEmpty(footer);

                _bg.visible = true;
                _title.visible = true;
                _body.visible = true;
                _visible = true;

                Raise();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        public void Hide()
        {
            try
            {
                _visible = false;
                if (_layer == null) return;
                _layer.visible = false;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>重挂到 GRoot 末尾（窗口被重建时火层可能被挤下去）。</summary>
        private void Raise()
        {
            GRoot root = GRoot.inst;
            if (root == null || _layer == null) return;
            if (_layer.parent != root)
            {
                if (_layer.parent != null) _layer.RemoveFromParent();
                root.AddChild(_layer);
            }
            _layer.SetSize(root.width, root.height);
        }

        private bool Ensure()
        {
            if (_failed) return false;
            if (_layer != null) return true;

            GRoot root = GRoot.inst;
            if (root == null) return false;

            _layer = new GComponent { name = "TeammateCheckPanel", touchable = false, sortingOrder = SortingOrder };

            _bg = new GGraph { touchable = false };
            _title = new GTextField { touchable = false, singleLine = true };
            _body = new GTextField { touchable = false, singleLine = false };
            _footer = new GTextField { touchable = false, singleLine = true };

            _title.textFormat = Format(15, new Color(0.78f, 0.94f, 1f), true);
            _body.textFormat = Format(13, new Color(0.93f, 0.96f, 1f), false);
            _footer.textFormat = Format(12, new Color(0.66f, 0.76f, 0.86f), false);

            _body.autoSize = AutoSizeType.None;
            _footer.autoSize = AutoSizeType.None;

            _layer.AddChild(_bg);
            _layer.AddChild(_title);
            _layer.AddChild(_body);
            _layer.AddChild(_footer);

            _layer.visible = false;
            root.AddChild(_layer);
            _layer.SetSize(root.width, root.height);
            return true;
        }

        private static TextFormat Format(int size, Color color, bool bold)
        {
            TextFormat format = new TextFormat();
            format.size = size;
            format.color = color;
            format.bold = bold;
            format.lineSpacing = 3;
            format.font = UIConfig.defaultFont;
            return format;
        }

        private static string Join(List<string> lines)
        {
            int count = lines == null ? 0 : lines.Count;
            if (count == 0) return string.Empty;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }

        private void Fail(Exception e)
        {
            _failed = true;
            _visible = false;
            try
            {
                if (_warn != null) _warn("浮窗不可用，已停用: " + e.Message);
            }
            catch
            {
                // 日志都写不了就算了，浮窗本来就是可选功能。
            }
        }
    }
}
