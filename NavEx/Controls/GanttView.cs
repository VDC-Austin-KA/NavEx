using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NavEx.FourD;

namespace NavEx.Controls
{
    public enum GanttZoom { Day, Week, Month, Quarter }

    /// <summary>
    /// A Gantt chart drawn directly into a <see cref="DrawingContext"/>.
    ///
    /// Immediate-mode rather than a panel full of elements: a construction
    /// schedule routinely runs to several thousand activities, and giving each one
    /// a Border and two TextBlocks makes the layout pass cost more than the
    /// drawing. Here the whole chart is a few hundred draw calls regardless of task
    /// count, because only the visible rows are rendered.
    ///
    /// The control sizes itself to its content and expects to live in a
    /// ScrollViewer. <see cref="VerticalOffset"/> is fed back in from that
    /// ScrollViewer so the date header can be redrawn pinned to the top of the
    /// viewport instead of scrolling away.
    /// </summary>
    public class GanttView : FrameworkElement
    {
        public const double RowHeight = 22;
        public const double HeaderHeight = 38;
        public const double NameColumnWidth = 280;

        private IList<TaskMatch> _tasks = new List<TaskMatch>();
        private DateTime _start = DateTime.Today;
        private DateTime _end = DateTime.Today.AddDays(30);
        private double _pixelsPerDay = 4;
        private GanttZoom _zoom = GanttZoom.Week;
        private int _selectedIndex = -1;
        private double _verticalOffset;

