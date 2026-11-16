using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

namespace TeammateCheckMod
{
    /// <summary>房间成员列表里的一行。</summary>
    internal struct MemberRow
    {
        public long Id;
        public string Name;
        public bool IsSelf;
    }

    /// <summary>
    /// 两套自绘面板：
    ///   1) 右下角的「疑似组队」小面板：可拖动，点一下展开成员列表；
    ///   2) 成员列表面板：列出本房间所有真人，点某个名字就在局内打开他的名片。
    ///
    /// 全部用 FairyGUI 在代码里搭，照 ForkDirectionMod 的做法：先往 GRoot 上挂一个
    /// 全屏、自身不吃点击、sortingOrder 很大的 GComponent 当图层，再往图层里加两个
    /// 可交互的子面板。孩子们自己 touchable=true，所以即使图层不接收点击也能点。
    ///
    /// 为什么必须给图层设 sortingOrder：FairyGUI 的 GComponent.AddChildAt 里，
    /// sortingOrder != 0 的子对象会被插入到所有 0 序号子对象之后（即渲染在上层），
    /// 而之后新加的窗口只会插到 0 号区域里。只靠 SetChildIndex 压不住游戏自己的
    /// Window（关掉/重开窗口就会重新盖住我们），所以用 sortingOrder 才是稳定的。
    ///
    /// 为什么面板要 opaque：FairyGUI 的 Container.HitTest_Container 里只有
    /// `opaque && _contentRect.Contains(vector)` 才会把命中算回组件自身，不 opaque
    /// 的组件只有文字那几行能点/能拖。
    ///
    /// 任何一步失败都只是「没有浮窗」，不影响扫描本身。
    /// </summary>
    internal sealed class ModPanels
    {
        private const float PairWidth = 300f;
        private const float LineHeight = 20f;
        private const float Margin = 16f;
        private const float MemberWidth = 220f;
        private const float RowHeight = 26f;

        /// <summary>远大于游戏窗口默认的 0，也大于常见的弹窗图层。</summary>
        private const int SortingOrder = 30000;

        private static readonly Color BorderColor = new Color(0.36f, 0.76f, 1f, 0.55f);
        private static readonly Color FillColor = new Color(0.03f, 0.06f, 0.11f, 0.86f);
        private static readonly Color TitleColor = new Color(0.78f, 0.94f, 1f);
        private static readonly Color BodyColor = new Color(0.93f, 0.96f, 1f);
        private static readonly Color MutedColor = new Color(0.62f, 0.68f, 0.78f);
        private static readonly Color RowColor = new Color(0.12f, 0.18f, 0.28f, 0.92f);
        private static readonly Color SelfColor = new Color(0.55f, 0.85f, 0.6f);

        private readonly Action<string> _warn;
        private readonly Action<string> _info;
        private readonly Action<long> _onPick;

        private GComponent _layer;
        private GComponent _pairPanel;
        private GGraph _pairBg;
        private GTextField _pairTitle;
        private GTextField _pairBody;
        private float _pairHeight = 62f;

        private GComponent _memberPanel;
        private GGraph _memberBg;
        private GTextField _memberTitle;
        private readonly List<GComponent> _memberRows = new List<GComponent>();
        private float _memberHeight;

        private bool _failed;
        private bool _wanted = true;       // 快捷键总开关
        private bool _suppressed;          // 名片窗口开着时临时藏起来
        private bool _membersOpen;
        private bool _pairsVisible;
        private bool _pairLogged;
        private string _pairSignature;
        private string _memberSignature;

        public ModPanels(Action<string> warn, Action<string> info, Action<long> onPick)
        {
            _warn = warn;
            _info = info;
            _onPick = onPick;
        }

        public bool Wanted { get { return _wanted; } }
        public bool MembersOpen { get { return _membersOpen; } }

        /// <summary>快捷键总开关：一次隐藏/显示两套面板。</summary>
        public void SetWanted(bool value)
        {
            _wanted = value;
            Apply();
        }

        /// <summary>名片窗口打开时让出屏幕，但不改变总开关状态。</summary>
        public void SetSuppressed(bool value)
        {
            if (_suppressed == value) return;
            _suppressed = value;
            Apply();
        }

