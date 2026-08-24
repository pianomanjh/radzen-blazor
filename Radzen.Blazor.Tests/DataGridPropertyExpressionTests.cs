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
        }

        class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public Address Address { get; set; }
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
