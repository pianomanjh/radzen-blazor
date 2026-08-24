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