        /// <summary>右下角面板的内容；lines 为空时显示一句占位，保证还能点开成员列表。</summary>
        public void ShowPairs(string title, List<string> lines)
        {
            try
            {
                if (!Ensure()) return;

                if (lines == null || lines.Count == 0)
                {
                    lines = new List<string>();
                    lines.Add("暂未发现疑似组队");
                }

                string signature = (title ?? string.Empty) + "|" + string.Join("|", lines.ToArray());
                LayoutPair(lines.Count);

                if (signature != _pairSignature)
                {
                    _pairSignature = signature;

                    _pairTitle.text = title ?? string.Empty;
                    _pairBody.text = Join(lines);
                    _pairTitle.SetXY(12f, 7f);
                    _pairBody.SetSize(PairWidth - 20f, Mathf.Max(1, lines.Count) * LineHeight);
                    _pairBody.SetXY(12f, 28f);
                }

                _pairPanel.visible = true;
                _pairsVisible = true;
                Apply();

                if (!_pairLogged)
                {
                    _pairLogged = true;
                    if (_info != null) _info("浮窗已显示（右下角，可拖动，点击展开成员，sortingOrder=" + SortingOrder + "）。");
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        public void HidePairs()
        {
            try
            {
                _pairsVisible = false;
                HideMembers();
                if (_pairPanel == null) return;
                _pairPanel.visible = false;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>展开/更新成员列表。</summary>
        public void ShowMembers(List<MemberRow> rows)
        {
            try
            {
                if (!Ensure()) return;

                string signature = RowsSignature(rows);
                LayoutMember(rows == null ? 0 : rows.Count);

                if (signature != _memberSignature)
                {
                    _memberSignature = signature;
                    RebuildRows(rows);
                }

                _memberTitle.text = "房间成员（点名字看名片）";
                _memberTitle.SetXY(12f, 7f);

                _memberPanel.visible = true;
                _membersOpen = true;
                Apply();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        public void HideMembers()
        {
            try
            {
                _membersOpen = false;
                if (_memberPanel == null) return;
                _memberPanel.visible = false;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        public void HideAll()
        {
            HideMembers();
            HidePairs();
        }

        // -------------------------------------------------------------- 内部

        private void Apply()
        {
            if (_layer == null) return;
            bool show = _wanted && !_suppressed;
            _layer.visible = show;
            if (!show) return;

            if (_pairPanel != null) _pairPanel.visible = _pairsVisible;
            if (_memberPanel != null) _memberPanel.visible = _membersOpen;
            Raise();
            Clamp(_pairPanel);
            Clamp(_memberPanel);
        }

        private void ApplyPairClick()
        {
            if (_membersOpen)
            {
                HideMembers();
                if (_info != null) _info("成员列表已收起。");
                return;
            }

            List<MemberRow> rows = _memberProvider != null ? _memberProvider() : null;
            if (rows == null || rows.Count == 0) return;
            ShowMembers(rows);
        }

        private Func<List<MemberRow>> _memberProvider;

        /// <summary>点开名单时向外部要一份最新的成员。</summary>
        public void SetMemberProvider(Func<List<MemberRow>> provider)
        {
            _memberProvider = provider;
        }

        private bool Ensure()
        {
            if (_failed) return false;
            if (_layer != null) return true;

            GRoot root = GRoot.inst;
            if (root == null) return false;

            _layer = new GComponent { name = "TeammateCheckPanel", touchable = false, sortingOrder = SortingOrder };
            _layer.SetSize(root.width, root.height);
            root.AddChild(_layer);

            BuildPairPanel();
            BuildMemberPanel();

            _layer.visible = false;
            return true;
        }

        private void BuildPairPanel()
        {
            _pairPanel = new GComponent
            {
                name = "TeammateCheckPairs",
                opaque = true,
                touchable = true,
                draggable = true
            };

            _pairBg = new GGraph { touchable = false };
            _pairTitle = new GTextField { touchable = false, singleLine = true };
            _pairBody = new GTextField { touchable = false, singleLine = false };

            _pairTitle.textFormat = Format(14, TitleColor, true);
            _pairBody.textFormat = Format(13, BodyColor, false);
            _pairBody.autoSize = AutoSizeType.None;

            _pairPanel.AddChild(_pairBg);
            _pairPanel.AddChild(_pairTitle);
            _pairPanel.AddChild(_pairBody);

            LayoutPair(1);

            // 拖动结束后把面板收回屏幕内，免得拖丢了。
            _pairPanel.onDragEnd.Add(delegate (EventContext context) { Clamp(_pairPanel); });
            _pairPanel.onClick.Add(delegate (EventContext context) { ApplyPairClick(); });

            _layer.AddChild(_pairPanel);
            _pairPanel.visible = false;
        }

        private void BuildMemberPanel()
        {
            _memberPanel = new GComponent
            {
                name = "TeammateCheckMembers",
                opaque = true,
                touchable = true,
                draggable = true
            };

            _memberBg = new GGraph { touchable = false };
            _memberTitle = new GTextField { touchable = false, singleLine = true };
            _memberTitle.textFormat = Format(13, TitleColor, true);

            _memberPanel.AddChild(_memberBg);
            _memberPanel.AddChild(_memberTitle);

            LayoutMember(0);

            _memberPanel.onDragEnd.Add(delegate (EventContext context) { Clamp(_memberPanel); });

            _layer.AddChild(_memberPanel);
            _memberPanel.visible = false;
        }

        /// <summary>右下角：贴着屏幕右边和下边各留 Margin。</summary>
        private void LayoutPair(int lineCount)
        {
            if (_pairPanel == null) return;

            float height = 32f + Mathf.Max(1, lineCount) * LineHeight + 10f;
            if (!Mathf.Approximately(height, _pairHeight))
            {
                _pairHeight = height;
                _pairPanel.SetSize(PairWidth, height);
                _pairBg.DrawRect(PairWidth, height, 1, BorderColor, FillColor);
            }

            float left = Mathf.Max(Margin, GRoot.inst.width - Margin - PairWidth);
            float top = Mathf.Max(Margin, GRoot.inst.height - Margin - height);
            if (_pairPanel.x <= 0f && _pairPanel.y <= 0f)
            {
                _pairPanel.SetXY(left, top);
            }
            Clamp(_pairPanel);
        }

        /// <summary>成员列表压在右下角面板上方；放不下就往下贴。</summary>
        private void LayoutMember(int rowCount)
        {
            if (_memberPanel == null) return;

            float height = 32f + Mathf.Max(1, rowCount) * RowHeight + 8f;
            if (!Mathf.Approximately(height, _memberHeight))
            {
                _memberHeight = height;
                _memberPanel.SetSize(MemberWidth, height);
                _memberBg.DrawRect(MemberWidth, height, 1, BorderColor, FillColor);
            }

            float left = Mathf.Max(Margin, GRoot.inst.width - Margin - MemberWidth);
            float top = Margin;
            if (_pairPanel != null && _pairPanel.visible)
            {
                top = _pairPanel.y - Margin - height;
                if (top < Margin) top = Mathf.Min(GRoot.inst.height - Margin - height, _pairPanel.y + _pairHeight + Margin);
                if (top < Margin) top = Margin;
            }
            if (_memberPanel.x <= 0f && _memberPanel.y <= 0f)
            {
                _memberPanel.SetXY(left, top);
            }
            Clamp(_memberPanel);
        }

        private void RebuildRows(List<MemberRow> rows)
        {
            for (int i = 0; i < _memberRows.Count; i++)
            {
                _memberPanel.RemoveChild(_memberRows[i], true);
            }
            _memberRows.Clear();

            if (rows == null) return;

            float y = 30f;
            for (int i = 0; i < rows.Count; i++)
            {
                MemberRow row = rows[i];
                GComponent item = new GComponent
                {
                    name = "member_" + row.Id,
                    opaque = true,
                    touchable = true
                };
                item.SetSize(MemberWidth - 16f, RowHeight - 4f);
                item.SetXY(8f, y);

                GGraph bg = new GGraph { touchable = false };
                bg.DrawRoundRect(MemberWidth - 16f, RowHeight - 4f, RowColor, new float[] { 6f, 6f, 6f, 6f });
                item.AddChild(bg);

                GTextField label = new GTextField { touchable = false, singleLine = true };
                label.textFormat = Format(13, row.IsSelf ? SelfColor : BodyColor, false);
                label.text = row.Name + (row.IsSelf ? "（我）" : string.Empty);
                label.autoSize = AutoSizeType.None;
                label.SetSize(MemberWidth - 32f, RowHeight - 4f);
                label.SetXY(8f, 0f);
                label.verticalAlign = VertAlignType.Middle;
                item.AddChild(label);

                long id = row.Id;
                item.onClick.Add(delegate (EventContext context) { Pick(id); });

                _memberPanel.AddChild(item);
                _memberRows.Add(item);
                y += RowHeight;
            }
        }

        private void Pick(long id)
        {
            try
            {
                if (_onPick != null) _onPick(id);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>把组件拉回屏幕内（拖动后可能拖出边界）。</summary>
        private static void Clamp(GComponent box)
        {
            if (box == null) return;
            GRoot root = GRoot.inst;
            if (root == null) return;

            float maxX = Mathf.Max(0f, root.width - box.width);
            float maxY = Mathf.Max(0f, root.height - box.height);
            float x = Mathf.Clamp(box.x, 0f, maxX);
            float y = Mathf.Clamp(box.y, 0f, maxY);
            if (!Mathf.Approximately(x, box.x) || !Mathf.Approximately(y, box.y))
            {
                box.SetXY(x, y);
            }
        }

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

        private static string RowsSignature(List<MemberRow> rows)
        {
            if (rows == null) return string.Empty;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(rows[i].Id).Append(':').Append(rows[i].Name);
            }
            return sb.ToString();
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
            _membersOpen = false;
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
