using System;
using System.Collections;
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
        /// Lists the members of a sequence. The string a cell of a collection column produces is
        /// unavoidable - and still cheaper than the render fragment a template would have cost - but the
        /// builder it is assembled in is not: one per thread is reused across every such cell.
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

                // Taken rather than borrowed: text is application code, and a ToString that rendered a
                // grid of its own would otherwise write into the builder this call is still using.
                var builder = shared ?? new StringBuilder();
                shared = null;

                builder.Append(first).Append(separator).Append(text(enumerator.Current));

                while (enumerator.MoveNext())
                {
                    builder.Append(separator).Append(text(enumerator.Current));
                }

                var joined = builder.ToString();

                // One cell that listed something enormous must not keep that buffer alive for the
                // lifetime of the thread, so an overgrown builder is dropped rather than kept.
                if (builder.Capacity <= 1024)
                {
                    builder.Clear();
                    shared = builder;
                }

                return joined;
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
    }
}
