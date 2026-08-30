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

        /// <summary>An enum, which does not convert from a string through IConvertible.</summary>
        public Grade Grade { get; set; }

        /// <summary>A Guid, which does not either.</summary>
        public Guid Reference { get; set; }

        /// <summary>
        /// Declared as object and holding values of more than one type, which is what a lookup has to
        /// cope with: the first value being comparable says nothing about the rest.
        /// </summary>
        public object Mixed { get; set; }
    }

    public enum Grade
    {
        Junior,
        Senior,
    }

    public class Company
    {
        public string Name { get; set; }

        /// <summary>A second member, so a test can filter on one and display the other.</summary>
        public string Region { get; set; }

        /// <summary>A value-typed member, which a selector returning object wraps in a Convert.</summary>
        public int Size { get; set; }
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
            string width = null,
            string minWidth = null,
            string maxWidth = null,
            TextAlign? textAlign = null,
            Radzen.Blazor.WhiteSpace? whiteSpace = null,
            bool visible = true,
            int? orderIndex = null,
            SortOrder? sortOrder = null,
            RenderFragment<ColumnBase<TItem>> headerTemplate = null,
            RenderFragment<ColumnBase<TItem>> footerTemplate = null,
            string footerCssClass = null) => (builder, seq) =>
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

            if (width is not null)
            {
                builder.AddAttribute(seq + 16, nameof(PropertyColumn<TItem, TProp>.Width), width);
            }

            if (minWidth is not null)
            {
                builder.AddAttribute(seq + 17, nameof(PropertyColumn<TItem, TProp>.MinWidth), minWidth);
            }

            if (maxWidth is not null)
            {
                builder.AddAttribute(seq + 18, nameof(PropertyColumn<TItem, TProp>.MaxWidth), maxWidth);
            }

            if (textAlign is not null)
            {
                builder.AddAttribute(seq + 19, nameof(PropertyColumn<TItem, TProp>.TextAlign), textAlign.Value);
            }

            if (whiteSpace is not null)
            {
                builder.AddAttribute(seq + 20, nameof(PropertyColumn<TItem, TProp>.WhiteSpace), whiteSpace.Value);
            }

            if (!visible)
            {
                builder.AddAttribute(seq + 21, nameof(PropertyColumn<TItem, TProp>.Visible), false);
            }

            if (orderIndex is not null)
            {
                builder.AddAttribute(seq + 22, nameof(PropertyColumn<TItem, TProp>.OrderIndex), orderIndex);
            }

            if (sortOrder is not null)
            {
                builder.AddAttribute(seq + 23, nameof(PropertyColumn<TItem, TProp>.SortOrder), sortOrder);
            }

            if (headerTemplate is not null)
            {
                builder.AddAttribute(seq + 24, nameof(PropertyColumn<TItem, TProp>.HeaderTemplate), headerTemplate);
            }

            if (footerTemplate is not null)
            {
                builder.AddAttribute(seq + 25, nameof(PropertyColumn<TItem, TProp>.FooterTemplate), footerTemplate);
            }

            if (footerCssClass is not null)
            {
                builder.AddAttribute(seq + 26, nameof(PropertyColumn<TItem, TProp>.FooterCssClass), footerCssClass);
            }

            builder.CloseComponent();
        };

        public static Action<RenderTreeBuilder, int> Collection<TItem, TElement>(
            Expression<Func<TItem, IEnumerable<TElement>>> property,
            Expression<Func<TElement, object>> displayProperty = null,
            Expression<Func<TElement, object>> filterProperty = null,
            Expression<Func<TItem, object>> sortBy = null,
            string title = null,
            string format = null,
            string separator = null,
            FilterMode? filterMode = null,
            bool filterable = true,
            object filterValue = null) => (builder, seq) =>
        {
            builder.OpenComponent<CollectionColumn<TItem, TElement>>(seq);
            builder.AddAttribute(seq + 1, nameof(CollectionColumn<TItem, TElement>.Property), property);

            if (displayProperty is not null)
            {
                builder.AddAttribute(seq + 2, nameof(CollectionColumn<TItem, TElement>.DisplayProperty), displayProperty);
            }

            if (filterProperty is not null)
            {
                builder.AddAttribute(seq + 3, nameof(CollectionColumn<TItem, TElement>.FilterProperty), filterProperty);
            }

            if (sortBy is not null)
            {
                builder.AddAttribute(seq + 4, nameof(CollectionColumn<TItem, TElement>.SortBy), sortBy);
            }

            if (title is not null)
            {
                builder.AddAttribute(seq + 5, nameof(CollectionColumn<TItem, TElement>.Title), title);
            }

            if (format is not null)
            {
                builder.AddAttribute(seq + 6, nameof(CollectionColumn<TItem, TElement>.Format), format);
            }

            if (separator is not null)
            {
                builder.AddAttribute(seq + 7, nameof(CollectionColumn<TItem, TElement>.Separator), separator);
            }

            if (filterMode is not null)
            {
                builder.AddAttribute(seq + 8, nameof(CollectionColumn<TItem, TElement>.FilterMode), filterMode);
            }

            if (!filterable)
            {
                builder.AddAttribute(seq + 9, nameof(CollectionColumn<TItem, TElement>.Filterable), false);
            }

            if (filterValue is not null)
            {
                builder.AddAttribute(seq + 10, nameof(CollectionColumn<TItem, TElement>.FilterValue), filterValue);
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
                Grade = Grade.Senior, Reference = Reference(3), Id = 3, First = "Carol", Mixed = 3, Last = "Adams", Salary = 4000m, Bonus = 250.5m,
                Hired = new DateTime(2019, 5, 4), Customer = new Company { Name = "Zeta" },
                Regions = new() { "North", "West" }, Codes = new[] { 10, 20 },
                Accounts = new() { new() { Name = "Acme", Region = "North", Size = 10 }, new() { Name = "Globex", Region = "West", Size = 20 } }
            },
            new Person
            {
                Grade = Grade.Junior, Reference = Reference(1), Id = 1, First = "Alice", Mixed = "n/a", Last = "Draper", Salary = 2000m, Bonus = null,
                Hired = new DateTime(2021, 1, 2), Customer = new Company { Name = "Yankee" },
                Regions = new() { "South" }, Codes = new[] { 20 },
                Accounts = new() { new() { Name = "Initech", Region = "South", Size = 30 } }
            },
            new Person
            {
                Grade = Grade.Junior, Reference = Reference(4), Id = 4, First = "Dave", Mixed = 4, Last = "Bell", Salary = 1000m, Bonus = 10m,
                Hired = new DateTime(2018, 11, 30), Customer = new Company { Name = "Xray" },
                Regions = new(), Codes = System.Array.Empty<int>(),
                Accounts = new()
            },
            new Person
            {
                Grade = Grade.Senior, Reference = Reference(2), Id = 2, First = "Bob", Mixed = 2, Last = "Cook", Salary = 3000m, Bonus = 99.25m,
                Hired = new DateTime(2020, 7, 15), Customer = new Company { Name = "Whisky" },
                Regions = new() { "North", "East", "South" }, Codes = new[] { 30 },
                Accounts = new() { new() { Name = "Acme", Region = "East", Size = 10 }, new() { Name = "Umbrella", Region = "North", Size = 40 } }
            },
        };

        /// <summary>A Guid that depends only on the row, so a test can name one without hard-coding it.</summary>
        public static Guid Reference(int id) => new Guid(id, 0, 0, new byte[8]);

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
                Accounts = new() { new Company { Name = "Account" + i, Region = "Region" + i } },
            })
            .ToList();
    }
}
