using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The typed filter builder against the reflective one it replaces, row for row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FilterExpression</c> exists because <c>QueryableExtension.Where</c> reaches members by name and
    /// closes generic methods at run time, which is correct and invisible to an ahead-of-time compiler.
    /// Replacing it is only worth doing if the replacement filters identically, and "identically" here
    /// covers a lot of behaviour nobody wrote down: what a null string compares as, which case-folding a
    /// given source gets, what happens to a value that will not convert.
    /// </para>
    /// <para>
    /// So these tests do not assert what the rows should be. They run the same filter through both
    /// builders over the same data and require the same rows out - which pins the incumbent's behaviour
    /// including the parts of it nobody intended.
    /// </para>
    /// </remarks>
    public class FilterExpressionParityTests
    {
        static readonly List<Person> People = Sample();

        static List<Person> Sample()
        {
            var people = Radzen.FastGrid.Tests.People.Many(12);

            // The shapes the two builders could disagree about: a null string, an empty one, one that
            // differs only by case, a null number, and both ends of the enum.
            people[0].Last = null;
            people[1].Last = string.Empty;
            people[2].Last = "LAST3";
            people[3].Last = "last4";
            people[4].Bonus = null;
            people[5].Grade = Grade.Senior;

            return people;
        }

        /// <summary>The rows the reflective builder keeps, which is the answer to match.</summary>
        static int[] Reflective(string path, FilterOperator op, object value, Type type,
            FilterCaseSensitivity sensitivity, bool inMemory)
        {
            var filters = new[]
            {
                new FilterDescriptor
                {
                    Property = path,
                    FilterOperator = op,
                    FilterValue = value,
                    Type = type,
                },
            };

            return Ids(Source(inMemory).Where(filters, LogicalFilterOperator.And, sensitivity));
        }

        /// <summary>The rows the typed builder keeps.</summary>
        static int[] Typed<TProp>(Expression<Func<Person, TProp>> selector, FilterOperator op, object value,
            FilterCaseSensitivity sensitivity, bool inMemory)
        {
            var predicate = FilterExpression<Person, TProp>.For(selector, op, value, sensitivity, inMemory);

            Assert.NotNull(predicate);

            return Ids(Source(inMemory).Where(predicate));
        }

        // Ordered outside the provider: the stand-in below is deliberately not an IOrderedQueryable,
        // which is the whole of what makes QueryableExtension treat it as a database.
        static int[] Ids(IQueryable<Person> source) =>
            source.Select(p => p.Id).AsEnumerable().OrderBy(id => id).ToArray();

        // An EnumerableQuery is what QueryableExtension recognises as in-memory, and a queryable whose
        // provider it does not recognise stands in for a database - the two take different string paths,
        // and both have to agree.
        static IQueryable<Person> Source(bool inMemory) =>
            inMemory ? People.AsQueryable() : new NotEnumerableQuery<Person>(People.AsQueryable());

        /// <summary>The rows the delegate builder keeps, which only ever runs over an in-memory source.</summary>
        static int[] Composed<TProp>(Expression<Func<Person, TProp>> selector, FilterOperator op,
            object value, FilterCaseSensitivity sensitivity)
        {
            var predicate = FilterExpression<Person, TProp>.PredicateFor(selector.Compile(), op, value,
                sensitivity);

            Assert.NotNull(predicate);

            return People.Where(predicate).Select(p => p.Id).OrderBy(id => id).ToArray();
        }

        static void Same<TProp>(Expression<Func<Person, TProp>> selector, string path, FilterOperator op,
            object value, FilterCaseSensitivity sensitivity = FilterCaseSensitivity.Default,
            bool inMemory = true)
        {
            var expected = Reflective(path, op, value, typeof(TProp), sensitivity, inMemory);

            Assert.Equal(expected, Typed(selector, op, value, sensitivity, inMemory));

            // The delegate builder is only ever used over an in-memory source, so it is only compared
            // where the expression builder is answering for one too. Three builders, one answer.
            if (inMemory)
            {
                Assert.Equal(expected, Composed(selector, op, value, sensitivity));
            }
        }

        // --- strings ----------------------------------------------------------------------------

        [Theory]
        [InlineData(FilterOperator.Equals)]
        [InlineData(FilterOperator.NotEquals)]
        [InlineData(FilterOperator.Contains)]
        [InlineData(FilterOperator.DoesNotContain)]
        [InlineData(FilterOperator.StartsWith)]
        [InlineData(FilterOperator.EndsWith)]
        [InlineData(FilterOperator.IsNull)]
        [InlineData(FilterOperator.IsNotNull)]
        [InlineData(FilterOperator.IsEmpty)]
        [InlineData(FilterOperator.IsNotEmpty)]
        public void OnAStringColumn(FilterOperator op) => Same(p => p.Last, nameof(Person.Last), op, "Last3");

        [Theory]
        [InlineData(FilterOperator.Equals, true)]
        [InlineData(FilterOperator.Contains, true)]
        [InlineData(FilterOperator.StartsWith, true)]
        [InlineData(FilterOperator.EndsWith, true)]
        [InlineData(FilterOperator.NotEquals, true)]
        [InlineData(FilterOperator.DoesNotContain, true)]
        [InlineData(FilterOperator.Equals, false)]
        [InlineData(FilterOperator.Contains, false)]
        [InlineData(FilterOperator.StartsWith, false)]
        [InlineData(FilterOperator.EndsWith, false)]
        [InlineData(FilterOperator.NotEquals, false)]
        [InlineData(FilterOperator.DoesNotContain, false)]
        public void IgnoringCase(FilterOperator op, bool inMemory) =>
            Same(p => p.Last, nameof(Person.Last), op, "last3",
                FilterCaseSensitivity.CaseInsensitive, inMemory);

        // The value nobody has. Both builders have to keep nothing rather than differ about it.
        [Theory]
        [InlineData(FilterOperator.Equals)]
        [InlineData(FilterOperator.Contains)]
        [InlineData(FilterOperator.StartsWith)]
        public void OnAStringNothingMatches(FilterOperator op) =>
            Same(p => p.Last, nameof(Person.Last), op, "no such person");

        // --- numbers ----------------------------------------------------------------------------

        [Theory]
        [InlineData(FilterOperator.Equals)]
        [InlineData(FilterOperator.NotEquals)]
        [InlineData(FilterOperator.LessThan)]
        [InlineData(FilterOperator.LessThanOrEquals)]
        [InlineData(FilterOperator.GreaterThan)]
        [InlineData(FilterOperator.GreaterThanOrEquals)]
        public void OnAnIntColumn(FilterOperator op) => Same(p => p.Id, nameof(Person.Id), op, 100006);

        [Theory]
        [InlineData(FilterOperator.Equals)]
        [InlineData(FilterOperator.LessThan)]
        [InlineData(FilterOperator.GreaterThanOrEquals)]
        public void OnADecimalColumn(FilterOperator op) =>
            Same(p => p.Salary, nameof(Person.Salary), op, 60m);

        // Nullable, and the sample has nulls in it - the case where a comparison and a null-check have
        // to agree about rows that have no value at all.
        [Theory]
        [InlineData(FilterOperator.Equals)]
        [InlineData(FilterOperator.NotEquals)]
        [InlineData(FilterOperator.LessThan)]
        [InlineData(FilterOperator.GreaterThan)]
        [InlineData(FilterOperator.IsNull)]
        [InlineData(FilterOperator.IsNotNull)]
        public void OnANullableDecimalColumn(FilterOperator op) =>
            Same(p => p.Bonus, nameof(Person.Bonus), op, 9m);

        // --- dates, enums and guids -------------------------------------------------------------

        [Theory]
        [InlineData(FilterOperator.Equals)]
        [InlineData(FilterOperator.LessThan)]
        [InlineData(FilterOperator.GreaterThan)]
        [InlineData(FilterOperator.GreaterThanOrEquals)]
        public void OnADateColumn(FilterOperator op) =>
            Same(p => p.Hired, nameof(Person.Hired), op, new DateTime(2020, 1, 7));

        [Theory]
        [InlineData(FilterOperator.Equals)]
        [InlineData(FilterOperator.NotEquals)]
        public void OnAnEnumColumn(FilterOperator op) =>
            Same(p => p.Grade, nameof(Person.Grade), op, Grade.Senior);

        // --- In and NotIn -----------------------------------------------------------------------

        [Theory]
        [InlineData(FilterOperator.In)]
        [InlineData(FilterOperator.NotIn)]
        public void OnAListOfStrings(FilterOperator op) =>
            Same(p => p.Last, nameof(Person.Last), op, new object[] { "Last3", "Last5" });

        [Theory]
        [InlineData(FilterOperator.In)]
        [InlineData(FilterOperator.NotIn)]
        public void OnAListOfInts(FilterOperator op) =>
            Same(p => p.Id, nameof(Person.Id), op, new object[] { 100003, 100005 });

        [Theory]
        [InlineData(FilterOperator.In)]
        [InlineData(FilterOperator.NotIn)]
        public void OnAListOfNullableDecimals(FilterOperator op) =>
            Same(p => p.Bonus, nameof(Person.Bonus), op, new object[] { 1.5m, 3.0m });

        // Nothing ticked. Not a filter that matches nothing - no filter at all.
        [Theory]
        [InlineData(FilterOperator.In)]
        [InlineData(FilterOperator.NotIn)]
        public void OnAnEmptyList(FilterOperator op) =>
            Same(p => p.Last, nameof(Person.Last), op, Array.Empty<object>());

        /// <summary>
        /// A queryable that is not an <c>EnumerableQuery</c>, so QueryableExtension treats it as a
        /// provider and builds the ToLower comparison rather than the OrdinalIgnoreCase one - while
        /// still executing in memory, so the two builders can be compared on the same rows.
        /// </summary>
        sealed class NotEnumerableQuery<T> : IQueryable<T>
        {
            readonly IQueryable<T> inner;

            public NotEnumerableQuery(IQueryable<T> inner) => this.inner = inner;

            public Type ElementType => inner.ElementType;

            public Expression Expression => inner.Expression;

            public IQueryProvider Provider => new NotEnumerableProvider(inner.Provider);

            public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            sealed class NotEnumerableProvider : IQueryProvider
            {
                readonly IQueryProvider inner;

                public NotEnumerableProvider(IQueryProvider inner) => this.inner = inner;

                public IQueryable CreateQuery(Expression expression) => inner.CreateQuery(expression);

                public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
                    new NotEnumerableQuery<TElement>(inner.CreateQuery<TElement>(expression));

                public object Execute(Expression expression) => inner.Execute(expression);

                public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
            }
        }
    }
}