        // ── Palette ──────────────────────────────────────────────────────────
        private static readonly Brush HeaderBackground = Frozen(Color.FromRgb(0xF9, 0xFA, 0xFB));
        private static readonly Brush NameBackground = Frozen(Color.FromRgb(0xFC, 0xFC, 0xFD));
        private static readonly Brush GridLine = Frozen(Color.FromRgb(0xE5, 0xE7, 0xEB));
        private static readonly Brush StrongGridLine = Frozen(Color.FromRgb(0xCB, 0xD1, 0xD9));
        private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x11, 0x18, 0x27));
        private static readonly Brush MutedText = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush SelectionFill = Frozen(Color.FromArgb(0x22, 0x1F, 0x6F, 0xEB));
        private static readonly Brush TodayLine = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
        private static readonly Brush WeekendFill = Frozen(Color.FromRgb(0xF3, 0xF4, 0xF6));

        private static readonly Brush BarAuto = Frozen(Color.FromRgb(0x1F, 0x6F, 0xEB));
        private static readonly Brush BarManual = Frozen(Color.FromRgb(0x05, 0x96, 0x69));
        private static readonly Brush BarUnmatched = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));
        private static readonly Brush BarExcluded = Frozen(Color.FromRgb(0xD1, 0xD5, 0xDB));
        private static readonly Brush BarLowConfidence = Frozen(Color.FromRgb(0xD9, 0x77, 0x06));
        private static readonly Brush ActualBar = Frozen(Color.FromArgb(0xCC, 0x11, 0x18, 0x27));

        private readonly Typeface _typeface = new Typeface("Segoe UI");

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public GanttView()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        /// <summary>Raised when a row is clicked; the argument is the row index.</summary>
        public event Action<int> TaskClicked;

        public IList<TaskMatch> Tasks
        {
            get { return _tasks; }
            set
            {
                _tasks = value ?? new List<TaskMatch>();
                RecomputeRange();
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public GanttZoom Zoom
        {
            get { return _zoom; }
            set
            {
                _zoom = value;
                _pixelsPerDay = PixelsPerDayFor(value);
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set { _selectedIndex = value; InvalidateVisual(); }
        }

        public double VerticalOffset
        {
            get { return _verticalOffset; }
            set { _verticalOffset = value; InvalidateVisual(); }
        }

        private static double PixelsPerDayFor(GanttZoom zoom)
        {
            switch (zoom)
            {
                case GanttZoom.Day: return 28;
                case GanttZoom.Week: return 8;
                case GanttZoom.Month: return 2.4;
                default: return 0.9;
            }
        }

        private void RecomputeRange()
        {
            var dated = _tasks.Where(t => t.Task != null && t.Task.PlannedStart.HasValue).ToList();
            if (dated.Count == 0)
            {
                _start = DateTime.Today;
                _end = DateTime.Today.AddDays(30);
                return;
            }

            _start = dated.Min(t => t.Task.PlannedStart.Value).Date.AddDays(-3);
            _end = dated
                .Where(t => t.Task.PlannedFinish.HasValue)
                .Select(t => t.Task.PlannedFinish.Value)
                .DefaultIfEmpty(_start.AddDays(30))
                .Max().Date.AddDays(3);

            if (_end <= _start) _end = _start.AddDays(30);
        }

        public void ZoomToFit(double availableWidth)
        {
            double days = Math.Max(1, (_end - _start).TotalDays);
            double usable = Math.Max(200, availableWidth - NameColumnWidth - 24);
            _pixelsPerDay = usable / days;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = NameColumnWidth + Math.Max(1, (_end - _start).TotalDays) * _pixelsPerDay + 24;
            double height = HeaderHeight + _tasks.Count * RowHeight + 8;

            if (double.IsInfinity(availableSize.Width)) return new Size(width, height);
            return new Size(Math.Max(width, availableSize.Width), height);
        }

        private double XFor(DateTime date)
        {
            return NameColumnWidth + (date - _start).TotalDays * _pixelsPerDay;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = Math.Max(ActualWidth, NameColumnWidth + 100);
            double height = Math.Max(ActualHeight, HeaderHeight);

            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            if (_tasks.Count == 0)
            {
                DrawText(dc, "No schedule loaded.", 16, HeaderHeight + 12, MutedText, 12);
                return;
            }

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double headerTop = _verticalOffset;

            DrawTimeGrid(dc, width, height, headerTop);
            DrawRows(dc, width, headerTop, pixelsPerDip);
            DrawHeader(dc, width, headerTop, pixelsPerDip);
        }

        private void DrawTimeGrid(DrawingContext dc, double width, double height, double headerTop)
        {
            var pen = new Pen(GridLine, 1);
            var strongPen = new Pen(StrongGridLine, 1);
            double top = headerTop + HeaderHeight;

            DateTime cursor = _start.Date;
            while (cursor <= _end)
            {
                double x = XFor(cursor);
                if (x > width) break;

                if (x >= NameColumnWidth)
                {
                    // Weekends get a wash so the eye can count working days.
                    if (_zoom == GanttZoom.Day &&
                        (cursor.DayOfWeek == DayOfWeek.Saturday || cursor.DayOfWeek == DayOfWeek.Sunday))
                        dc.DrawRectangle(WeekendFill, null,
                            new Rect(x, top, _pixelsPerDay, Math.Max(0, height - top)));

                    bool major = IsMajorTick(cursor);
                    dc.DrawLine(major ? strongPen : pen,
                        new Point(x, top), new Point(x, height));
                }

                cursor = NextTick(cursor);
            }

            DateTime today = DateTime.Today;
            if (today >= _start && today <= _end)
            {
                double x = XFor(today);
                dc.DrawLine(new Pen(TodayLine, 1.5), new Point(x, top), new Point(x, height));
            }
        }

        private bool IsMajorTick(DateTime date)
        {
            switch (_zoom)
            {
                case GanttZoom.Day: return date.DayOfWeek == DayOfWeek.Monday;
                case GanttZoom.Week: return date.Day == 1;
                default: return date.Month == 1;
            }
        }

        private DateTime NextTick(DateTime date)
        {
            switch (_zoom)
            {
                case GanttZoom.Day: return date.AddDays(1);
                case GanttZoom.Week: return date.AddDays(7);
                case GanttZoom.Month: return date.AddMonths(1);
                default: return date.AddMonths(3);
            }
        }

        private void DrawHeader(DrawingContext dc, double width, double headerTop, double pixelsPerDip)
        {
            dc.DrawRectangle(HeaderBackground, null, new Rect(0, headerTop, width, HeaderHeight));
            dc.DrawLine(new Pen(StrongGridLine, 1),
                new Point(0, headerTop + HeaderHeight), new Point(width, headerTop + HeaderHeight));

            DrawText(dc, "Task", 8, headerTop + 11, TextBrush, 11.5, true, pixelsPerDip);

            DateTime cursor = _start.Date;
            DateTime lastLabelled = DateTime.MinValue;

            while (cursor <= _end)
            {
                double x = XFor(cursor);
                if (x > width) break;

                if (x >= NameColumnWidth && (cursor - lastLabelled).TotalDays * _pixelsPerDay > 54)
                {
                    DrawText(dc, LabelFor(cursor), x + 3, headerTop + 11, MutedText, 10.5, false, pixelsPerDip);
                    lastLabelled = cursor;
                }

                cursor = NextTick(cursor);
            }

            dc.DrawLine(new Pen(StrongGridLine, 1),
                new Point(NameColumnWidth, headerTop), new Point(NameColumnWidth, headerTop + HeaderHeight));
        }

        private string LabelFor(DateTime date)
        {
            switch (_zoom)
            {
                case GanttZoom.Day: return date.ToString("d MMM", CultureInfo.CurrentCulture);
                case GanttZoom.Week: return date.ToString("d MMM", CultureInfo.CurrentCulture);
                case GanttZoom.Month: return date.ToString("MMM yy", CultureInfo.CurrentCulture);
                default: return date.ToString("MMM yy", CultureInfo.CurrentCulture);
            }
        }

        private void DrawRows(DrawingContext dc, double width, double headerTop, double pixelsPerDip)
        {
            double top = HeaderHeight;

            // Only the rows inside the viewport are drawn; a 5000-activity
            // schedule costs the same as a 50-activity one.
            int first = Math.Max(0, (int)((_verticalOffset - top) / RowHeight) - 1);
            int visible = (int)(Math.Max(ActualHeight, 400) / RowHeight) + 3;
            int last = Math.Min(_tasks.Count - 1, first + visible);

            dc.DrawRectangle(NameBackground, null,
                new Rect(0, top + first * RowHeight, NameColumnWidth,
                         Math.Max(0, (last - first + 1) * RowHeight)));

            var rowPen = new Pen(GridLine, 1);

            for (int i = first; i <= last; i++)
            {
                TaskMatch match = _tasks[i];
                if (match == null || match.Task == null) continue;

                double y = top + i * RowHeight;

                if (i == _selectedIndex)
                    dc.DrawRectangle(SelectionFill, null, new Rect(0, y, width, RowHeight));

                dc.DrawLine(rowPen, new Point(0, y + RowHeight), new Point(width, y + RowHeight));

                DrawText(dc, Truncate(match.Task.Name, 42), 8, y + 4, TextBrush, 11, false, pixelsPerDip);

                DrawBar(dc, match, y, pixelsPerDip);
            }

            dc.DrawLine(new Pen(StrongGridLine, 1),
                new Point(NameColumnWidth, top), new Point(NameColumnWidth, top + _tasks.Count * RowHeight));
        }

        private void DrawBar(DrawingContext dc, TaskMatch match, double y, double pixelsPerDip)
        {
            ScheduleTask task = match.Task;
            if (!task.PlannedStart.HasValue || !task.PlannedFinish.HasValue) return;

            double x1 = XFor(task.PlannedStart.Value);
            double x2 = XFor(task.PlannedFinish.Value);
            if (x2 < x1) { double swap = x1; x1 = x2; x2 = swap; }

            // A zero-length milestone still needs to be visible.
            double barWidth = Math.Max(3, x2 - x1);
            var rect = new Rect(x1, y + 4, barWidth, RowHeight - 9);

            dc.DrawRoundedRectangle(BrushFor(match), null, rect, 2, 2);

            // Actual dates overlay the planned bar as a darker inner strip.
            if (task.ActualStart.HasValue)
            {
                DateTime actualEnd = task.ActualFinish ?? DateTime.Today;
                double a1 = XFor(task.ActualStart.Value);
                double a2 = XFor(actualEnd);
                if (a2 > a1)
                    dc.DrawRectangle(ActualBar, null,
                        new Rect(a1, y + RowHeight * 0.5 - 2, Math.Max(2, a2 - a1), 4));
            }

            string label = match.Target != null ? match.Target.SetName : "";
            if (!string.IsNullOrEmpty(label) && barWidth > 40)
                DrawText(dc, Truncate(label, (int)(barWidth / 6)), x1 + 4, y + 4,
                         Brushes.White, 10, false, pixelsPerDip);
        }

        private static Brush BrushFor(TaskMatch match)
        {
            switch (match.State)
            {
                case MatchState.Manual:
                case MatchState.Confirmed:
                    return BarManual;
                case MatchState.Excluded:
                    return BarExcluded;
                case MatchState.Auto:
                    return match.ConfidenceBand == "low" ? BarLowConfidence : BarAuto;
                default:
                    return BarUnmatched;
            }
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (length < 4) return "";
            return value.Length <= length ? value : value.Substring(0, length - 1) + "…";
        }

        private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size)
        {
            DrawText(dc, text, x, y, brush, size, false, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush,
                              double size, bool bold, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text)) return;

            var typeface = bold
                ? new Typeface(_typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal)
                : _typeface;

            // The pixelsPerDip overload is the non-obsolete one; the older
            // constructor assumes 96 DPI and blurs on scaled displays.
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                              typeface, size, brush, pixelsPerDip);
            dc.DrawText(formatted, new Point(x, y));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            Point point = e.GetPosition(this);
            int index = (int)((point.Y - HeaderHeight) / RowHeight);
            if (index < 0 || index >= _tasks.Count) return;

            SelectedIndex = index;
            Action<int> handler = TaskClicked;
            if (handler != null) handler(index);
        }
    }
}
