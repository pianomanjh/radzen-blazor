using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>§39's band half: the grid's own verbs, in the pill bar's band, as a menu.</summary>
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>
        /// Whether the band carries a menu of the grid's own actions - today, resetting the layout.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Off by default, as every feature here is. A menu inside §37's pill bar rather than a toolbar
        /// above it: a toolbar is a second <c>rz-datatable-header</c>, which every theme dresses as its
        /// own band, so the two stack to about twice the chrome. The menu leaves the band the height it
        /// already was.
        /// </para>
        /// <para>
        /// The band draws for a menu even when nothing is filtered, which §37 did not do - about 53px of
        /// chrome on a grid with no filter. That is why this is a parameter rather than something the
        /// grid decides: a grid that leaves it off is unchanged.
        /// </para>
        /// <para>
        /// <strong>Not gated on <see cref="StorageKey" />.</strong> <see cref="ClearSettings" /> is
        /// meaningful on a grid that stores nothing - it clears the width, visibility, position and
        /// filter a user chose in this session and puts the markup's own sort back - and gating the
        /// menu on a storage key would make the reset unreachable on exactly the grids whose users
        /// cannot get their layout back any other way.
        /// </para>
        /// </remarks>
        [Parameter] public bool ShowGridMenu { get; set; }

        /// <summary>
        /// Whether this grid offers the export entry, when something is registered to export with.
        /// </summary>
        /// <remarks>
        /// On by default, so registering an exporter enables the entry across the application. A grid
        /// that should not offer it turns this off and keeps the menu's other entries.
        /// </remarks>
        [Parameter] public bool ShowExport { get; set; } = true;

        // Whether an export is running. Its own flag rather than IsLoading, which belongs to the grid's
        // data load and has a state machine around it that an export has no business entering.
        bool exporting;

        internal string GridMenuElementId => ElementId + "-grid-menu";


        // One panel for the grid, null until the first open builds it - §29's reason, and the same
        // shape the filter menu above uses.
        RadzenPopup? gridMenuPopup;
        Action<object>? captureGridMenuPopup;

        // The trigger, so the panel has something to hang from. One element, not one per column, which
        // is the whole difference between this and §35's array of captures.
        ElementReference gridMenuElement;
        Action<ElementReference>? captureGridMenuElement;

        // Whether a click has asked for the panel and the render that writes its body has not happened
        // yet. §29's order: Radzen.openPopup measures a panel this render has not written.
        bool gridMenuPending;

        // Whether the panel has ever been opened, which is what says the body has something to draw.
        bool gridMenuBuilt;

        IFastGridExporter? exporter;
        bool exporterResolved;

        /// <summary>
        /// What will export this grid, if the application registered anything - and null if not.
        /// </summary>
        /// <remarks>
        /// <see cref="IFastGridQueryExecutor" />'s resolution, for its reason: asked of the service
        /// provider once and cached, with null an ordinary answer rather than a fault. There is no
        /// built-in fallback here, which is the difference - an unregistered executor still has an
        /// answer, and an unregistered exporter means the entry is simply not offered. See
        /// <see cref="IFastGridExporter" /> for why it arrives this way rather than by reference.
        /// </remarks>
        IFastGridExporter? Exporter
        {
            get
            {
                if (!exporterResolved)
                {
                    exporterResolved = true;
                    exporter = Services?.GetService(typeof(IFastGridExporter)) as IFastGridExporter;
                }

                return exporter;
            }
        }

        /// <summary>
        /// The band, above the scroll container and below the top pager.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Flex only when the menu is on.</strong> The trigger has to be a flex item in the
        /// band's row: measured as a block sibling of the chip list inside the block-level band it wraps
        /// to a second line and the band grows to 86.5px, 88px or 103px depending on the control - a
        /// second <em>row</em> rather than a second band, but chrome bought for nothing. As a flex item
        /// with <c>margin-inline-start:auto</c> the band stays at 67px and the trigger lands 16px from
        /// the band's inline edge, which is the band's own padding.
        /// </para>
        /// <para>
        /// <strong>An inline style, and it is a cost.</strong> This package ships no stylesheet, so
        /// there is nowhere else to put it, and an inline style is the one thing a consumer's own rule
        /// cannot override without <c>!important</c> - against a band §37 deliberately named
        /// <c>rz-filter-pills</c> so that it could be restyled. It is written only on the render that
        /// draws the menu: a grid using §37's bar alone gets the block band it has always had, so
        /// nothing anyone has already styled moves.
        /// </para>
        /// <para>
        /// The chip list is not written when there is nothing in it. An empty <c>role="list"</c> with
        /// an accessible name is a list a screen reader announces as having no items, which is worse
        /// than the band being quiet.
        /// </para>
        /// </remarks>
        void RenderBand(RenderTreeBuilder builder)
        {
            var menu = ShowGridMenu;
            var pills = ShowFilterPills && AnyColumnFiltered();

            if (!menu && !pills)
            {
                return;
            }

            builder.OpenElement(15, "div");
            builder.AddAttribute(16, "class", "rz-datatable-header rz-filter-pills");

            if (menu)
            {
                builder.AddAttribute(17, "style", "display:flex;align-items:center;gap:.5rem");
            }

            if (pills)
            {
                RenderFilterPills(builder);
            }

            if (menu)
            {
                RenderGridMenuTrigger(builder);
            }

            builder.CloseElement();
        }

        /// <summary>The trigger, which is upstream's overflow-menu button and not this grid's own.</summary>
        /// <remarks>
        /// <para>
        /// <strong><c>rz-menu-toggle</c>, read out of <c>RadzenMenu</c> and checked in a browser.</strong>
        /// It is the only control upstream draws for "open a menu of everything else": the button
        /// <c>RadzenMenu</c> renders when <c>Responsive</c> is on. The class itself carries no scope -
        /// <c>.rz-menu-toggle { appearance:none; background:none; border:none; display:inline-flex;
        /// padding:0; color:var(--rz-menu-top-item-color) }</c> - and that colour is defined at
        /// <c>:root</c>, so it resolves outside a menubar. Measured here: 20x20, no padding, no margin.
        /// </para>
        /// <para>
        /// <strong>The glyph is <c>more_horiz</c> because <c>more_vert</c> is taken.</strong> The themes
        /// draw the column drag handle with <c>.rz-column-drag:after { content: "more_vert" }</c>, so
        /// the vertical kebab already means "pick this column up" one band away in this same component.
        /// Upstream's own toggle uses <c>menu</c>, which reads as site navigation in a grid.
        /// </para>
        /// <para>
        /// <strong>A real tab stop</strong>, as §37's pills are and for their reason: this is outside
        /// <c>role="grid"</c>, so §12's one-tab-stop rule does not reach it, and a menu only a mouse can
        /// open is the mouse-only control §31 refused.
        /// </para>
        /// </remarks>
        void RenderGridMenuTrigger(RenderTreeBuilder builder)
        {
            builder.OpenElement(70, "button");
            builder.AddAttribute(71, "type", "button");
            builder.AddAttribute(72, "class", "rz-menu-toggle");

            // Pushed to the inline end whatever is beside it. Not justify-content:space-between on the
            // band, which puts a lone trigger at the start on a grid with the menu on and no filters -
            // which is the ordinary state of such a grid.
            builder.AddAttribute(73, "style", "margin-inline-start:auto");
            builder.AddAttribute(74, "aria-haspopup", "menu");
            builder.AddAttribute(75, "aria-label", GridMenuText);
            builder.AddAttribute(76, "title", GridMenuText);
            builder.AddAttribute(77, "aria-controls", GridMenuElementId);
            builder.AddAttribute(78, "aria-expanded",
                gridMenuPopup is { IsOpen: true } ? "true" : "false");
            builder.AddAttribute(79, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => OpenGridMenu()));
            builder.AddElementReferenceCapture(80,
                captureGridMenuElement ??= reference => gridMenuElement = reference);

            builder.OpenElement(81, "i");
            builder.AddAttribute(82, "class", "notranslate rzi");
            builder.AddAttribute(83, "aria-hidden", "true");
            builder.AddContent(84, "more_horiz");
            builder.CloseElement();

            builder.CloseElement();
        }

        /// <summary>Asks the render after this one to open the panel.</summary>
        void OpenGridMenu()
        {
            gridMenuBuilt = true;
            gridMenuPending = true;

            StateHasChanged();
        }

        /// <summary>Opens the panel, once the render that wrote its body has happened.</summary>
        async Task OpenPendingGridMenuAsync()
        {
            gridMenuPending = false;

            if (gridMenuPopup is not { } popup)
            {
                return;
            }

            await popup.ToggleAsync(gridMenuElement);

            StateHasChanged();
        }

        void RenderGridMenu(RenderTreeBuilder builder)
        {
            if (!gridMenuBuilt)
            {
                return;
            }

            // 320, clear of §35's panel at 300 and the eleven numbers it writes under it: both are
            // written last among the grid's children and the numbers a run is written in have to ascend.
            RenderOverlayMenu(builder, 320, GridMenuElementId, GridMenuText, RenderGridMenuBody,
                EventCallback.Factory.Create(this, OnGridMenuClosed),
                captureGridMenuPopup ??= reference => gridMenuPopup = (RadzenPopup)reference);
        }

        void OnGridMenuClosed() => StateHasChanged();

        /// <summary>
        /// What is in the menu: the grid's own verbs, and nothing else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>rz-menu-list</c> and <c>rz-menuitem</c> rather than §35's <c>rz-listbox-list</c> and
        /// <c>rz-multiselect-item</c>, and the difference is what the two panels are. §35's is an
        /// operator picker - a list one of whose entries is chosen, which is listbox semantics. This is
        /// a command menu, whose entries do things and none of which is selected afterwards. Upstream
        /// keeps the same two vocabularies apart for the same reason: <c>RadzenSplitButton</c>'s popup
        /// is <c>ul.rz-menu-list &gt; li.rz-menuitem</c>.
        /// </para>
        /// <para>
        /// The entry is a real <c>button</c> inside a presentational <c>li</c>, which is where this
        /// departs from <c>RadzenSplitButtonItem</c> - that puts <c>role="menuitem"</c> and the click on
        /// the <c>li</c> and manages focus through <c>aria-activedescendant</c> on the list. A button is
        /// focusable, takes Enter and Space from the browser, and is what §35's panel already does one
        /// panel over; the alternative is a second activedescendant implementation for a menu with one
        /// entry in it.
        /// </para>
        /// <para>
        /// <strong>No extension point.</strong> §39: an application that wants its own actions in the
        /// band is asking for a <c>HeaderTemplate</c>, and that is the two-band question again with a
        /// different sponsor - it should be argued when someone needs it, against a band that by then
        /// has a menu in it and something to be measured against.
        /// </para>
        /// </remarks>
        void RenderGridMenuBody(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "ul");
            builder.AddAttribute(1, "class", "rz-menu-list");

            RenderGridMenuItem(builder, 2, "settings_backup_restore", ResetLayoutText, ResetLayoutAsync);

            // Only when something is registered to do it. An application that does not reference the
            // export package resolves nothing here and gets a menu with one entry, unchanged.
            if (ShowExport && Exporter is { } registered)
            {
                RenderGridMenuItem(builder, 20, "download", ExportText, () => ExportAsync(registered));
            }

            builder.CloseElement();
        }

        /// <summary>One entry: an icon, a word, and a verb about the whole grid.</summary>
        void RenderGridMenuItem(RenderTreeBuilder builder, int sequence, string icon, string text,
            Func<Task> invoke)
        {
            builder.OpenElement(sequence, "li");
            builder.AddAttribute(sequence + 1, "class", "rz-menuitem");
            builder.AddAttribute(sequence + 2, "role", "presentation");

            builder.OpenElement(sequence + 3, "button");
            builder.AddAttribute(sequence + 4, "type", "button");
            builder.AddAttribute(sequence + 5, "role", "menuitem");

            // rz-filter-menu-item is upstream's reset for a button wearing rz-menuitem-link, which on
            // its own leaves the user agent's own control showing. §39's third finding.
            builder.AddAttribute(sequence + 6, "class", "rz-menuitem-link rz-filter-menu-item");
            builder.AddAttribute(sequence + 7, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => invoke()));

            builder.OpenElement(sequence + 8, "span");
            builder.AddAttribute(sequence + 9, "class", "rz-menuitem-icon notranslate rzi");
            builder.AddAttribute(sequence + 10, "aria-hidden", "true");
            builder.AddContent(sequence + 11, icon);
            builder.CloseElement();

            builder.OpenElement(sequence + 12, "span");
            builder.AddAttribute(sequence + 13, "class", "rz-menuitem-text");
            builder.AddContent(sequence + 14, text);
            builder.CloseElement();

            builder.CloseElement();
            builder.CloseElement();
        }

        /// <summary>Closes the menu, then hands the grid to whatever is registered to export it.</summary>
        /// <remarks>
        /// The close is first for <see cref="ResetLayoutAsync" />'s reason and one of its own: an export
        /// of any size blocks, and a menu left open over a frozen page is worse than a menu that shut
        /// before the wait started.
        /// </remarks>
        async Task ExportAsync(IFastGridExporter registered)
        {
            if (gridMenuPopup is { } popup)
            {
                await popup.CloseAsync();
            }

            // The scrim, for the same reason the grid draws one over a slow load: an export of a large
            // grid takes over a second - 1.74 s at 50,000 rows, measured - and a page that looks idle
            // through it invites a second click, which is a second export.
            exporting = true;

            StateHasChanged();

            try
            {
                await registered.ExportAsync(this);
            }
            finally
            {
                exporting = false;

                StateHasChanged();
            }
        }

        /// <summary>Resets the layout and closes the menu it was invoked from.</summary>
        /// <remarks>
        /// The close is first, and it has to be: <see cref="ClearSettings" /> reloads, and a panel
        /// <c>Radzen.openPopup</c> has reparented to <c>document.body</c> is not moved back by that
        /// render - so a menu left open sits over a grid that has already changed underneath it.
        /// </remarks>
        async Task ResetLayoutAsync()
        {
            if (gridMenuPopup is { } popup)
            {
                await popup.CloseAsync();
            }

            await ClearSettings();
        }
    }
}
