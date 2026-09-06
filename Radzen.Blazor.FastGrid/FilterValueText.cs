using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>
    /// The canonical text a filter value is stored as, and the way back from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §33's answer to §32. A stored value used to be <c>object?</c>, so a serializer flattened it and
    /// four heuristic attempts tried to guess the type back - and a check-box list stored as a JSON array
    /// could not be guessed at all. Values are invariant-culture strings now: <strong>no serializer can
    /// turn a string into anything but a string</strong>, so the round trip is lossless by construction
    /// and the way back is one parse against the column's own type.
    /// </para>
    /// <para>
    /// Invariant and round-trippable, which are two requirements rather than one. <c>ToString</c> on a
    /// <see cref="DateTime" /> under the invariant culture drops everything below the second, so a filter
    /// would come back a different instant from the one that was stored; the round-trip formats are used
    /// for the types that have them.
    /// </para>
    /// </remarks>
    internal static class FilterValueText
    {
        /// <summary>One value as the text it is stored as, or null where there is nothing to store.</summary>
        internal static string? From(object? value) => value switch
        {
            null => null,
            string text => text,

            // "O" round-trips; the invariant ToString does not. DateOnly and TimeOnly are here for the
            // same reason and because Convert.ChangeType cannot see them at all - they are not
            // IConvertible, so without this pair they store and restore as nothing.
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly day => day.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),

            // Enums by name rather than by number, so a stored filter survives someone inserting a
            // member. Guid's own ToString is already canonical.
            Enum member => member.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        /// <summary>
        /// A condition's values as the texts they are stored as, flattening the sequence an <c>In</c>
        /// carries as its single value.
        /// </summary>
        /// <remarks>
        /// Flattened because a check-box selection arrives as one object holding many ids, and storing
        /// it as one text would store <c>System.Collections.Generic.List`1[System.Int32]</c>. One text
        /// per value is also what makes <c>In</c>, <c>Between</c> and <c>Equals</c> one storage shape.
        /// </remarks>
        internal static List<string?> From(FastGridFilterCondition condition)
        {
            var texts = new List<string?>();

            if (condition.Operator.Arity() == FastGridFilterArity.Many)
            {
                if (condition.Value is IEnumerable sequence and not string)
                {
                    foreach (var item in sequence)
                    {
                        texts.Add(From(item));
                    }

                    return texts;
                }
            }

            for (var i = 0; i < condition.Values.Count; i++)
            {
                texts.Add(From(condition.Values[i]));
            }

            return texts;
        }

        /// <summary>
        /// One stored text as a value of <paramref name="declared" />, or null when it is not one.
        /// </summary>
        internal static object? To(string? text, Type declared)
        {
            if (text is null)
            {
                return null;
            }

            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            if (type == typeof(string) || type == typeof(object))
            {
                return text;
            }

            try
            {
                // The types Convert.ChangeType cannot see, which is why a filter on any of them used to
                // be dropped on every restore however it was stored. §33 recorded that as a gap and this
                // is the gap closed: the format is being defined here, so the parse can be too.
                if (type == typeof(DateOnly))
                {
                    return DateOnly.Parse(text, CultureInfo.InvariantCulture);
                }

                if (type == typeof(TimeOnly))
                {
                    return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
                }

                if (type == typeof(DateTimeOffset))
                {
                    return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
                }

                if (type == typeof(TimeSpan))
                {
                    return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
                }

                return type.IsEnum
                    ? Enum.Parse(type, text, ignoreCase: true)
                    : ConvertType.ChangeType(text, declared, CultureInfo.InvariantCulture);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Every exception, on §9's rule and for the reason its neighbours in ColumnBase give: an
                // optional path whose whole job is to drop what will not parse, over text nobody here
                // chose. A stored filter that cannot be read is dropped, which is §32's policy.
                return null;
            }
        }
    }
}
