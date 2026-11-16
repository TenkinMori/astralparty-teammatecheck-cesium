using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

namespace TeammateCheckMod
{
    /// <summary>
    /// 局内左下角的小浮窗：列出「疑似组队」的两两组合。
    ///
    /// 全部用 FairyGUI 在代码里搭（面板资源在包里不好挂），照 HomeBackgroundMod 的做法
    /// 直接往 GRoot 上加几个不吃点击的 GObject：半透明底板 + 标题 + 正文 + 脚注。
    /// 任何一步失败都只是「没有浮窗」，不影响扫描本身。
    /// </summary>
    internal sealed class PairPanel
    {
        private const float Width = 340f;
        private const float LineHeight = 20f;

        private readonly Action<string> _warn;

        private GGraph _bg;
        private GTextField _title;
        private GTextField _body;
        private GTextField _footer;

        private bool _attached;
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

                if (!string.IsNullOrEmpty(footer))
                {
                    _footer.text = footer;
                    _footer.SetSize(Width - 24f, LineHeight);
                    _footer.SetXY(26f, top + 32f + bodyHeight + 2f);
                }

                _footer.visible = !string.IsNullOrEmpty(footer);
                SetVisible(true);
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
                if (!_attached) return;
                _bg.visible = false;
                _title.visible = false;
                _body.visible = false;
                _footer.visible = false;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void SetVisible(bool visible)
        {
            _bg.visible = visible;
            _title.visible = visible;
            _body.visible = visible;
            _visible = visible;
        }

        /// <summary>压到最上层；只在内容变化/重新出现时调，避免一直盖住对话框。</summary>
        private void Raise()
        {
            GRoot root = GRoot.inst;
            if (root == null) return;

            GObject[] objects = new GObject[] { _bg, _title, _body, _footer };
            int last = root.numChildren - 1;
            if (last < 0) return;

            int start = Mathf.Max(0, last - objects.Length + 1);
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                root.SetChildIndex(objects[i], start + i);
            }
        }

        private bool Ensure()
        {
            if (_failed) return false;
            if (_attached) return true;

            GRoot root = GRoot.inst;
            if (root == null) return false;

            _bg = new GGraph();
            _title = new GTextField();
            _body = new GTextField();
            _footer = new GTextField();

            root.AddChild(_bg);
            root.AddChild(_title);
            root.AddChild(_body);
            root.AddChild(_footer);

            _bg.touchable = false;
            _title.touchable = false;
            _body.touchable = false;
            _footer.touchable = false;

            _title.textFormat = Format(15, new Color(0.78f, 0.94f, 1f), true);
            _body.textFormat = Format(13, new Color(0.93f, 0.96f, 1f), false);
            _footer.textFormat = Format(12, new Color(0.66f, 0.76f, 0.86f), false);

            _title.singleLine = true;
            _body.singleLine = false;
            _footer.singleLine = true;

            _body.autoSize = AutoSizeType.None;
            _footer.autoSize = AutoSizeType.None;

            _attached = true;

            _bg.visible = false;
            _title.visible = false;
            _body.visible = false;
            _footer.visible = false;
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
