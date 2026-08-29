using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AngleSharp.Dom;
using Xunit.Sdk;

namespace Radzen.Blazor.FastGrid.Tests
{
    /// <summary>
    /// Failure messages that name the broken rule, not just the failed comparison.
    /// </summary>
    /// <remarks>
    /// A check is only worth having if its output tells whoever reads the CI log what to go and fix. Each
    /// failure here carries the rule, why the rule exists, what was expected, what was found, and an
    /// excerpt of the offending markup.
    /// </remarks>
    static class ParityAssert
    {
        /// <summary>Fails with a message that identifies the rule and shows the markup that broke it.</summary>
        public static void Fail(string rule, string why, string expected, string actual, string excerpt = null)
        {
            var message = new StringBuilder();

            message.AppendLine(CultureInfo.InvariantCulture, $"Styling parity broken: {rule}");
            message.AppendLine();
            message.AppendLine(CultureInfo.InvariantCulture, $"  why it matters : {why}");
            message.AppendLine(CultureInfo.InvariantCulture, $"  expected       : {expected}");
            message.AppendLine(CultureInfo.InvariantCulture, $"  actual         : {actual}");

            if (!string.IsNullOrEmpty(excerpt))
            {
                message.AppendLine();
                message.AppendLine("  markup:");
                message.AppendLine(Indent(excerpt));
            }

            throw new XunitException(message.ToString());
        }

        public static void True(bool condition, string rule, string why, string expected, string actual,
            string excerpt = null)
        {
            if (!condition)
            {
                Fail(rule, why, expected, actual, excerpt);
            }
        }

        /// <summary>The element's opening tag, which is what identifies it in a failure message.</summary>
        public static string OpeningTag(IElement element)
        {
            if (element is null)
            {
                return "(no such element)";
            }

            var html = element.OuterHtml;
            var close = html.IndexOf('>');

            return close < 0 ? html : html[..(close + 1)];
        }

        /// <summary>An element's markup, truncated so a 1000-row grid does not fill the log.</summary>
        public static string Excerpt(IElement element, int maxLength = 400)
        {
            if (element is null)
            {
                return "(no such element)";
            }

            var html = element.OuterHtml;

            return html.Length <= maxLength ? html : html[..maxLength] + " ...";
        }

        /// <summary>Class tokens in a stable order, so two class lists can be compared as sets.</summary>
        public static string NormalizeClasses(IElement element) =>
            string.Join(' ', (element.ClassName ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(c => c, StringComparer.Ordinal));

        public static IEnumerable<string> ClassTokens(IElement element) =>
            (element.ClassName ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        static string Indent(string text) =>
            string.Join(Environment.NewLine, text.Split('\n').Select(line => "    " + line.TrimEnd('\r')));
    }
}
