using System;
using System.Globalization;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// A column's .NET format string, as a spreadsheet number format - or nothing, honestly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Giving up is the design.</strong> Where there is no safe equivalent this answers null,
    /// and the caller exports the text the column drew instead of a number wearing the wrong format.
    /// That is fidelity chosen over typing, deliberately and only where typing cannot be kept honest -
    /// everywhere else the number stays a number and Excel can still sum the column.
    /// </para>
    /// <para>
    /// Currency is the one that cannot be right for everyone: <c>C</c> means the current culture's
    /// symbol, and a spreadsheet carries a literal. The current culture's symbol is what the grid drew
    /// for this reader, so that is what goes in.
    /// </para>
    /// </remarks>
    static class ExportFormat
    {
        /// <summary>The Excel number format for a .NET format string, or null where there is none.</summary>
        /// <param name="format">The column's <c>Format</c>, such as <c>C</c>, <c>N2</c> or <c>yyyy-MM-dd</c>.</param>
        internal static string? ToNumberFormat(string? format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return null;
            }

            // A standard specifier is one letter and up to two digits of precision. Anything longer is
            // either a custom pattern or something this does not know.
            if (format.Length <= 3)
            {
                var digits = format.Length > 1 && int.TryParse(format.AsSpan(1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : -1;

                if (format.Length == 1 || digits >= 0)
                {
                    // A standard specifier that maps to nothing is the end of it, not the start of a
                    // custom pattern: the two are disjoint in .NET, and a single letter is always the
                    // former. "d" is the case that made this explicit - it is .NET's short date and
                    // Excel's day-of-month, so falling through would have exported one as the other.
                    return Standard(char.ToUpperInvariant(format[0]), digits);
                }
            }

            // A custom pattern that is already in the file's own language: Excel's placeholders are 0,
            // # and the date and time letters, and the two vocabularies agree on all of them. Anything
            // holding a letter neither side reads the same way is left alone.
            return IsExcelPattern(format) ? format : null;
        }

        static string? Standard(char specifier, int digits) => specifier switch
        {
            // "N" and "F" differ only in the thousands separator.
            'N' => Decimals("#,##0", digits < 0 ? 2 : digits),
            'F' => Decimals("0", digits < 0 ? 2 : digits),

            // The reader's own symbol, because it is the one the grid drew in the cell beside this.
            'C' => CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol
                + Decimals("#,##0", digits < 0 ? 2 : digits),

            // Excel's percent format multiplies by 100 exactly as .NET's does, so the values agree.
            'P' => Decimals("0", digits < 0 ? 2 : digits) + "%",

            // "Dn" is a zero-padded integer. Bare "D" and "d" are the standard date specifiers, and
            // they are left to the writer, which gives a date cell a locale-aware format of its own.
            'D' => digits > 0 ? new string('0', digits) : null,

            // "G", "R", "X", "E" and the rest have no spreadsheet equivalent worth guessing at.
            _ => null,
        };

        static string Decimals(string integerPart, int digits) =>
            digits > 0 ? integerPart + "." + new string('0', digits) : integerPart;

        /// <summary>Whether a custom pattern means the same thing to Excel as it does to .NET.</summary>
        /// <remarks>
        /// Conservative on purpose. A pattern of digit placeholders and separators reads identically on
        /// both sides, and so do the date and time letters - with the exception of <c>m</c>, which is
        /// minutes in .NET and months in Excel, and is therefore not a reason to reject a pattern that
        /// is otherwise a date. What this rejects is anything carrying a letter that is a format
        /// specifier to one side and a literal to the other, because that is where a wrong answer looks
        /// like a right one.
        /// </remarks>
        static bool IsExcelPattern(string format)
        {
            var meaningful = false;

            foreach (var c in format)
            {
                if (c is '0' or '#' or 'y' or 'M' or 'd' or 'h' or 'H' or 'm' or 's')
                {
                    meaningful = true;

                    continue;
                }

                if (c is ',' or '.' or ' ' or ':' or '/' or '-' or '%' or '(' or ')' or '\'' or '"')
                {
                    continue;
                }

                return false;
            }

            return meaningful;
        }
    }
}
