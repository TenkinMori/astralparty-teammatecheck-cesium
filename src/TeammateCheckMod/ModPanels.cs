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
    ///   1) 右下角的「疑似组队」面板：可拖动，点一下展开成员列表；
    ///   2) 成员列表面板：列出本房间所有真人，点某个名字就在局内打开他的名片。
    ///
    /// 两个面板都直接挂在 GRoot 下（照 TownTrainer 的做法：GComponent + opaque + draggable），
    /// 各自带一个很大的 sortingOrder，所以不会被游戏窗口盖住。
    ///
    /// 三条硬性要求，缺一条就「看得见但拖不动」或「干脆看不见」：
    ///   1) 面板自身 touchable = true 且 opaque = true —— FairyGUI 的
    ///      Container.HitTest_Container 里只有 `opaque &amp;&amp; _contentRect.Contains(vector)`
    ///      才会把命中算回组件自身，否则只有文字那几行能点；
    ///   2) 不能再套一层 touchable = false 的全屏图层 —— DisplayObject.InternalHitTest
    ///      对不可触摸的对象直接返回 null，子对象连试都不试；
    ///   3) 背景 GGraph 必须在面板尺寸确定之后画一次（DrawRoundRect 内部会 SetSize），
    ///      尺寸没变就跳过绘制的话，背景永远是 0×0 的透明块。
    ///
    /// 任何一步失败都只是「没有面板」，不影响扫描本身。
    /// </summary>
    internal sealed class ModPanels
    {
        // 右下角主面板
        private const float PairWidth = 440f;
        private const float PairPadX = 16f;
        private const float PairPadY = 12f;
        private const float PairTitleHeight = 30f;
        private const float PairLineHeight = 26f;
        private const int PairTitleSize = 21;
        private const int PairBodySize = 18;

        // 成员列表
        private const float MemberWidth = 320f;
        private const float MemberPadX = 12f;
        private const float MemberTitleHeight = 34f;
        private const float MemberRowHeight = 36f;
        private const float MemberRowGap = 4f;
        private const int MemberTitleSize = 18;
        private const int MemberBodySize = 17;

        private const float Margin = 18f;

        /// <summary>远大于游戏窗口默认的 0，也大于常见的弹窗图层。</summary>
        private const int SortingOrder = 30000;

        private static readonly Color FillColor = new Color(0.04f, 0.07f, 0.12f, 0.92f);
        private static readonly Color MemberFillColor = new Color(0.05f, 0.09f, 0.15f, 0.94f);
        private static readonly Color TitleColor = new Color(0.72f, 0.92f, 1f);
        private static readonly Color BodyColor = new Color(0.95f, 0.97f, 1f);
        private static readonly Color RowColor = new Color(0.14f, 0.21f, 0.32f, 0.95f);
        private static readonly Color SelfColor = new Color(0.62f, 0.92f, 0.68f);
        private static readonly float[] Corners = new float[] { 10f, 10f, 10f, 10f };

        private readonly Action<string> _warn;
        private readonly Action<string> _info;
        private readonly Action<long> _onPick;

        private GComponent _pairPanel;
        private GGraph _pairBg;
        private GTextField _pairTitle;
        private GTextField _pairBody;
        private float _pairHeight = -1f;
        private bool _pairPlaced;

        private GComponent _memberPanel;
        private GGraph _memberBg;
        private GTextField _memberTitle;
        private readonly List<GComponent> _memberRows = new List<GComponent>();
        private float _memberHeight = -1f;
        private bool _memberPlaced;

        private Func<List<MemberRow>> _memberProvider;

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

        /// <summary>点开名单时向外部要一份最新的成员。</summary>
        public void SetMemberProvider(Func<List<MemberRow>> provider)
        {
            _memberProvider = provider;
        }

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
                    _pairTitle.SetXY(PairPadX, PairPadY);
                    _pairBody.text = Join(lines);
                    _pairBody.SetXY(PairPadX, PairPadY + PairTitleHeight);
                }

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
                Apply();
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
                if (signature != _memberSignature)
                {
                    _memberSignature = signature;
                    LayoutMember(rows == null ? 0 : rows.Count);
                    _memberTitle.text = "房间成员（点名字看名片）";
                    _memberTitle.SetXY(MemberPadX, 8f);
                    RebuildRows(rows);
                }

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
                Apply();
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
            if (_pairPanel == null) return;

            bool show = _wanted && !_suppressed;

            if (show) Raise(_pairPanel);
            _pairPanel.visible = show && _pairsVisible;

            if (_memberPanel != null)
            {
                if (show) Raise(_memberPanel);
                _memberPanel.visible = show && _membersOpen;
            }

            if (!show) return;
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

        private bool Ensure()
        {
            if (_failed) return false;
            if (_pairPanel != null) return true;

            GRoot root = GRoot.inst;
            if (root == null) return false;

            BuildPairPanel(root);
            BuildMemberPanel(root);
            return true;
        }

        private void BuildPairPanel(GRoot root)
        {
            _pairPanel = new GComponent
            {
                name = "TeammateCheckPairs",
                opaque = true,
                touchable = true,
                draggable = true,
                sortingOrder = SortingOrder
            };

            _pairBg = new GGraph { touchable = false };
            _pairTitle = new GTextField { touchable = false, singleLine = true };
            _pairBody = new GTextField { touchable = false, singleLine = false };

            _pairTitle.textFormat = Format(PairTitleSize, TitleColor, true);
            _pairBody.textFormat = Format(PairBodySize, BodyColor, false);
            _pairBody.autoSize = AutoSizeType.None;

            _pairPanel.AddChild(_pairBg);
            _pairPanel.AddChild(_pairTitle);
            _pairPanel.AddChild(_pairBody);

            LayoutPair(1);

            // 拖动结束后把面板收回屏幕内，免得拖丢了。
            _pairPanel.onDragEnd.Add(delegate (EventContext context) { Clamp(_pairPanel); });
            _pairPanel.onClick.Add(delegate (EventContext context) { ApplyPairClick(); });

            root.AddChild(_pairPanel);
            _pairPanel.visible = false;
        }

        private void BuildMemberPanel(GRoot root)
        {
            _memberPanel = new GComponent
            {
                name = "TeammateCheckMembers",
                opaque = true,
                touchable = true,
                draggable = true,
                sortingOrder = SortingOrder
            };

            _memberBg = new GGraph { touchable = false };
            _memberTitle = new GTextField { touchable = false, singleLine = true };
            _memberTitle.textFormat = Format(MemberTitleSize, TitleColor, true);

            _memberPanel.AddChild(_memberBg);
            _memberPanel.AddChild(_memberTitle);

            LayoutMember(0);

            _memberPanel.onDragEnd.Add(delegate (EventContext context) { Clamp(_memberPanel); });

            root.AddChild(_memberPanel);
            _memberPanel.visible = false;
        }

        /// <summary>右下角：贴着屏幕右边和下边各留 Margin。</summary>
        private void LayoutPair(int lineCount)
        {
            if (_pairPanel == null) return;

            int count = Mathf.Max(1, lineCount);
            float height = PairPadY * 2f + PairTitleHeight + count * PairLineHeight;
            if (!Mathf.Approximately(height, _pairHeight))
            {
                _pairHeight = height;
                _pairPanel.SetSize(PairWidth, height);
                _pairBg.DrawRoundRect(PairWidth, height, FillColor, Corners);
            }

            _pairTitle.SetSize(PairWidth - PairPadX * 2f, PairTitleHeight);
            _pairBody.SetSize(PairWidth - PairPadX * 2f, count * PairLineHeight);

            if (!_pairPlaced)
            {
                _pairPlaced = true;
                _pairPanel.SetXY(
                    Mathf.Max(Margin, GRoot.inst.width - Margin - PairWidth),
                    Mathf.Max(Margin, GRoot.inst.height - Margin - height));
            }
        }

        /// <summary>成员列表默认压在右下角面板上方；放不下就贴屏幕顶部。</summary>
        private void LayoutMember(int rowCount)
        {
            if (_memberPanel == null) return;

            int count = Mathf.Max(1, rowCount);
            float height = MemberTitleHeight + count * MemberRowHeight + 10f;
            if (!Mathf.Approximately(height, _memberHeight))
            {
                _memberHeight = height;
                _memberPanel.SetSize(MemberWidth, height);
                _memberBg.DrawRoundRect(MemberWidth, height, MemberFillColor, Corners);
            }

            _memberTitle.SetSize(MemberWidth - MemberPadX * 2f, MemberTitleHeight - 8f);

            if (!_memberPlaced)
            {
                _memberPlaced = true;
                float left = Mathf.Max(Margin, GRoot.inst.width - Margin - MemberWidth);
                float top = Margin;
                if (_pairPanel != null)
                {
                    float above = _pairPanel.y - Margin - height;
                    top = above >= Margin ? above : Mathf.Max(Margin, _pairPanel.y + _pairHeight + Margin);
                }
                _memberPanel.SetXY(left, top);
            }
        }

        private void RebuildRows(List<MemberRow> rows)
        {
            for (int i = 0; i < _memberRows.Count; i++)
            {
                _memberPanel.RemoveChild(_memberRows[i], true);
            }
            _memberRows.Clear();

            if (rows == null) return;

            float rowWidth = MemberWidth - MemberPadX * 2f;
            float y = MemberTitleHeight;
            for (int i = 0; i < rows.Count; i++)
            {
                MemberRow row = rows[i];
                GComponent item = new GComponent
                {
                    name = "member_" + row.Id,
                    opaque = true,
                    touchable = true
                };
                item.SetSize(rowWidth, MemberRowHeight - MemberRowGap);
                item.SetXY(MemberPadX, y);

                GGraph bg = new GGraph { touchable = false };
                bg.DrawRoundRect(rowWidth, MemberRowHeight - MemberRowGap, RowColor, Corners);
                item.AddChild(bg);

                GTextField label = new GTextField { touchable = false, singleLine = true };
                label.textFormat = Format(MemberBodySize, row.IsSelf ? SelfColor : BodyColor, false);
                label.text = row.Name + (row.IsSelf ? "（我）" : string.Empty);
                label.autoSize = AutoSizeType.None;
                label.SetSize(rowWidth - 16f, MemberRowHeight - MemberRowGap);
                label.SetXY(10f, 0f);
                label.verticalAlign = VertAlignType.Middle;
                item.AddChild(label);

                long id = row.Id;
                item.onClick.Add(delegate (EventContext context) { Pick(id); });

                _memberPanel.AddChild(item);
                _memberRows.Add(item);
                y += MemberRowHeight;
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

        /// <summary>重新挂到 GRoot 末尾，防止被后来添加的图层压住。</summary>
        private static void Raise(GComponent box)
        {
            GRoot root = GRoot.inst;
            if (root == null || box == null) return;
            if (box.parent != root)
            {
                if (box.parent != null) box.RemoveFromParent();
                root.AddChild(box);
            }
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
            format.lineSpacing = 4;
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
