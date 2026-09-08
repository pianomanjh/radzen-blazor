using System;
using Radzen.Documents.Spreadsheet;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// What a cell can be handed, and what has to become text first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This class exists because <c>Cell.Value</c> throws, and §40 guessed only that it might
    /// format oddly.</strong> The section's open question was <em>"whether <c>Cell.Value</c> infers the
    /// same set - and what it does with a <c>DateOnly</c>, a <c>TimeSpan</c> or a nullable enum"</em>.
    /// The answer is that <c>CellData</c>'s type switch ends in
    /// <c>throw new NotSupportedException</c>, and its whole accepted set is: null, <c>string</c>,
    /// <c>bool</c>, <c>DateTime</c>, and the eleven numeric primitives - plus their nullable forms.
    /// </para>
    /// <para>
    /// Probed one type at a time against a real workbook, these throw: <strong><c>DateOnly</c>,
    /// <c>TimeOnly</c>, <c>TimeSpan</c>, <c>DateTimeOffset</c>, <c>Guid</c>, and every enum</strong>,
    /// nullable or not. An enum column is ordinary in a grid, so without this an ordinary export
    /// crashes rather than formats oddly.
    /// </para>
    /// <para>
    /// <strong>Typed where a spreadsheet gains something, text where it does not.</strong> §38's
    /// application is explicit that numbers and dates stay typed so Excel sorts and sums them, and that
    /// is the rule kept here. <c>DateOnly</c> and <c>DateTimeOffset</c> are dates, so they are converted
    /// to <c>DateTime</c> rather than written as text - the writer gives a date cell a
    /// <c>mm/dd/yyyy</c> format of its own accord, measured, so it reads as a date in Excel. Everything
    /// the writer refuses becomes the column's own text, which is §40's fallback rule and is what the
    /// reader was looking at on screen.
    /// </para>
    /// </remarks>
    static class ExportValue
    {
        /// <summary>Writes a value into a cell in the closest form the writer will keep.</summary>
        /// <remarks>
        /// <para>
        /// <c>SetValue</c> with a leading apostrophe is the writer's own answer, and its comment names
        /// this exact case: <em>"the quote prefix means literal text, so the string must not go through
        /// type inference ('0123 stays "0123")"</em>. It sets Excel's <c>quotePrefix</c> flag, which is
        /// what Excel itself writes for text that looks like a number.
        /// </para>
        /// <para>
        /// Only where the inference actually bit, so an ordinary word carries no flag: assign, and
        /// insist only if what came back is no longer a string. Asking first is not possible from here -
        /// <c>CellData.TryConvertFromString</c> is internal to <c>Radzen.Blazor</c>.
        /// </para>
        /// </remarks>
        /// <param name="cell">The cell to write.</param>
        /// <param name="value">The value the column produced, which may be of any type.</param>
        /// <param name="text">The column's own text for the same cell, used where the value cannot be.</param>
        internal static void Write(Cell cell, object? value, string? text)
        {
            var coerced = Coerce(value, text);

            cell.Value = coerced;

            if (coerced is string written && cell.ValueType != CellDataType.String)
            {
                cell.SetValue("'" + written);
            }
        }

        /// <summary>The value a cell can actually hold, given what the column produced.</summary>
        internal static object? Coerce(object? value, string? text) => value switch
        {
            null => null,

            // The writer's whole accepted set, kept as it is so Excel sorts and sums it.
            string => value,
            byte or sbyte or short or ushort or int or uint or long or ulong => value,
            float or double or decimal => value,
            DateTime => value,

            // Dates the writer does not know are still dates. Converting keeps them sortable and lets
            // the writer apply its own date format, which text would lose.
            //
            // A DateTimeOffset loses its offset here, and .DateTime rather than .UtcDateTime is the
            // deliberate half of that: a spreadsheet cell has nowhere to put an offset, so one of the
            // two has to go, and the local reading is the one the grid drew in the cell beside it.
            // An application that means the instant exports .UtcDateTime through ExportValue.
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            DateTimeOffset offset => offset.DateTime,

            // A bool stays a bool. It was text here until the file was read rather than the round trip:
            // XlsxWriter writes t="b", which is ECMA-376's boolean and what Excel shows as TRUE, and it
            // was XlsxReader dropping the attribute that made a round trip answer with the number 1. The
            // file was always right, so exporting the word instead was a demotion made for a fault that
            // was never in the file - and typed is what lets Excel filter the column as a boolean.
            bool => value,

            // Everything else: an enum, a Guid, a TimeOnly, a TimeSpan and an application's own type
            // would all throw out of Cell.Value. The column's text is what the reader was looking at.
            _ => text ?? value.ToString(),
        };
    }
}
