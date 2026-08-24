using Bunit;
using Microsoft.AspNetCore.Components;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radzen.Blazor.Tests
{
    public class DataGridFilterOperatorCacheTests
    {
        class Item
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        static RadzenDataGridColumn<Item> Column(TestContext ctx, string property)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

            var grid = ctx.RenderComponent<RadzenDataGrid<Item>>(p =>
            {
                p.Add(g => g.Data, new List<Item> { new() { Name = "a", Count = 1 } });
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.Columns, b =>
                {
                    b.OpenComponent(0, typeof(RadzenDataGridColumn<Item>));
                    b.AddAttribute(1, nameof(RadzenDataGridColumn<Item>.Property), property);
                    b.CloseComponent();
                });
            });

            return grid.Instance.ColumnsCollection.Single();
        }

        [Fact]
        public void GetFilterOperators_IsMemoized_AndReturnsCorrectOperators()
        {
            using var ctx = new TestContext();
            var column = Column(ctx, "Name");

            var first = column.GetFilterOperators();
            var second = column.GetFilterOperators();

            // Same materialized instance reused across calls within a render.
            Assert.Same(first, second);

            // String columns expose the string operators.
            Assert.Contains(FilterOperator.Contains, first);
            Assert.Contains(FilterOperator.StartsWith, first);
            Assert.DoesNotContain(FilterOperator.Custom, first);
        }

        [Fact]
        public void GetFilterOperators_DiffersByColumnType()
        {
            using var ctx = new TestContext();
            var stringOps = Column(ctx, "Name").GetFilterOperators().ToList();

            using var ctx2 = new TestContext();
            var numericOps = Column(ctx2, "Count").GetFilterOperators().ToList();

            // Numeric columns get comparison operators but not string ones.
            Assert.Contains(FilterOperator.GreaterThan, numericOps);
            Assert.DoesNotContain(FilterOperator.Contains, numericOps);
            Assert.Contains(FilterOperator.Contains, stringOps);
        }
    }
}
