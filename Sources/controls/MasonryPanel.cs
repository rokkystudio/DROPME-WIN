using System;
using System.Windows;
using System.Windows.Controls;

namespace DROPME.Controls
{
    /// <summary>
    /// Размещает дочерние блоки в одной колонке на узкой ширине и в двух колонках на широкой.
    /// В двухколоночном режиме каждый следующий блок помещается в колонку с меньшей текущей высотой.
    /// </summary>
    internal sealed class MasonryPanel : Panel
    {
        public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
            nameof(MinColumnWidth),
            typeof(double),
            typeof(MasonryPanel),
            new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
            nameof(ColumnGap),
            typeof(double),
            typeof(MasonryPanel),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
            nameof(RowGap),
            typeof(double),
            typeof(MasonryPanel),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MinColumnWidth
        {
            get => (double)GetValue(MinColumnWidthProperty);
            set => SetValue(MinColumnWidthProperty, value);
        }

        public double ColumnGap
        {
            get => (double)GetValue(ColumnGapProperty);
            set => SetValue(ColumnGapProperty, value);
        }

        public double RowGap
        {
            get => (double)GetValue(RowGapProperty);
            set => SetValue(RowGapProperty, value);
        }

        /// <summary>
        /// Измеряет видимые блоки с шириной текущей колонки и возвращает высоту самой высокой колонки.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width)
                ? Math.Max(ActualWidth, MinColumnWidth)
                : availableSize.Width;
            int columnCount = GetColumnCount(width);
            double columnWidth = GetColumnWidth(width, columnCount);
            double[] heights = new double[columnCount];

            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                int column = GetShortestColumn(heights);
                if (heights[column] > 0)
                {
                    heights[column] += RowGap;
                }
                heights[column] += child.DesiredSize.Height;
            }

            return new Size(width, GetMaximumHeight(heights));
        }

        /// <summary>
        /// Располагает видимые блоки сверху вниз, каждый раз выбирая колонку с меньшей занятой высотой.
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            int columnCount = GetColumnCount(finalSize.Width);
            double columnWidth = GetColumnWidth(finalSize.Width, columnCount);
            double[] heights = new double[columnCount];

            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                int column = GetShortestColumn(heights);
                double top = heights[column];
                if (top > 0)
                {
                    top += RowGap;
                }

                double left = column * (columnWidth + ColumnGap);
                child.Arrange(new Rect(left, top, columnWidth, child.DesiredSize.Height));
                heights[column] = top + child.DesiredSize.Height;
            }

            return new Size(finalSize.Width, GetMaximumHeight(heights));
        }

        private int GetColumnCount(double width)
        {
            return width >= MinColumnWidth * 2 + ColumnGap ? 2 : 1;
        }

        private double GetColumnWidth(double width, int columnCount)
        {
            return columnCount == 2
                ? Math.Max(0, (width - ColumnGap) / 2)
                : Math.Max(0, width);
        }

        private static int GetShortestColumn(double[] heights)
        {
            if (heights.Length == 1)
            {
                return 0;
            }

            return heights[0] <= heights[1] ? 0 : 1;
        }

        private static double GetMaximumHeight(double[] heights)
        {
            double result = 0;
            foreach (double height in heights)
            {
                result = Math.Max(result, height);
            }
            return result;
        }
    }
}
