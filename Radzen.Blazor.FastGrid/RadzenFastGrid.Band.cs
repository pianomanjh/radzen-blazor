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
        /// Off by default, as every feature here is. <c>Show</c> rather than <c>Allow</c> for
        /// <see cref="ShowFilterPills" />'s reason: it displays a thing rather than permitting one.
        /// </para>
        /// <para>
        /// <strong>A menu rather than a toolbar, and the difference is a band.</strong> §37b measured a
        /// toolbar injected above §37's pill bar and found two siblings inside <c>.rz-data-grid</c>,
        /// each dressed by the theme as its own band - its own background, its own 16px padding, its own
        /// 1px bottom border. That measurement was taken for a control that did not exist yet, so §39
        /// left it open; it is now taken again, on the same stage, for this control:
        /// </para>
        /// <list type="table">
        /// <item><description>pill bar alone: <strong>67px</strong></description></item>
        /// <item><description>pill bar with this menu in it: <strong>67px</strong> - unchanged</description></item>
        /// <item><description>a toolbar above the pill bar: 69px + 67px = <strong>136px</strong></description></item>
        /// </list>
        /// <para>
        /// So §39's choice holds, and §37b's "about 128px" was not far off. What the measurement added
        /// is the two constraints below, neither of which was in the design: the trigger has to sit in
        /// the band's <em>row</em>, and it costs 53px on a grid with nothing filtered.
        /// </para>
        /// <para>
        /// <strong>The band is drawn for a menu even when nothing is filtered, and that is not free.</strong>
        /// §37 draws it only when something is filtered, on the argument that a permanently empty band
        /// costs a row of chrome forever to avoid one layout shift. With the menu on and no filter the
        /// band measures <strong>53px</strong> - that is the price, and it is the reason this is a
        /// parameter rather than something the grid does on its own. A grid that turns it on has a
        /// reason for the band on every render, which is the case §37's rule was distinguishing itself
        /// from; a grid that does not is untouched.
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

        /// <summary>The panel's element id, so the trigger can name it and upstream can find it.</summary>
        /// <remarks>
        /// §35's reason, unchanged: <c>Radzen.setPopupAriaExpanded</c> looks the anchor up <em>by</em>
        /// <c>aria-controls</c> and silently does nothing when it is missing.
        /// </remarks>
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
        /// The <c>li.rz-menu-toggle-item</c> upstream wraps it in is <c>display:none</c> until a
        /// breakpoint shows it. That wrapper is deliberately not taken - it is <c>RadzenMenu</c>'s
        /// responsive machinery, not part of the button - which is the distinction §37b's correction is
        /// about, made in advance this time rather than after a review.
        /// </para>
        /// <para>
        /// <strong><c>rz-grid-filter-icon</c> was the obvious borrow and it is the wrong one.</strong>
        /// It is the class §37b corrected §35's icon <em>to</em>, it is unscoped, and it renders at
        /// 16x16 in this band - so it passes every check that mattered there. It fails the one that
        /// matters here: its colour is <c>--rz-grid-filter-color</c>, rgb(175,175,178), which against
        /// this band's white is <strong>2.19:1</strong> - below WCAG 2.2 1.4.11's 3:1 for a user
        /// interface component. It is faint on purpose, because in a header cell it is a hint beside a
        /// title; here it would be the only control on its side of the band. <c>rz-menu-toggle</c>
        /// measures <strong>8.18:1</strong>, in company with this band's own "Clear all filters" at 21:1
        /// and its chip text at 15.27:1. §37b's lesson, caught from the other side: the right class for
        /// one job is the wrong class for another, and only the rendered thing says which.
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

            // The trigger's aria-expanded is written from C# and opening the panel changes nothing the
            // grid has rendered, so without this it stays "false" for as long as the menu is open.
            // §35's finding, and the same two-writers argument.
            StateHasChanged();
        }

        /// <summary>The panel, rendered once the first open has asked for one.</summary>
        /// <remarks>
        /// Never removed once it is here, for §35's reason: <c>Radzen.openPopup</c> reparents it to
        /// <c>document.body</c>, and Blazor resolves a removal against the logical parent it recorded.
        /// </remarks>
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

        /// <summary>Says the trigger is closed, once the popup has closed itself.</summary>
        /// <remarks>
        /// The first draft of this section left it out and the review found it. <c>RadzenPopup</c>'s
        /// <c>OnClose</c> updates its own <c>IsOpen</c> but nothing redraws the grid, so a menu closed
        /// by a click outside left <c>aria-expanded="true"</c> in the render tree until something else
        /// happened to render - and the DOM said <c>false</c> the whole time, because upstream's
        /// <c>setPopupAriaExpanded</c> writes it there directly. See <c>RenderOverlayMenu</c>.
        /// </remarks>
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
        /// <strong><c>rz-filter-menu-item</c> is the price of that button, and the browser is what
        /// charged it.</strong> <c>.rz-menuitem .rz-menuitem-link</c> is
        /// <c>color:inherit;display:flex;align-items:center;text-decoration:none</c> and nothing else,
        /// because upstream only ever puts it on a <c>span</c> or a <c>NavLink</c> - neither of which
        /// needs a reset. On a <c>button</c> it left the user agent's own control showing: measured at
        /// <c>border: 2px outset</c>, <c>background: rgb(239,239,239)</c>, Arial at 13.33px inside a
        /// themed panel. <c>.rz-filter-menu-item</c> is
        /// <c>width:100%;text-align:start;background:none;border:none;font:inherit;cursor:pointer</c> -
        /// upstream's own class for precisely this, applied by <c>RadzenDataGrid</c> to the buttons in
        /// its filter menu and by §35 to the operators in this component's other panel. Its name says
        /// <em>filter</em> and its rules say <em>button in a menu</em>; the rules are what is being
        /// borrowed, and there is no more general class shipped.
        /// </para>
        /// <para>
        /// This is §37b's finding for the third time, and the first two were the same shape: a class is
        /// only upstream's if the element under it is the element upstream puts it on. §35 claimed a
        /// control it did not draw; §37b corrected it; this took the right class and put it on the
        /// wrong tag. All three were invisible to a test asserting the markup and obvious on sight.
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

            builder.OpenElement(2, "li");
            builder.AddAttribute(3, "class", "rz-menuitem");
            builder.AddAttribute(4, "role", "presentation");

            builder.OpenElement(5, "button");
            builder.AddAttribute(6, "type", "button");
            builder.AddAttribute(7, "role", "menuitem");
            builder.AddAttribute(8, "class", "rz-menuitem-link rz-filter-menu-item");
            builder.AddAttribute(9, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => ResetLayoutAsync()));

            builder.OpenElement(10, "span");
            builder.AddAttribute(11, "class", "rz-menuitem-icon notranslate rzi");
            builder.AddAttribute(12, "aria-hidden", "true");
            builder.AddContent(13, "settings_backup_restore");
            builder.CloseElement();

            builder.OpenElement(14, "span");
            builder.AddAttribute(15, "class", "rz-menuitem-text");
            builder.AddContent(16, ResetLayoutText);
            builder.CloseElement();

            builder.CloseElement();
            builder.CloseElement();
            builder.CloseElement();
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
