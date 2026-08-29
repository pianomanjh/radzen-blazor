using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radzen.Blazor.Tests
{
    /// <summary>
    /// Filtering a collection-valued property matches a row when any member matches. An array is a
    /// collection like any other; it was previously left with no item type, because the item type was
    /// read only from generic arguments, and the filter was then built against the array itself.
    /// </summary>
    public class QueryableExtensionArrayFilterTests
    {
        class Person
        {
            public string Name { get; set; }

            public string[] Regions { get; set; }

            public int[] Codes { get; set; }

            public List<string> Tags { get; set; }
        }

        static IQueryable<Person> People() => new[]
        {
            new Person { Name = "Carol", Regions = new[] { "North", "West" }, Codes = new[] { 10, 20 }, Tags = new() { "North" } },
            new Person { Name = "Alice", Regions = new[] { "South" }, Codes = new[] { 20 }, Tags = new() { "South" } },
            new Person { Name = "Dave", Regions = System.Array.Empty<string>(), Codes = System.Array.Empty<int>(), Tags = new() },
        }.AsQueryable();

        static string[] Names(IQueryable<Person> source, FilterDescriptor filter) => source
            .Where(new[] { filter }, LogicalFilterOperator.And, FilterCaseSensitivity.Default)
            .Select(p => p.Name)
            .ToArray();

        [Fact]
        public void AStringArrayFiltersOnSubstringsOfItsMembers()
        {
            Assert.Equal(new[] { "Carol" }, Names(People(), new FilterDescriptor
            {
                Property = nameof(Person.Regions),
                FilterValue = "ort",
                FilterOperator = FilterOperator.Contains,
                Type = typeof(string[]),
            }));
        }

        [Fact]
        public void AnIntArrayFiltersOnEqualMembers()
        {
            // Previously threw: the binary operator Equal is not defined for Int32[] and Int32.
            Assert.Equal(new[] { "Carol", "Alice" }, Names(People(), new FilterDescriptor
            {
                Property = nameof(Person.Codes),
                FilterValue = 20,
                FilterOperator = FilterOperator.Equals,
                Type = typeof(int[]),
            }));
        }

        [Fact]
        public void AnEmptyArrayMatchesNothing()
        {
            Assert.DoesNotContain("Dave", Names(People(), new FilterDescriptor
            {
                Property = nameof(Person.Regions),
                FilterValue = "o",
                FilterOperator = FilterOperator.Contains,
                Type = typeof(string[]),
            }));
        }

        [Fact]
        public void AGenericListStillBehavesAsBefore()
        {
            Assert.Equal(new[] { "Carol" }, Names(People(), new FilterDescriptor
            {
                Property = nameof(Person.Tags),
                FilterValue = "North",
                FilterOperator = FilterOperator.Contains,
                Type = typeof(List<string>),
            }));
        }
    }
}
