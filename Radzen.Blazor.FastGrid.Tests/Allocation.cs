using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Measures what one cell costs to render. Boxing leaves no trace in the markup, so the only way to
    /// assert it is absent is to weigh it.
    /// </summary>
    static class Allocation
    {
        public static double PerCell<TItem>(ColumnBase<TItem> column, TItem item, int iterations)
        {
            var builder = new RenderTreeBuilder();

            // Warm up: JIT the path and grow the builder's frame array to its final size, so the measured
            // loop below never resizes it and the only allocation left is the cell's own.
            for (var i = 0; i < iterations; i++)
            {
                column.RenderCell(builder, 0, item);
            }

            builder.Clear();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < iterations; i++)
            {
                column.RenderCell(builder, 0, item);
            }

            var after = GC.GetAllocatedBytesForCurrentThread();

            builder.Clear();

            return (after - before) / (double)iterations;
        }
    }

    /// <summary>
    /// The cell written the naive way: read the value as an object and let RenderTreeBuilder stringify it.
    /// Exists only as the yardstick the real column is weighed against.
    /// </summary>
    sealed class BoxingColumn<TItem, TProp> : ColumnBase<TItem>
    {
        readonly Func<TItem, TProp> compiled;

        public BoxingColumn(Expression<Func<TItem, TProp>> property) => compiled = property.Compile();

        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, (object)compiled(item));
    }

    /// <summary>
    /// The formatted cell written the naive way: cast the value to IFormattable, which boxes a struct.
    /// The yardstick for the formatted path, as BoxingColumn is for the unformatted one.
    /// </summary>
    sealed class FormattingBoxingColumn<TItem, TProp> : ColumnBase<TItem>
    {
        readonly Func<TItem, TProp> compiled;
        readonly string format;

        public FormattingBoxingColumn(Expression<Func<TItem, TProp>> property, string format)
        {
            compiled = property.Compile();
            this.format = format;
        }

        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence,
                ((IFormattable)(object)compiled(item))?.ToString(format, CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// A lookup collection cell listed through the untyped join, which boxes every id on the way to a
    /// <c>Func&lt;object, string&gt;</c>. Identical characters out, so the difference from the real
    /// column is the boxes and nothing else.
    /// </summary>
    sealed class BoxingJoinColumn<TItem, TKey> : ColumnBase<TItem>
    {
        readonly Func<TItem, IEnumerable<TKey>> ids;
        readonly string separator;
        readonly Func<object, string> show;

        public BoxingJoinColumn(Expression<Func<TItem, IEnumerable<TKey>>> property,
            IReadOnlyDictionary<TKey, string> names, string separator)
        {
            ids = property.Compile();
            this.separator = separator;
            show = value => value is TKey key && names.TryGetValue(key, out var text) ? text : value?.ToString();
        }

        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence,
                ids(item) is { } members ? CellText.Join((IEnumerable)members, separator, show) : null);
    }
}
