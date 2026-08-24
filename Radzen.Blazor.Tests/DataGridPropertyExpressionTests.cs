using Bunit;
using Microsoft.AspNetCore.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radzen.Blazor.Tests
{
    public class DataGridPropertyExpressionTests
    {
        class Address
        {
            public string City { get; set; }
            public int ZipCode { get; set; }
        }

        struct Detail
        {
            public int Level { get; set; }
        }

        class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public Address Address { get; set; }
            public Detail? Detail { get; set; }
        }

        static List<Person> People() => new()
        {
            new Person { Id = 1, Name = "Charlie", Address = new Address { City = "Paris" } },
            new Person { Id = 2, Name = "Alice", Address = new Address { City = "Berlin" } },
            new Person { Id = 3, Name = "Bob", Address = new Address { City = "London" } },
        };

        static IRenderedComponent<RadzenDataGrid<Person>> RenderGrid(TestContext ctx, RenderFragment columns)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

            return ctx.RenderComponent<RadzenDataGrid<Person>>(parameterBuilder =>
            {
                parameterBuilder.Add(p => p.Data, People());
                parameterBuilder.Add(p => p.AllowSorting, true);
                parameterBuilder.Add(p => p.Columns, columns);
            });
        }

        [Fact]
        public void PropertyExpression_Renders_FlatValue()
        {
            using var ctx = new TestContext();

            var component = RenderGrid(ctx, builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Name));
                builder.CloseComponent();
            });

            var cells = component.FindAll(".rz-cell-data");
            Assert.Equal("Charlie", cells[0].TextContent.Trim());
            Assert.Equal("Alice", cells[1].TextContent.Trim());
            Assert.Equal("Bob", cells[2].TextContent.Trim());
        }

        // A null intermediate on a nested path must render an empty cell, matching the reflection-based value
        // access the compiled getter replaced - not a NullReferenceException, and not the leaf type's default
        // (e.g. "0" for an int leaf). Covers both the string Property path and the PropertyExpression path.

        static List<Person> PeopleWithNullAddress() => new()
        {
            new Person { Id = 1, Name = "Charlie", Address = new Address { City = "Paris", ZipCode = 75001 }, Detail = new Detail { Level = 7 } },
            new Person { Id = 2, Name = "NoAddress", Address = null, Detail = null },
        };

        static IRenderedComponent<RadzenDataGrid<Person>> RenderGridWith(TestContext ctx, List<Person> data, RenderFragment columns)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx.RenderComponent<RadzenDataGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.Columns, columns);
            });
        }

        [Fact]
        public void StringProperty_NestedValueTypeLeaf_NullIntermediate_RendersEmptyNotDefault()
        {
            using var ctx = new TestContext();
            var component = RenderGridWith(ctx, PeopleWithNullAddress(), builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.Property), "Address.ZipCode");
                builder.CloseComponent();
            });

            var cells = component.FindAll(".rz-cell-data");
            Assert.Equal("75001", cells[0].TextContent.Trim());
            Assert.Equal("", cells[1].TextContent.Trim()); // null Address -> empty, not "0"
        }

        [Fact]
        public void StringProperty_NullableValueTypeIntermediate_NullIntermediate_RendersEmptyNotDefault()
        {
            using var ctx = new TestContext();
            // "Detail.Level" where Detail is a Nullable<Detail> (a value type). A null Detail must render
            // empty, not the leaf int's default "0".
            var component = RenderGridWith(ctx, PeopleWithNullAddress(), builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.Property), "Detail.Level");
                builder.CloseComponent();
            });

            var cells = component.FindAll(".rz-cell-data");
            Assert.Equal("7", cells[0].TextContent.Trim());
            Assert.Equal("", cells[1].TextContent.Trim()); // null Detail -> empty, not "0"
        }

        [Fact]
        public void StringProperty_NestedReferenceLeaf_NullIntermediate_RendersEmpty()
        {
            using var ctx = new TestContext();
            var component = RenderGridWith(ctx, PeopleWithNullAddress(), builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.Property), "Address.City");
                builder.CloseComponent();
            });

            var cells = component.FindAll(".rz-cell-data");
            Assert.Equal("Paris", cells[0].TextContent.Trim());
            Assert.Equal("", cells[1].TextContent.Trim());
        }

        [Fact]
        public void PropertyExpression_NestedPath_NullIntermediate_DoesNotThrow_RendersEmpty()
        {
            using var ctx = new TestContext();
            // x => x.Address.City would NRE on a null Address if compiled as a raw lambda; the member-path
            // expression must instead route through the null-safe getter.
            var component = RenderGridWith(ctx, PeopleWithNullAddress(), builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Address.City));
                builder.CloseComponent();
                builder.OpenComponent(2, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(3, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Address.ZipCode));
                builder.CloseComponent();
            });

            var cells = component.FindAll(".rz-cell-data");
            // row 0: City, ZipCode ; row 1 (null Address): both empty
            Assert.Equal("Paris", cells[0].TextContent.Trim());
            Assert.Equal("75001", cells[1].TextContent.Trim());
            Assert.Equal("", cells[2].TextContent.Trim());
            Assert.Equal("", cells[3].TextContent.Trim());
        }

        [Fact]
        public void PropertyExpression_Renders_NestedValue()
        {
            using var ctx = new TestContext();

            var component = RenderGrid(ctx, builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Address.City));
                builder.CloseComponent();
            });

            var cells = component.FindAll(".rz-cell-data");
            Assert.Equal("Paris", cells[0].TextContent.Trim());
            Assert.Equal("Berlin", cells[1].TextContent.Trim());
            Assert.Equal("London", cells[2].TextContent.Trim());
        }

        [Fact]
        public void PropertyExpression_Derives_MemberPath()
        {
            Assert.True(RadzenDataGridColumn<Person>.TryGetMemberPath(p => p.Name, out var flat));
            Assert.Equal("Name", flat);

            Assert.True(RadzenDataGridColumn<Person>.TryGetMemberPath(p => p.Address.City, out var nested));
            Assert.Equal("Address.City", nested);

            // A boxed value type still yields the path (the compiler inserts a Convert node).
            Assert.True(RadzenDataGridColumn<Person>.TryGetMemberPath(p => p.Id, out var valueType));
            Assert.Equal("Id", valueType);

            // Not a simple member access.
            Assert.False(RadzenDataGridColumn<Person>.TryGetMemberPath(p => p.Name.Length + 1, out _));
        }

        [Fact]
        public void PropertyExpression_Populates_PipelineProperty()
        {
            using var ctx = new TestContext();

            var component = RenderGrid(ctx, builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Address.City));
                builder.CloseComponent();
            });

            // The derived member path drives the string-based sort/filter/group pipeline.
            var column = component.Instance.ColumnsCollection.Single();
            Assert.Equal("Address.City", column.Property);
            Assert.Equal("Address.City", column.GetSortProperty());
        }

        [Fact]
        public void PropertyExpression_SortsGridView_ByDerivedPath()
        {
            using var ctx = new TestContext();

            var component = RenderGrid(ctx, builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Name));
                builder.AddAttribute(2, nameof(RadzenDataGridColumn<Person>.SortOrder), SortOrder.Ascending);
                builder.CloseComponent();
            });

            // Sorting a PropertyExpression column runs through the ordinary (string-path) sort pipeline.
            var names = component.Instance.View.ToList().Select(p => p.Name).ToList();
            Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, names);
        }

        [Fact]
        public void PropertyExpression_FiltersGridView_ByDerivedPath()
        {
            using var ctx = new TestContext();

            var component = RenderGrid(ctx, builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Address.City));
                builder.AddAttribute(2, nameof(RadzenDataGridColumn<Person>.FilterValue), "London");
                builder.AddAttribute(3, nameof(RadzenDataGridColumn<Person>.FilterOperator), FilterOperator.Equals);
                builder.CloseComponent();
            });

            // Filtering a PropertyExpression column runs through the ordinary (string-path) filter pipeline.
            var cities = component.Instance.View.ToList().Select(p => p.Address.City).ToList();
            Assert.Equal(new[] { "London" }, cities);
        }

        [Fact]
        public void PropertyExpression_Computed_RendersValue_ButHasNoSortableProperty()
        {
            using var ctx = new TestContext();

            var component = RenderGrid(ctx, builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Name + " (" + p.Address.City + ")"));
                builder.CloseComponent();
            });

            // A computed expression has no member path, so it cannot drive the string sort/filter pipeline,
            // but it should still render its value in the cell.
            var cells = component.FindAll(".rz-cell-data");
            Assert.Equal("Charlie (Paris)", cells[0].TextContent.Trim());
            Assert.Equal("Alice (Berlin)", cells[1].TextContent.Trim());

            Assert.True(string.IsNullOrEmpty(component.Instance.ColumnsCollection.Single().Property));
        }

        [Fact]
        public void PropertyExpression_IsIgnored_WhenStringPropertySet()
        {
            using var ctx = new TestContext();

            var component = RenderGrid(ctx, builder =>
            {
                builder.OpenComponent(0, typeof(RadzenDataGridColumn<Person>));
                builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.Property), "Name");
                builder.AddAttribute(2, nameof(RadzenDataGridColumn<Person>.PropertyExpression),
                    (System.Linq.Expressions.Expression<Func<Person, object>>)(p => p.Address.City));
                builder.CloseComponent();
            });

            var column = component.Instance.ColumnsCollection.Single();
            Assert.Equal("Name", column.Property);

            var cells = component.FindAll(".rz-cell-data");
            Assert.Equal("Charlie", cells[0].TextContent.Trim());
        }
    }
}
