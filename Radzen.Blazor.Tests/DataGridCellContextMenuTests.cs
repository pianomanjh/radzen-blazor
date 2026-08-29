using Bunit;
using Microsoft.AspNetCore.Components;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radzen.Blazor.Tests
{
    // RenderCell writes the <td> two ways - with the oncontextmenu modifiers when CellContextMenu has a
    // handler, without them otherwise, since Razor emits an event-modifier attribute whichever way its
    // expression evaluates and that costs real allocation on every cell. These cover both branches.
    public class DataGridCellContextMenuTests
    {
        class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        static List<Person> People(int n) =>
            Enumerable.Range(1, n).Select(i => new Person { Id = i, Name = "Person " + i }).ToList();

        static IRenderedComponent<RadzenDataGrid<Person>> RenderGrid(TestContext ctx,
            EventCallback<DataGridCellMouseEventArgs<Person>>? contextMenu)
        {
            return ctx.RenderComponent<RadzenDataGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People(3));

                if (contextMenu.HasValue)
                {
                    p.Add(g => g.CellContextMenu, contextMenu.Value);
                }

                p.Add<RenderFragment>(g => g.Columns, builder =>
                {
                    builder.OpenComponent<RadzenDataGridColumn<Person>>(0);
                    builder.AddAttribute(1, nameof(RadzenDataGridColumn<Person>.Property), nameof(Person.Id));
                    builder.CloseComponent();
                    builder.OpenComponent<RadzenDataGridColumn<Person>>(2);
                    builder.AddAttribute(3, nameof(RadzenDataGridColumn<Person>.Property), nameof(Person.Name));
                    builder.CloseComponent();
                });
            });
        }

        [Fact]
        public void DataGrid_CellContextMenu_FiresWithCellAndItem()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

            DataGridCellMouseEventArgs<Person> received = null;
            var component = RenderGrid(ctx, EventCallback.Factory.Create<DataGridCellMouseEventArgs<Person>>(
                this, args => received = args));

            var cells = component.FindAll("td");
            Assert.NotEmpty(cells);

            cells[0].ContextMenu();

            Assert.NotNull(received);
            Assert.Equal(1, received.Data.Id);
            Assert.Equal(nameof(Person.Id), received.Column.Property);
        }

        [Fact]
        public void DataGrid_WithoutCellContextMenu_RendersTheSameCells()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

            var withHandler = RenderGrid(ctx, EventCallback.Factory.Create<DataGridCellMouseEventArgs<Person>>(
                this, _ => { }));
            var withoutHandler = RenderGrid(ctx, null);

            // The two <td> branches must produce identical cell content and classes; only the internal
            // event-modifier attributes differ, and those are not part of the rendered text.
            string CellText(IRenderedComponent<RadzenDataGrid<Person>> c) =>
                string.Join("|", c.FindAll("td").Select(td => td.TextContent.Trim()));

            Assert.Equal(CellText(withHandler), CellText(withoutHandler));
            Assert.Contains("Person 1", withoutHandler.Markup);
            Assert.Contains("Person 3", withoutHandler.Markup);
        }
    }
}
