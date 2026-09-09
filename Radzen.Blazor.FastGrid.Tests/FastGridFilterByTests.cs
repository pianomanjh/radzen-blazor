using System;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Filtering by a key the column is not typed at, through <see cref="FastGridFilterBy{TItem}" />.
    /// </summary>
    /// <remarks>
    /// The filter counterpart of <see cref="FastGridSort{TItem}" />, and it exists for the same reason a
    /// sort carrier does: <see cref="TemplateColumn{TItem}" /> has no expression of its own. Until this,
    /// a template column could be told how to sort and not how to filter, so
    /// <c>ColumnBase.CanFilter</c> — which reads <c>FilterPropertyPath</c> — was false for one by
    /// construction.
    /// </remarks>
    public class FastGridFilterByTests
    {
        [Fact]
        public void ACarrierReportsThePathAndTypeOfItsKey()
        {
            var filterBy = FastGridFilterBy<Person>.By(p => p.First);

            Assert.Equal("First", filterBy.Path);
            Assert.Equal(typeof(string), filterBy.PropertyType);
        }

        [Fact]
        public void ACarrierReportsADottedPathForAMemberChain()
        {
            var filterBy = FastGridFilterBy<Person>.By(p => p.Customer.Name);

            Assert.Equal("Customer.Name", filterBy.Path);
            Assert.Equal(typeof(string), filterBy.PropertyType);
        }

        /// <summary>
        /// A computed key has no path, which is what <see cref="FastGridSort{TItem}.Path" /> already
        /// says of a sort — and here it carries a consequence a sort does not have: the path is what
        /// <c>CanFilter</c> reads, so a column filtering by a computed key does not filter at all.
        /// </summary>
        [Fact]
        public void AComputedKeyHasNoPath()
        {
            var filterBy = FastGridFilterBy<Person>.By(p => p.First + p.Last);

            Assert.Null(filterBy.Path);
            Assert.Equal(typeof(string), filterBy.PropertyType);
        }

        [Fact]
        public void TheKeysTypeIsCapturedRatherThanErased()
        {
            Assert.Equal(typeof(decimal), FastGridFilterBy<Person>.By(p => p.Salary).PropertyType);
            Assert.Equal(typeof(decimal?), FastGridFilterBy<Person>.By(p => p.Bonus).PropertyType);
            Assert.Equal(typeof(DateTime), FastGridFilterBy<Person>.By(p => p.Hired).PropertyType);
            Assert.Equal(typeof(Grade), FastGridFilterBy<Person>.By(p => p.Grade).PropertyType);
        }

        [Fact]
        public void ANullKeyIsRefusedAtTheFactory()
        {
            Assert.Throws<ArgumentNullException>(() => FastGridFilterBy<Person>.By<string>(null));
        }
    }
}
