using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Turns a cell's value into the string the render tree is given. Shared by the column types, which
    /// each carried their own copy of the format rule and the join loop.
    /// </summary>
    internal static class CellText
    {
        /// <summary>The value as a string, honouring a format when the value can take one.</summary>
        internal static string? Of(object? value, string? format) =>
            format is { Length: > 0 } && value is IFormattable formattable
                ? formattable.ToString(format, CultureInfo.CurrentCulture)
                : value?.ToString();

        [ThreadStatic]
        static StringBuilder? shared;

        /// <summary>
        /// Lists the members of a sequence whose element type is only known at run time - a column
        /// declared as <c>object</c>, or one whose collection was recognised from its interfaces.
        /// </summary>
        /// <param name="sequence">The members to list.</param>
        /// <param name="separator">What goes between them.</param>
        /// <param name="text">How one member is rendered. Built once per column, not once per cell.</param>
        internal static string Join(IEnumerable sequence, string separator, Func<object?, string?> text)
        {
            var enumerator = sequence.GetEnumerator();

            try
            {
                if (!enumerator.MoveNext())
                {
                    return string.Empty;
                }

                var first = text(enumerator.Current);

                if (!enumerator.MoveNext())
                {
                    // One member is the common case for a small collection, and needs no builder.
                    return first ?? string.Empty;
                }

                var builder = Rent();

                builder.Append(first).Append(separator).Append(text(enumerator.Current));

                while (enumerator.MoveNext())
                {
                    builder.Append(separator).Append(text(enumerator.Current));
                }

                return Return(builder);
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// The same for a sequence whose element type is a type parameter, which is the difference
        /// between a value member costing a string and costing a string and a box.
        /// </summary>
        /// <remarks>
        /// Written out rather than shared with the loop above: the two differ in the enumerator they
        /// walk, and the only way to abstract over that is the interface that does the boxing. What is
        /// shared is the part that is a policy rather than a loop - which builder, and when it is kept.
        /// </remarks>
        internal static string Join<T>(IEnumerable<T> sequence, string separator, Func<T, string?> text)
        {
            using var enumerator = sequence.GetEnumerator();

            if (!enumerator.MoveNext())
            {
                return string.Empty;
            }

            var first = text(enumerator.Current);

            if (!enumerator.MoveNext())
            {
                return first ?? string.Empty;
            }

            var builder = Rent();

            builder.Append(first).Append(separator).Append(text(enumerator.Current));

            while (enumerator.MoveNext())
            {
                builder.Append(separator).Append(text(enumerator.Current));
            }

            return Return(builder);
        }

        /// <summary>
        /// The shared builder, taken rather than borrowed: the text delegate is application code, and a
        /// <c>ToString</c> that rendered a grid of its own would otherwise write into the builder the
        /// call that lent it is still using.
        /// </summary>
        static StringBuilder Rent()
        {
            var builder = shared ?? new StringBuilder();

            shared = null;

            return builder;
        }

        /// <summary>
        /// The joined string, and the builder back on the shelf - unless one cell listed something
        /// enormous, which must not keep that buffer alive for the lifetime of the thread.
        /// </summary>
        static string Return(StringBuilder builder)
        {
            var joined = builder.ToString();

            if (builder.Capacity <= 1024)
            {
                builder.Clear();

                shared = builder;
            }

            return joined;
        }
    }
}
