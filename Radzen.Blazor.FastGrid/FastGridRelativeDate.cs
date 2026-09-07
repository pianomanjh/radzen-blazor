using System;
using System.Globalization;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A date expressed relative to when it is read: an anchor, a signed offset, and which end of the
    /// day it means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §34. It is a <em>value</em> - it sits in <see cref="FastGridFilterCondition.Values" /> like any
    /// other, and the operator vocabulary, the arity rule and the two-condition shape are all untouched
    /// by its existence. That is the section's stated tripwire rather than a hope: if any of them has to
    /// learn about a token, the piece has gone wrong.
    /// </para>
    /// <para>
    /// <strong>It resolves to one instant.</strong> §33 wrote the case as
    /// <c>Hired Between [last-7-days]</c> - one token in a two-arity operator - and that form cannot
    /// exist, because a single value supplying two bounds would make arity a property of the operator
    /// <em>and</em> of what is sitting in its slots. So <em>last 7 days</em> is a pair,
    /// <c>Between today-6d today@end</c>, and every one of §31's six presets is a pair. The presets are
    /// the menu's vocabulary; these are the model's.
    /// </para>
    /// <para>
    /// <strong>Why it is not resolved when it is picked.</strong> Settings outlive sessions - which is
    /// what §32 and §33 spent two pieces making true - so a preset flattened to absolute dates at pick
    /// time comes back from a blob written in March still asking about March, silently, with dates that
    /// look deliberate.
    /// </para>
    /// </remarks>
    public sealed class FastGridRelativeDate : IEquatable<FastGridRelativeDate>
    {
        const string EndSuffix = "@end";

        /// <summary>A token for the anchor alone - the start of today, of this month, or of this year.</summary>
        public FastGridRelativeDate(FastGridRelativeDateAnchor anchor)
            : this(anchor, 0, FastGridRelativeDateUnit.Days, endOfDay: false)
        {
        }

        /// <summary>A token for an anchor offset by a whole number of days, months or years.</summary>
        /// <param name="anchor">Where counting starts.</param>
        /// <param name="offset">How far from it, signed. Zero is the anchor itself.</param>
        /// <param name="unit">What <paramref name="offset" /> counts.</param>
        /// <param name="endOfDay">
        /// Whether the token means the last tick of its day rather than the first. Explicit rather than
        /// inferred from position: <c>Between</c> is inclusive at both ends, so a range ending at
        /// <c>today</c> at midnight silently drops everything that happened today - a wrong answer that
        /// looks like a right one, which is the failure direction §32 banned. Position-directed
        /// resolution was rejected because the two places a token is read by a human, a settings blob
        /// and a pill, are both places where it sits alone with no position to be judged by.
        /// </param>
        public FastGridRelativeDate(FastGridRelativeDateAnchor anchor, int offset,
            FastGridRelativeDateUnit unit, bool endOfDay)
        {
            Anchor = anchor;

            // A zero offset has no unit to speak of, and leaving one on it would make today+0d and
            // today+0m two values that mean the same instant - which is the second-spelling problem
            // §34 refuses a `w` unit over.
            Offset = offset;
            Unit = offset == 0 ? FastGridRelativeDateUnit.Days : unit;
            EndOfDay = endOfDay;
        }

        /// <summary>Where counting starts.</summary>
        public FastGridRelativeDateAnchor Anchor { get; }

        /// <summary>How far from the anchor, signed and in <see cref="Unit" />s.</summary>
        public int Offset { get; }

        /// <summary>What <see cref="Offset" /> counts. Always days when the offset is zero.</summary>
        public FastGridRelativeDateUnit Unit { get; }

        /// <summary>Whether this is the last tick of its day rather than the first.</summary>
        public bool EndOfDay { get; }

        /// <summary>The start of today.</summary>
        public static FastGridRelativeDate Today { get; } =
            new FastGridRelativeDate(FastGridRelativeDateAnchor.Today);

        /// <summary>The end of today.</summary>
        public static FastGridRelativeDate TodayEnd { get; } =
            new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, 0,
                FastGridRelativeDateUnit.Days, endOfDay: true);

        /// <summary>
        /// Whether a filter value could be relative - which is only ever true for a date column.
        /// </summary>
        /// <remarks>
        /// The whole of why the two vocabularies cannot collide. A string column filtered to the literal
        /// text <c>today-6d</c> is never offered to <see cref="TryParse" />, so it stores and restores
        /// that string; and within a date column no invariant-culture date format parses as
        /// <c>today</c> and no token parses as a date.
        /// </remarks>
        public static bool AppliesTo(Type declared)
        {
            ArgumentNullException.ThrowIfNull(declared);

            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            return type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly);
        }

        /// <summary>
        /// Reads the canonical text form: an anchor, an optional signed offset with a unit, and an
        /// optional <c>@end</c>.
        /// </summary>
        /// <remarks>
        /// Case-insensitive on the way in and lower-case on the way out, so <c>Today</c> written by hand
        /// in markup is understood while there stays exactly one spelling in a stored blob. Anything
        /// else - a missing unit, a bare anchor with a sign, trailing text - is not a token, and saying
        /// so by returning false is what lets a caller fall through to reading it as a date.
        /// </remarks>
        public static bool TryParse(string? text, out FastGridRelativeDate? token)
        {
            token = null;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var span = text.AsSpan().Trim();
            var endOfDay = span.EndsWith(EndSuffix, StringComparison.OrdinalIgnoreCase);

            if (endOfDay)
            {
                span = span[..^EndSuffix.Length];
            }

            if (!TryTakeAnchor(ref span, out var anchor))
            {
                return false;
            }

            if (span.IsEmpty)
            {
                token = new FastGridRelativeDate(anchor, 0, FastGridRelativeDateUnit.Days, endOfDay);

                return true;
            }

            if (!TryTakeOffset(span, out var offset, out var unit))
            {
                return false;
            }

            token = new FastGridRelativeDate(anchor, offset, unit, endOfDay);

            return true;
        }

        static bool TryTakeAnchor(ref ReadOnlySpan<char> span, out FastGridRelativeDateAnchor anchor)
        {
            foreach (var (text, value) in Anchors)
            {
                if (span.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                {
                    span = span[text.Length..];
                    anchor = value;

                    return true;
                }
            }

            anchor = default;

            return false;
        }

        // Longest first, so that a prefix of another anchor could never win. Nothing here is a prefix of
        // anything else today; the order is what keeps that from becoming load-bearing silently.
        static readonly (string Text, FastGridRelativeDateAnchor Anchor)[] Anchors =
        {
            ("month-start", FastGridRelativeDateAnchor.MonthStart),
            ("year-start", FastGridRelativeDateAnchor.YearStart),
            ("today", FastGridRelativeDateAnchor.Today),
        };

        static bool TryTakeOffset(ReadOnlySpan<char> span, out int offset, out FastGridRelativeDateUnit unit)
        {
            offset = 0;
            unit = FastGridRelativeDateUnit.Days;

            // A sign, at least one digit, and a unit - so `today-6` and `today6d` are both refused
            // rather than half-read.
            if (span.Length < 3 || (span[0] != '+' && span[0] != '-'))
            {
                return false;
            }

            unit = char.ToLowerInvariant(span[^1]) switch
            {
                'd' => FastGridRelativeDateUnit.Days,
                'm' => FastGridRelativeDateUnit.Months,
                'y' => FastGridRelativeDateUnit.Years,
                _ => (FastGridRelativeDateUnit)(-1),
            };

            if (unit == (FastGridRelativeDateUnit)(-1))
            {
                return false;
            }

            var digits = span[1..^1];

            // int.TryParse would accept a sign of its own, so the digits are checked before it reads
            // them: `today+-6d` is not a token.
            foreach (var character in digits)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
            {
                return false;
            }

            offset = span[0] == '-' ? -magnitude : magnitude;

            return true;
        }

        /// <summary>
        /// This token as a value of <paramref name="declared" />, read at <paramref name="now" />.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Typed to the column rather than always to <see cref="DateTime" />, so that what reaches
        /// <c>FilterExpression</c> and the descriptors is indistinguishable from a value somebody typed.
        /// A <see cref="DateOnly" /> has no end of day to reach, so <see cref="EndOfDay" /> is a no-op
        /// there rather than an error.
        /// </para>
        /// <para>
        /// The result's <see cref="DateTime.Kind" /> is <see cref="DateTimeKind.Unspecified" />.
        /// Comparison ignores <c>Kind</c>, but the wire does not: a <c>Local</c> instant renders an
        /// offset into a string a server then has to parse, and §33's review already measured what a
        /// misread offset costs.
        /// </para>
        /// </remarks>
        public object Resolve(Type declared, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(declared);

            var type = Nullable.GetUnderlyingType(declared) ?? declared;
            var day = Day(now.DateTime);

            if (type == typeof(DateOnly))
            {
                return DateOnly.FromDateTime(day);
            }

            var instant = EndOfDay ? EndOf(day) : day;

            // Two statements rather than a conditional, and the reason is a bug the tests caught: there
            // is an implicit DateTime-to-DateTimeOffset conversion, so `cond ? offset : instant` types
            // the whole expression as DateTimeOffset and every column - DateTime included - would be
            // handed one. It compiles, it boxes, and the cast at the far end is where it is found.
            if (type == typeof(DateTimeOffset))
            {
                // The stamp's own offset rather than the machine's: the clock the grid was given is what
                // decides which zone "today" is in, and asking TimeZoneInfo.Local here would put a
                // second answer beside it.
                return new DateTimeOffset(instant, now.Offset);
            }

            return instant;
        }

        DateTime Day(DateTime now)
        {
            var anchor = Anchor switch
            {
                FastGridRelativeDateAnchor.MonthStart =>
                    new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
                FastGridRelativeDateAnchor.YearStart =>
                    new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                _ => DateTime.SpecifyKind(now.Date, DateTimeKind.Unspecified),
            };

            // Clamped rather than thrown. An offset large enough to leave the calendar is a filter
            // nobody can have meant, and the two alternatives are worse: throwing puts an exception in
            // BuildRenderTree, which §32 established is a dead circuit, and skipping silently drops a
            // filter the user can see in the box.
            try
            {
                return Unit switch
                {
                    FastGridRelativeDateUnit.Months => anchor.AddMonths(Offset),
                    FastGridRelativeDateUnit.Years => anchor.AddYears(Offset),
                    _ => anchor.AddDays(Offset),
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return Offset < 0 ? DateTime.MinValue : DateTime.MaxValue.Date;
            }
        }

        // The last tick of the day, because Between is inclusive at both ends. Adding a day and taking
        // a tick back is exact - a DateTime is ticks - where 23:59:59.999 would leave a window.
        static DateTime EndOf(DateTime day) =>
            day >= DateTime.MaxValue.Date ? DateTime.MaxValue : day.AddDays(1).AddTicks(-1);

        /// <summary>The canonical text this token is stored as.</summary>
        public override string ToString()
        {
            var anchor = Anchor switch
            {
                FastGridRelativeDateAnchor.MonthStart => "month-start",
                FastGridRelativeDateAnchor.YearStart => "year-start",
                _ => "today",
            };

            var offset = Offset == 0
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture,
                    $"{(Offset < 0 ? '-' : '+')}{Math.Abs((long)Offset)}{UnitText}");

            return string.Concat(anchor, offset, EndOfDay ? EndSuffix : string.Empty);
        }

        char UnitText => Unit switch
        {
            FastGridRelativeDateUnit.Months => 'm',
            FastGridRelativeDateUnit.Years => 'y',
            _ => 'd',
        };

        /// <summary>
        /// Whether two tokens mean the same instant whenever they are read.
        /// </summary>
        /// <remarks>
        /// By value rather than by reference, because a declared filter value is compared against the
        /// last one to decide whether a column's parameters changed. Reference equality there would make
        /// every parameter set look like a new filter and reload the grid.
        /// </remarks>
        public bool Equals(FastGridRelativeDate? other) =>
            other is not null && Anchor == other.Anchor && Offset == other.Offset
                && Unit == other.Unit && EndOfDay == other.EndOfDay;

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as FastGridRelativeDate);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Anchor, Offset, Unit, EndOfDay);
    }

    /// <summary>Where a relative date counts from.</summary>
    /// <remarks>
    /// Three, and <c>week-start</c> is refused rather than overlooked: a week's first day is
    /// culture-dependent, and nothing on this component owns a culture decision of that kind - §32
    /// established that stored values are invariant and typed text is <c>CurrentCulture</c>, and a
    /// first-day-of-week would be a third answer with no argument behind it.
    /// </remarks>
    public enum FastGridRelativeDateAnchor
    {
        /// <summary>The current day.</summary>
        Today,

        /// <summary>The first day of the current month.</summary>
        MonthStart,

        /// <summary>The first day of the current year.</summary>
        YearStart,
    }

    /// <summary>What a relative date's offset counts.</summary>
    /// <remarks>
    /// No week: it is seven days spelled differently, and two spellings of one instant is two things a
    /// reader of a stored blob has to recognise.
    /// </remarks>
    public enum FastGridRelativeDateUnit
    {
        /// <summary>Days.</summary>
        Days,

        /// <summary>Calendar months, which clamp - 31 March less one month is 28 February.</summary>
        Months,

        /// <summary>Calendar years, which clamp on 29 February.</summary>
        Years,
    }
}
