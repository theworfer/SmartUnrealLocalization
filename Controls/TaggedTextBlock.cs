using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Renders Unreal-style inline tokens as compact chips while keeping all
    /// text/chip fragments vertically centered in the same row.
    ///
    /// Uses plain <see cref="TextBlock"/> (NOT <see cref="SelectableTextBlock"/>)
    /// for two reasons that together caused real bugs in earlier iterations:
    ///
    ///   1. SelectableTextBlock is focusable; clicking it pulls keyboard focus
    ///      into the cell which lit up the DataGrid's :focus-within pseudo-class
    ///      and made the row flicker on the first click.
    ///
    ///   2. SelectableTextBlock has its own DoubleTapped handling for word
    ///      selection, which swallowed our DoubleTapped to edit handler.
    ///
    /// Part of the text is still selectable with the mouse — press, drag, Ctrl+C —
    /// through <see cref="CellTextSelection"/>, which does not take focus and starts
    /// only after a drag, so a click still focuses the row and a double-click still
    /// opens the editor. Every segment records the characters it shows, which is
    /// how a pointer position becomes a character index across text and chips.
    ///
    /// <para>
    /// Etap 4 additions: <see cref="ProblemSpans"/> drives a wavy underline
    /// under text segments whose character range intersects any Problem with
    /// a non-null Range; the underline colour comes from the highest-severity
    /// intersecting Problem. <see cref="FindHits"/> drives a translucent
    /// background highlight on intersecting segments — the
    /// <see cref="CurrentFindHitIndex"/>'th hit gets a stronger colour so the
    /// user can spot the "Next match" target inside a sea of matches.
    /// Overlays only render in NO-WRAP mode (the default scroll-friendly mode).
    /// Wrap mode keeps the prior inline-only rendering — adding overlays there
    /// requires character-precision text metrics on a TextBlock that wraps,
    /// which is meaningful future work but out of scope for Etap 4.
    /// </para>
    /// </summary>
    public class TaggedTextBlock : StackPanel, ISelectableCellText
    {
        /// <summary>What a double-click selects. None (the default) leaves the
        /// double-click to the cell — the Target cell uses it to open its editor.</summary>
        public static readonly StyledProperty<CellDoubleClick> DoubleClickSelectsProperty =
            AvaloniaProperty.Register<TaggedTextBlock, CellDoubleClick>(nameof(DoubleClickSelects));

        public CellDoubleClick DoubleClickSelects
        {
            get => GetValue(DoubleClickSelectsProperty);
            set => SetValue(DoubleClickSelectsProperty, value);
        }

        public static readonly StyledProperty<string?> SourceTextProperty =
            AvaloniaProperty.Register<TaggedTextBlock, string?>(nameof(SourceText));

        /// <summary>
        /// When true, inner TextBlocks use <c>TextWrapping.Wrap</c> and the row
        /// is allowed to grow vertically. When false (default) text stays on a
        /// single line of fixed height (19 px) so DataGrid virtualization works
        /// off a single uniform row height.
        /// </summary>
        public static readonly StyledProperty<bool> WrapProperty =
            AvaloniaProperty.Register<TaggedTextBlock, bool>(nameof(Wrap));

        /// <summary>
        /// Validation problems whose <see cref="Problem.Range"/> falls inside
        /// the rendered text. Each intersecting segment gets a wavy underline
        /// coloured by the highest-severity match. Problems with null Range
        /// contribute the status-icon column but no overlay here.
        /// </summary>
        public static readonly StyledProperty<IReadOnlyList<Problem>?> ProblemSpansProperty =
            AvaloniaProperty.Register<TaggedTextBlock, IReadOnlyList<Problem>?>(nameof(ProblemSpans));

        /// <summary>
        /// Character ranges of search matches inside the rendered text.
        /// Intersecting segments get a translucent background highlight.
        /// </summary>
        public static readonly StyledProperty<IReadOnlyList<TextRange>?> FindHitsProperty =
            AvaloniaProperty.Register<TaggedTextBlock, IReadOnlyList<TextRange>?>(nameof(FindHits));

        /// <summary>
        /// Index inside <see cref="FindHits"/> of the "current" match (the one
        /// Next/Prev would step on next). Drawn with a stronger highlight.
        /// -1 means "no current" (e.g. before the user pressed Next once).
        /// </summary>
        public static readonly StyledProperty<int> CurrentFindHitIndexProperty =
            AvaloniaProperty.Register<TaggedTextBlock, int>(nameof(CurrentFindHitIndex), -1);

        /// <summary>
        /// True when the rendered content overflows horizontally in no-wrap mode
        /// (i.e. the inner horizontal StackPanel's DesiredSize.Width exceeds its
        /// arranged Bounds.Width). Computed on LayoutUpdated; bound back to
        /// <c>TranslationRow.IsClipped</c> with OneWayToSource from the cell
        /// template so Etap 5's chevron-expand button can decide whether to
        /// surface itself. Always false in wrap mode (where rows grow to fit).
        /// </summary>
        public static readonly StyledProperty<bool> IsClippedProperty =
            AvaloniaProperty.Register<TaggedTextBlock, bool>(
                nameof(IsClipped),
                defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

        private static readonly Regex TagRegex = new(@"<[^>]+>|\{[^}]+\}", RegexOptions.Compiled);

        public TaggedTextBlock()
        {
            Orientation = Orientation.Vertical;
            Spacing = 0;
            // The whole cell area takes the press, not only the glyphs: a drag that
            // starts just past the end of the text still selects.
            Background = Brushes.Transparent;
            CellTextSelection.Attach(this);
            // ClipToBounds is set inside Rebuild() based on Wrap.
            LayoutUpdated += OnLayoutUpdated;
        }

        // The static ProblemSpans/FindHits property-change handlers subscribe to the
        // bound collection's CollectionChanged. Those collections live on the ROW model,
        // which long outlives a virtualized cell control — so when the DataGrid recycles
        // or tears down a cell, the model keeps a live reference to the dead control via
        // that subscription. Over thousands of rows + scrolling + re-validation this leaks
        // controls (and their render data) and was a major contributor to the runaway RAM
        // (AA0035 "event handler leaks"). Unhook on detach; re-hook + repaint on attach.
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (ProblemSpans is INotifyCollectionChanged p) p.CollectionChanged -= OnProblemSpansCollectionChanged;
            if (FindHits is INotifyCollectionChanged f) f.CollectionChanged -= OnFindHitsCollectionChanged;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Resubscribe(ProblemSpans as INotifyCollectionChanged, OnProblemSpansCollectionChanged);
            Resubscribe(FindHits as INotifyCollectionChanged, OnFindHitsCollectionChanged);
            Rebuild();
        }

        private static void Resubscribe(INotifyCollectionChanged? cc, NotifyCollectionChangedEventHandler handler)
        {
            if (cc is null) return;
            cc.CollectionChanged -= handler; // idempotent — never double-subscribe
            cc.CollectionChanged += handler;
        }

        /// <summary>
        /// Recompute IsClipped after each layout pass. In no-wrap mode the
        /// inner horizontal StackPanel reports its full desired width
        /// (sum of segment widths); if that exceeds its arranged bounds the
        /// text is being clipped horizontally and we expose IsClipped=true so
        /// the cell-actions StackPanel can show its chevron-expand button.
        /// Wrap mode never reports clipped because rows are allowed to grow.
        /// </summary>
        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            bool clipped = false;
            if (!Wrap && Children.Count > 0 && Children[0] is Layoutable inner)
            {
                // 0.5 px tolerance avoids spurious flips from rounding noise.
                clipped = inner.DesiredSize.Width > inner.Bounds.Width + 0.5;
            }
            if (IsClipped != clipped) IsClipped = clipped;
        }

        public string? SourceText
        {
            get => GetValue(SourceTextProperty);
            set => SetValue(SourceTextProperty, value);
        }

        public bool Wrap
        {
            get => GetValue(WrapProperty);
            set => SetValue(WrapProperty, value);
        }

        public IReadOnlyList<Problem>? ProblemSpans
        {
            get => GetValue(ProblemSpansProperty);
            set => SetValue(ProblemSpansProperty, value);
        }

        public IReadOnlyList<TextRange>? FindHits
        {
            get => GetValue(FindHitsProperty);
            set => SetValue(FindHitsProperty, value);
        }

        public int CurrentFindHitIndex
        {
            get => GetValue(CurrentFindHitIndexProperty);
            set => SetValue(CurrentFindHitIndexProperty, value);
        }

        public bool IsClipped
        {
            get => GetValue(IsClippedProperty);
            set => SetValue(IsClippedProperty, value);
        }

        static TaggedTextBlock()
        {
            SourceTextProperty.Changed.AddClassHandler<TaggedTextBlock>((b, _) => { CellTextSelection.TextChanged(b); b.Rebuild(); });
            WrapProperty.Changed.AddClassHandler<TaggedTextBlock>((b, _) => b.Rebuild());

            // ProblemSpans / FindHits typically arrive as ObservableCollection
            // bindings — Apply Fix mutates the items inside the *same*
            // collection instance, so the StyledProperty.Changed event never
            // re-fires (reference unchanged). We have to subscribe to the
            // collection's CollectionChanged to catch in-place mutations and
            // rebuild the overlays. Without this hookup the wavy underline
            // stays visible after the fix lands (the underlying validator
            // already removed the Problem, the overlay just never repaints).
            ProblemSpansProperty.Changed.AddClassHandler<TaggedTextBlock>((b, e) =>
            {
                b.SwapCollectionSubscription(e.OldValue as INotifyCollectionChanged,
                                             e.NewValue as INotifyCollectionChanged,
                                             b.OnProblemSpansCollectionChanged);
                b.Rebuild();
            });
            FindHitsProperty.Changed.AddClassHandler<TaggedTextBlock>((b, e) =>
            {
                b.SwapCollectionSubscription(e.OldValue as INotifyCollectionChanged,
                                             e.NewValue as INotifyCollectionChanged,
                                             b.OnFindHitsCollectionChanged);
                b.Rebuild();
            });
            CurrentFindHitIndexProperty.Changed.AddClassHandler<TaggedTextBlock>((b, _) => b.Rebuild());
        }

        private void SwapCollectionSubscription(
            INotifyCollectionChanged? oldCc,
            INotifyCollectionChanged? newCc,
            NotifyCollectionChangedEventHandler handler)
        {
            if (oldCc is not null) oldCc.CollectionChanged -= handler;
            if (newCc is not null) newCc.CollectionChanged += handler;
        }

        private void OnProblemSpansCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => Rebuild();

        private void OnFindHitsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => Rebuild();

        // ── Mouse selection (CellTextSelection) ──────────────────────────────

        /// <summary>One drawn piece: its outer box (with overlays), the text or chip
        /// inside, and the characters [Start, End) of <see cref="SelectionText"/> it shows.</summary>
        private readonly record struct Seg(Control Box, Control Inner, int Start, int End);

        private readonly List<Seg> _segs = new();
        private string _selText = "";

        public string SelectionText => _selText;

        private void AddSeg(Panel panel, Control inner, Control box, int start, int end)
        {
            panel.Children.Add(box);
            _segs.Add(new Seg(box, inner, start, end));
        }

        private Rect BoxIn(Control c)
        {
            var origin = c.TranslatePoint(default, this) ?? default;
            return new Rect(origin, c.Bounds.Size);
        }

        int ISelectableCellText.HitIndex(Point p)
        {
            if (_segs.Count == 0) return 0;

            // The visual row under the pointer (wrap mode has several), else the nearest one.
            var boxes = new List<(Seg Seg, Rect Rect)>(_segs.Count);
            foreach (var s in _segs) boxes.Add((s, BoxIn(s.Box)));
            double rowTop = double.NaN, best = double.MaxValue;
            foreach (var (_, r) in boxes)
            {
                double d = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
                if (d < best) { best = d; rowTop = r.Top; }
            }
            var row = boxes.FindAll(b => Math.Abs(b.Rect.Top - rowTop) < 1.5);
            row.Sort((a, b) => a.Rect.Left.CompareTo(b.Rect.Left));

            if (p.X <= row[0].Rect.Left) return row[0].Seg.Start;
            foreach (var (seg, r) in row)
            {
                if (p.X > r.Right) continue;
                if (p.X < r.Left) return seg.Start;                         // in a gap before this piece
                if (seg.Inner is TextBlock tb)
                {
                    var local = p - (tb.TranslatePoint(default, this) ?? default);
                    return Math.Clamp(seg.Start + CellTextSelection.IndexIn(tb, local), seg.Start, seg.End);
                }
                return p.X < r.Left + r.Width / 2 ? seg.Start : seg.End;    // a chip is one unit
            }
            return row[^1].Seg.End;
        }

        (int Start, int End)? ISelectableCellText.DoubleClickRange(Point p)
        {
            switch (DoubleClickSelects)
            {
                case CellDoubleClick.All: return (0, _selText.Length);
                case CellDoubleClick.Word:
                    // On a tag chip the whole tag is the word.
                    foreach (var seg in _segs)
                        if (seg.Inner is not TextBlock && BoxIn(seg.Inner).Contains(p)) return (seg.Start, seg.End);
                    return CellTextSelection.WordAt(_selText, ((ISelectableCellText)this).HitIndex(p));
                default: return null;
            }
        }

        IEnumerable<Rect> ISelectableCellText.RangeRects(int start, int end)
        {
            foreach (var seg in _segs)
            {
                if (seg.End <= start || seg.Start >= end) continue;
                if (seg.Inner is TextBlock tb)
                {
                    var offset = tb.TranslatePoint(default, this) ?? default;
                    foreach (var r in CellTextSelection.RectsIn(tb, Math.Max(start, seg.Start) - seg.Start,
                                                                Math.Min(end, seg.End) - seg.Start))
                        yield return r.Translate(offset);
                }
                else yield return BoxIn(seg.Inner);
            }
        }


        private void Rebuild()
        {
            Children.Clear();
            _segs.Clear();
            ClipToBounds = !Wrap;

            var text = SourceText ?? "";
            // Indices are into the text as drawn: unchanged in one-line mode (a line
            // break is drawn as a space of the same length), CRLF → LF when wrapping.
            _selText = Wrap ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
            if (text.Length == 0) return;

            if (Wrap)
            {
                // Wrap mode: word-level wrapping via a WrapPanel of centered segments
                // (see CreateWrapLine). Every chip and word share VerticalAlignment.Center —
                // the same model as no-wrap — so the chip can no longer grow the line box and
                // drop the surrounding text (the expand/edit misalignment bug).
                //
                // Overlays (find hits + wavy underlines) NOT rendered in wrap mode
                // for Etap 4 — see class XML docs for rationale.
                int lineStart = 0;
                foreach (var line in _selText.Split('\n'))
                {
                    Children.Add(CreateWrapLine(line, lineStart));
                    lineStart += line.Length + 1;
                }
            }
            else
            {
                // No-wrap mode (default): every "\n" collapses to a space, and the
                // entire line is rendered as a horizontal StackPanel with an
                // explicit Height of 19 px. Text segments are TextBlocks (NoWrap),
                // tag segments are Border chips. The StackPanel sidesteps Avalonia's
                // text engine entirely, so the chip's BaselineAlignment cannot
                // grow the line box. Every non-editing row stays at exactly the
                // same visual height (38 px = 19 + 9 padding * 2), which is what
                // the DataGrid's estimate-based virtualization needs to avoid
                // scrollbar drift on large datasets.
                //
                // The earlier attempt used InlineUIContainer here as well, and
                // that is why rows with tags grew to 44 px while rows without
                // tags stayed at 38 px (the height-inconsistency bug the user
                // screenshotted).
                //
                // Etap 4: each segment is wrapped in a 1-cell Grid when it
                // intersects a Problem.Range or FindHits range. The Grid hosts
                // (in z-order bottom→top) a background Border for find-hit
                // highlight, the original text/chip control, and a WavyUnderline
                // pinned to the bottom for problem severity. Plain-text runs are
                // further split at every range boundary (see AddTextRuns) so the
                // highlight / underline covers ONLY the matched characters, not
                // the whole segment between tags.
                //
                // Collapse newlines to spaces WITHOUT changing length: every
                // control char maps 1:1 to a single space so character offsets
                // stay aligned with the raw Target string that FindHits /
                // ProblemSpans were computed against. (Replacing "\r\n" with a
                // single space would shift every later offset by one and
                // mis-place highlights / underlines in multi-line cells.)
                var collapsed = text
                    .Replace('\r', ' ')
                    .Replace('\n', ' ');
                Children.Add(CreateNoWrapLine(collapsed));
            }
        }

        /// <summary>
        /// Single horizontal line with a hard Height=19. Used in no-wrap mode
        /// where every row must end up at exactly the same height. Each segment
        /// is wrapped in a Grid with overlay layers when it intersects a
        /// ProblemSpan or FindHit.
        /// </summary>
        private StackPanel CreateNoWrapLine(string line)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                // 22 (not 19): the chip is a 19px box shifted down by the baseline
                // delta (see MakeChip); at Height=19 that shift got clipped at the
                // bottom. The extra 3px lives in the cell's 9px vertical padding
                // (unused room), so the chip's rounded bottom is no longer cut.
                // Still uniform across every row → DataGrid virtualization holds.
                // ClipToBounds stays for horizontal overflow (drives IsClipped +
                // the chevron); the DataGridCell itself does not clip.
                Height = 22,
                ClipToBounds = true,
            };

            var problems = ProblemSpans;
            var hits = FindHits;
            int currentHit = CurrentFindHitIndex;

            int pos = 0;
            foreach (Match match in TagRegex.Matches(line))
            {
                if (match.Index > pos)
                {
                    var seg = line.Substring(pos, match.Index - pos);
                    AddTextRuns(panel, seg, pos, problems, hits, currentHit);
                }
                var chip = MakeChip(match.Value);
                AddSeg(panel, chip, WrapWithOverlays(chip, match.Index, match.Index + match.Length, problems, hits, currentHit),
                       match.Index, match.Index + match.Length);
                pos = match.Index + match.Length;
            }
            if (pos < line.Length)
            {
                var seg = line.Substring(pos);
                AddTextRuns(panel, seg, pos, problems, hits, currentHit);
            }

            return panel;
        }

        /// <summary>
        /// Split a plain-text run into sub-runs at every <see cref="FindHits"/> /
        /// <see cref="ProblemSpans"/> boundary that falls inside it, so the
        /// highlight background and wavy underline cover ONLY the matched
        /// characters instead of the entire segment between tags. Each emitted
        /// sub-run is uniformly inside-or-outside every range, so
        /// <see cref="WrapWithOverlays"/> draws the correct overlay per piece.
        /// <paramref name="absStart"/> is the absolute character offset of
        /// <paramref name="runText"/> into the (length-preserving) collapsed
        /// line, which equals the raw-Target offset FindHits / ProblemSpans use.
        /// </summary>
        private void AddTextRuns(
            StackPanel panel,
            string runText,
            int absStart,
            IReadOnlyList<Problem>? problems,
            IReadOnlyList<TextRange>? hits,
            int currentHit)
        {
            int absEnd = absStart + runText.Length;

            // Collect interior cut points from every hit / problem range edge.
            SortedSet<int>? cuts = null;
            void AddCut(int at)
            {
                if (at > absStart && at < absEnd) (cuts ??= new SortedSet<int>()).Add(at);
            }
            if (hits is not null)
                foreach (var h in hits)
                {
                    if (h.Length <= 0) continue;
                    AddCut(h.Start);
                    AddCut(h.End);
                }
            if (problems is not null)
                foreach (var p in problems)
                {
                    if (p.Range is not TextRange r || r.Length <= 0) continue;
                    AddCut(r.Start);
                    AddCut(r.End);
                }

            // Fast path: nothing intersects this run → one TextBlock, no Grid.
            if (cuts is null)
            {
                var tb = MakeText(runText);
                AddSeg(panel, tb, WrapWithOverlays(tb, absStart, absEnd, problems, hits, currentHit), absStart, absEnd);
                return;
            }

            int prev = absStart;
            foreach (int cut in cuts)
            {
                AppendRun(panel, runText, absStart, prev, cut, problems, hits, currentHit);
                prev = cut;
            }
            AppendRun(panel, runText, absStart, prev, absEnd, problems, hits, currentHit);
        }

        /// <summary>Emit one boundary-aligned sub-run [<paramref name="from"/>, <paramref name="to"/>).</summary>
        private void AppendRun(
            StackPanel panel, string runText, int absStart, int from, int to,
            IReadOnlyList<Problem>? problems, IReadOnlyList<TextRange>? hits, int currentHit)
        {
            if (to <= from) return;
            var sub = runText.Substring(from - absStart, to - from);
            var tb = MakeText(sub);
            AddSeg(panel, tb, WrapWithOverlays(tb, from, to, problems, hits, currentHit), from, to);
        }

        private static TextBlock MakeText(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            };
            tb.Classes.Add("inlineText");
            return tb;
        }

        // Mirrors the AppTheme styles the chip/text classes apply — keep in sync:
        // TextBlock.inlineText   → body font (inherited),   FontSize 12.5, LineHeight 19
        // TextBlock.inlineTagText→ mono stack,               FontSize 11,   LineHeight 19
        private const string ChipFontStack = "JetBrains Mono, Cascadia Code, Consolas, Menlo, monospace";
        private static double? _chipBaselineDelta;

        /// <summary>
        /// Vertical offset that puts the chip's mono glyphs on the SAME BASELINE
        /// as the surrounding body text. Both runs live in 19px line boxes, so
        /// centering aligns the BOXES — but Inter@12.5 and the mono stack@11
        /// have different ascents, which made chip text sit visibly higher.
        /// Computed ONCE from the fonts Avalonia actually resolves at runtime
        /// (whichever of JetBrains Mono / Cascadia / Consolas exists on this
        /// machine is measured, not guessed) and applied as a render-only
        /// transform — layout heights stay untouched, so the fixed 19px rows
        /// and DataGrid virtualization are unaffected.
        /// </summary>
        private double GetChipBaselineDelta()
        {
            if (_chipBaselineDelta is double cached) return cached;

            double delta = 0;
            try
            {
                var bodyFamily = GetValue(TextBlock.FontFamilyProperty) ?? FontFamily.Default;
                double bodyBaseline = FirstBaseline(bodyFamily, 12.5);
                double chipBaseline = FirstBaseline(new FontFamily(ChipFontStack), 11);
                delta = bodyBaseline - chipBaseline;
            }
            catch
            {
                // Metrics unavailable → keep 0 (exactly today's behavior).
                delta = 0;
            }
            _chipBaselineDelta = delta;
            return delta;

            static double FirstBaseline(FontFamily family, double size)
            {
                var layout = new TextLayout("Ag", new Typeface(family), size, Brushes.Black, lineHeight: 19);
                return layout.TextLines[0].Baseline;
            }
        }

        private Border MakeChip(string tagText)
        {
            var inner = new TextBlock
            {
                Text = tagText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            inner.Classes.Add("inlineTagText");

            var border = new Border { Child = inner };
            border.Classes.Add("inlineTagChip");

            // Baseline compensation applied to the WHOLE chip (border + its text)
            // instead of the inner text alone: the mono glyphs keep their natural
            // centered position INSIDE the chip, and the entire chip shifts by
            // delta so the text still lands on the surrounding body-text baseline.
            // (Shifting only the inner text put it at the right baseline but low
            // inside the border — the "text sits near the bottom edge" look.)
            // Render-only transform: layout height stays 19px, so the fixed row
            // height and DataGrid virtualization are untouched.
            double delta = GetChipBaselineDelta();
            if (Math.Abs(delta) > 0.05)
                border.RenderTransform = new TranslateTransform(0, delta);

            return border;
        }

        /// <summary>
        /// Wrap <paramref name="inner"/> in a 1-cell Grid with overlay layers
        /// when its character range intersects a ProblemSpan or FindHit.
        /// Returns the bare <paramref name="inner"/> when nothing intersects
        /// (zero allocation overhead on the common path).
        ///
        /// Etap 6: when a wavy underline is added (the segment intersects a
        /// Problem with a Range), the wrap Grid also wires PointerEntered /
        /// Exited to surface the problem details popover. The intersecting
        /// Problem is captured by reference so the popover gets the exact
        /// one whose Range covers this segment (not just the first/highest).
        /// </summary>
        private Control WrapWithOverlays(
            Control inner,
            int segStart,
            int segEnd,
            IReadOnlyList<Problem>? problems,
            IReadOnlyList<TextRange>? hits,
            int currentHit)
        {
            var sev = HighestSeverityIn(segStart, segEnd, problems);
            var (hit, isCurrent) = FindHitIn(segStart, segEnd, hits, currentHit);
            if (sev is null && !hit) return inner;

            var grid = new Grid();

            if (hit)
            {
                grid.Children.Add(new Border
                {
                    Background = MakeFindHitBrush(isCurrent),
                    CornerRadius = new CornerRadius(2),
                });
            }

            grid.Children.Add(inner);

            if (sev is not null)
            {
                grid.Children.Add(new WavyUnderline
                {
                    Stroke = MakeSeverityBrush(sev.Value),
                    Height = 3,
                    VerticalAlignment = VerticalAlignment.Bottom,
                });

                // Etap 6 — popover hover trigger. Pick the highest-severity Problem
                // whose Range covers this segment; that's the one whose detail we
                // want to surface. PointerEntered runs the show grace timer in the
                // service; PointerExited runs the hide grace.
                var hoverProblem = HighestSeverityProblemIn(segStart, segEnd, problems);
                if (hoverProblem is not null)
                {
                    grid.PointerEntered += (_, _) =>
                    {
                        if (DataContext is TranslationRow row)
                            Services.ProblemPopoverService.Instance.RequestShow(grid, hoverProblem, row);
                    };
                    grid.PointerExited += (_, _) =>
                        Services.ProblemPopoverService.Instance.RequestHide();
                }
            }

            return grid;
        }

        private static Problem? HighestSeverityProblemIn(int segStart, int segEnd, IReadOnlyList<Problem>? problems)
        {
            if (problems is null || problems.Count == 0) return null;
            Problem? best = null;
            foreach (var p in problems)
            {
                if (p.Range is not TextRange r) continue;
                if (r.Length <= 0) continue;
                if (r.End <= segStart || r.Start >= segEnd) continue;
                if (best is null || p.Severity > best.Severity) best = p;
            }
            return best;
        }

        private static ProblemSeverity? HighestSeverityIn(int segStart, int segEnd, IReadOnlyList<Problem>? problems)
        {
            if (problems is null || problems.Count == 0) return null;
            ProblemSeverity? max = null;
            foreach (var p in problems)
            {
                if (p.Range is not TextRange r) continue;
                if (r.Length <= 0) continue;
                if (r.End <= segStart || r.Start >= segEnd) continue;
                if (max is null || p.Severity > max) max = p.Severity;
            }
            return max;
        }

        private static (bool intersects, bool isCurrent) FindHitIn(
            int segStart, int segEnd, IReadOnlyList<TextRange>? hits, int currentHit)
        {
            if (hits is null || hits.Count == 0) return (false, false);
            bool any = false;
            bool curr = false;
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                if (h.Length <= 0) continue;
                if (h.End <= segStart || h.Start >= segEnd) continue;
                any = true;
                if (i == currentHit) curr = true;
            }
            return (any, curr);
        }

        private static IBrush MakeSeverityBrush(ProblemSeverity sev)
        {
            bool dark = AppConfig.Instance.IsDarkTheme;
            return sev switch
            {
                ProblemSeverity.Error => SolidColorBrush.Parse(dark ? "#F48771" : "#B44A2A"),
                ProblemSeverity.Warning => SolidColorBrush.Parse(dark ? "#DCDCAA" : "#B5891D"),
                _ => SolidColorBrush.Parse(dark ? "#9CDCFE" : "#2B6CB0"),
            };
        }

        private static IBrush MakeFindHitBrush(bool isCurrent)
        {
            // Translucent accent overlay. Current hit is twice as strong so it
            // stands out among the rest. Hex prefix = alpha channel.
            var accent = Services.ThemeManager.Accent;
            return SolidColorBrush.Parse(isCurrent ? accent.FindHitCurrent : accent.FindHit);
        }

        /// <summary>
        /// One wrapped line as a <see cref="WrapPanel"/> of word- and chip-segments, each
        /// vertically centered — the SAME alignment model the no-wrap line uses, so chips and
        /// the surrounding text never drift apart. (The previous implementation used a TextBlock
        /// with an <c>InlineUIContainer</c> chip whose baseline placement grew the line box and
        /// pushed the text below the chip — the misalignment users saw when expanding rows or
        /// editing a tagged row.) Wrapping now happens between words at the panel level. Overlays
        /// (find hits / wavy underlines) are still not drawn in wrap mode — see <see cref="Rebuild"/>.
        /// </summary>
        private Control CreateWrapLine(string line, int lineStart)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };

            int pos = 0;
            foreach (Match match in TagRegex.Matches(line))
            {
                if (match.Index > pos)
                    AddWrapWords(panel, line.Substring(pos, match.Index - pos), lineStart + pos);
                var chip = MakeChip(match.Value);
                AddSeg(panel, chip, chip, lineStart + match.Index, lineStart + match.Index + match.Length);
                pos = match.Index + match.Length;
            }
            if (pos < line.Length)
                AddWrapWords(panel, line.Substring(pos), lineStart + pos);

            return panel;
        }

        /// <summary>
        /// Add a plain-text run to a wrap line as one TextBlock per whitespace-delimited word
        /// (the trailing spaces stay attached to the word), so the <see cref="WrapPanel"/> can
        /// break between words while every piece keeps the chips' vertical centering. A single
        /// unbreakable long token won't wrap mid-word, but UI strings effectively never need that.
        /// </summary>
        private void AddWrapWords(WrapPanel panel, string text, int absStart)
        {
            int i = 0;
            while (i < text.Length)
            {
                int start = i;
                while (i < text.Length && text[i] != ' ') i++; // the word
                while (i < text.Length && text[i] == ' ') i++;  // + its trailing space(s)
                var tb = MakeText(text.Substring(start, i - start));
                AddSeg(panel, tb, tb, absStart + start, absStart + i);
            }
        }
    }
}
