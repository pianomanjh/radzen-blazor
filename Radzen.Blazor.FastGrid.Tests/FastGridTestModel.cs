using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid.Tests
{
    /// <summary>A row type with one property of every shape the column model has to cope with.</summary>
    public class Person
    {
        public int Id { get; set; }

        public string First { get; set; }

        public string Last { get; set; }

        public decimal Salary { get; set; }

        /// <summary>Nullable, so the format path for <c>Nullable&lt;T&gt;</c> has something to bind to.</summary>
        public decimal? Bonus { get; set; }

        public DateTime Hired { get; set; }

        public Company Customer { get; set; }

        /// <summary>A collection-valued property, which a column lists rather than stringifies.</summary>
        public List<string> Regions { get; set; }

        /// <summary>The same, of a value type, so the element type decides the filter operator.</summary>
        public int[] Codes { get; set; }

        /// <summary>A collection of objects, which is what FilterProperty exists for.</summary>
        public List<Company> Accounts { get; set; }
    }

    public class Company
    {
        public string Name { get; set; }
    }

    /// <summary>
    /// Column declarations for tests. Building the fragment by hand rather than in Razor keeps the test
    /// project a plain xunit assembly, matching Radzen.Blazor.Tests.
    /// </summary>
    public static class Columns
    {
        /// <summary>
        /// Composes column declarations into a single <see cref="RenderFragment" />, giving each one its
        /// own sequence-number region so the renderer treats them as distinct siblings.
        /// </summary>
        public static RenderFragment Of(params Action<RenderTreeBuilder, int>[] declarations) => builder =>
        {
            for (var i = 0; i < declarations.Length; i++)
            {
                builder.OpenRegion(i);
                declarations[i](builder, 0);
                builder.CloseRegion();
            }
        };

        public static Action<RenderTreeBuilder, int> Property<TItem, TProp>(
            Expression<Func<TItem, TProp>> property,
            string title = null,
            string format = null,
            Expression<Func<TItem, TProp>> sortBy = null,
            bool sortable = true,
            string cssClass = null,
            object filterValue = null,
            FilterOperator? filterOperator = null,
            Expression<Func<TItem, TProp>> filterBy = null,
            bool filterable = true,
            RenderFragment<ColumnBase<TItem>> filterTemplate = null,
            string separator = null,
            Expression<Func<TItem, TProp>> sortByPath = null,
            FilterMode? filterMode = null,
            System.Collections.IEnumerable filterLookupData = null,
            string filterProperty = null) => (builder, seq) =>
        {
            builder.OpenComponent<PropertyColumn<TItem, TProp>>(seq);
            builder.AddAttribute(seq + 1, nameof(PropertyColumn<TItem, TProp>.Property), property);

            if (title is not null)
            {
                builder.AddAttribute(seq + 2, nameof(PropertyColumn<TItem, TProp>.Title), title);
            }

            if (format is not null)
            {
                builder.AddAttribute(seq + 3, nameof(PropertyColumn<TItem, TProp>.Format), format);
            }

            if (sortBy is not null)
            {
                builder.AddAttribute(seq + 4, nameof(PropertyColumn<TItem, TProp>.SortBy), sortBy);
            }

            if (!sortable)
            {
                builder.AddAttribute(seq + 5, nameof(PropertyColumn<TItem, TProp>.Sortable), false);
            }

            if (cssClass is not null)
            {
                builder.AddAttribute(seq + 6, nameof(PropertyColumn<TItem, TProp>.CssClass), cssClass);
            }

            if (filterValue is not null)
            {
                builder.AddAttribute(seq + 7, nameof(PropertyColumn<TItem, TProp>.FilterValue), filterValue);
            }

            if (filterOperator is not null)
            {
                builder.AddAttribute(seq + 8, nameof(PropertyColumn<TItem, TProp>.FilterOperator), filterOperator);
            }

            if (filterBy is not null)
            {
                builder.AddAttribute(seq + 9, nameof(PropertyColumn<TItem, TProp>.FilterBy), filterBy);
            }

            if (!filterable)
            {
                builder.AddAttribute(seq + 10, nameof(PropertyColumn<TItem, TProp>.Filterable), false);
            }

            if (filterTemplate is not null)
            {
                builder.AddAttribute(seq + 11, nameof(PropertyColumn<TItem, TProp>.FilterTemplate), filterTemplate);
            }

            if (separator is not null)
            {
                builder.AddAttribute(seq + 12, nameof(PropertyColumn<TItem, TProp>.Separator), separator);
            }

            if (sortByPath is not null)
            {
                builder.AddAttribute(seq + 13, nameof(PropertyColumn<TItem, TProp>.SortBy), sortByPath);
            }

            if (filterMode is not null)
            {
                builder.AddAttribute(seq + 14, nameof(PropertyColumn<TItem, TProp>.FilterMode), filterMode);
            }

            if (filterLookupData is not null)
            {
                builder.AddAttribute(seq + 15, nameof(PropertyColumn<TItem, TProp>.FilterLookupData), filterLookupData);
            }

            if (filterProperty is not null)
            {
                builder.AddAttribute(seq + 16, nameof(PropertyColumn<TItem, TProp>.FilterProperty), filterProperty);
            }

            builder.CloseComponent();
        };

        public static Action<RenderTreeBuilder, int> Template<TItem>(
            RenderFragment<TItem> template,
            string title = null,
            string sortProperty = null,
            bool sortable = true) => (builder, seq) =>
        {
            builder.OpenComponent<TemplateColumn<TItem>>(seq);

            if (template is not null)
            {
                builder.AddAttribute(seq + 1, nameof(TemplateColumn<TItem>.Template), template);
            }

            if (title is not null)
            {
                builder.AddAttribute(seq + 2, nameof(TemplateColumn<TItem>.Title), title);
            }

            if (sortProperty is not null)
            {
                builder.AddAttribute(seq + 3, nameof(TemplateColumn<TItem>.SortProperty), sortProperty);
            }

            if (!sortable)
            {
                builder.AddAttribute(seq + 4, nameof(TemplateColumn<TItem>.Sortable), false);
            }

            builder.CloseComponent();
        };
    }

    public static class People
    {
        /// <summary>
        /// Four rows whose orderings by name, id, salary and hire date all differ, so a test that sorts by
        /// one column cannot pass because the data happened to be ordered by another.
        /// </summary>
        public static List<Person> Sample() => new()
        {
            new Person
            {
                Id = 3, First = "Carol", Last = "Adams", Salary = 4000m, Bonus = 250.5m,
                Hired = new DateTime(2019, 5, 4), Customer = new Company { Name = "Zeta" },
                Regions = new() { "North", "West" }, Codes = new[] { 10, 20 },
                Accounts = new() { new() { Name = "Acme" }, new() { Name = "Globex" } }
            },
            new Person
            {
                Id = 1, First = "Alice", Last = "Draper", Salary = 2000m, Bonus = null,
                Hired = new DateTime(2021, 1, 2), Customer = new Company { Name = "Yankee" },
                Regions = new() { "South" }, Codes = new[] { 20 },
                Accounts = new() { new() { Name = "Initech" } }
            },
            new Person
            {
                Id = 4, First = "Dave", Last = "Bell", Salary = 1000m, Bonus = 10m,
                Hired = new DateTime(2018, 11, 30), Customer = new Company { Name = "Xray" },
                Regions = new(), Codes = System.Array.Empty<int>(),
                Accounts = new()
            },
            new Person
            {
                Id = 2, First = "Bob", Last = "Cook", Salary = 3000m, Bonus = 99.25m,
                Hired = new DateTime(2020, 7, 15), Customer = new Company { Name = "Whisky" },
                Regions = new() { "North", "East", "South" }, Codes = new[] { 30 },
                Accounts = new() { new() { Name = "Acme" }, new() { Name = "Umbrella" } }
            },
        };

        public static List<Person> Many(int count) => Enumerable.Range(1, count)
            .Select(i => new Person
            {
                Id = 100000 + i,
                First = "First" + i,
                Last = "Last" + i,
                Salary = i * 10m,
                Bonus = i % 3 == 0 ? null : (decimal?)(i * 1.5m),
                Hired = new DateTime(2020, 1, 1).AddDays(i),
                Customer = new Company { Name = "Company" + i },
                Regions = new() { "Region" + i },
                Codes = new[] { i },
                Accounts = new() { new Company { Name = "Account" + i } },
            })
            .ToList();
    }
}
