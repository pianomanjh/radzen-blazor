using System.Threading.Tasks;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Something that can export a grid, found in the service provider rather than referenced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This interface is the answer to a problem §39 and §40 made between them.</strong> §40 said
    /// an export entry belongs in §39's band menu <em>"once there is a menu to put it in"</em>. There is
    /// one, and the entry could not be built: the menu is drawn here, <c>ToWorkbook</c> lives in
    /// <c>Radzen.Blazor.FastGrid.Export</c>, and that package references this one - so this one cannot
    /// reference it back without rooting <c>XlsxWriter</c> in every consumer, measured at 400 KB over the
    /// wire on a trimmed WebAssembly build.
    /// </para>
    /// <para>
    /// So the dependency stays one way and the <em>service provider</em> carries the export across.
    /// <c>AddRadzenFastGridExport()</c> registers an implementation; a grid with
    /// <see cref="RadzenFastGrid{TItem}.ShowGridMenu" /> on then offers the entry. An application that
    /// does not reference the export package registers nothing, resolves nothing, and draws no entry.
    /// </para>
    /// <para>
    /// <strong>It is deliberately not the extension point §39 refused</strong>, and it takes one member
    /// to keep it that way. That refusal was of <em>"an application that wants its own actions in the
    /// band"</em> - a list of arbitrary items, which is the toolbar question again. This is one verb,
    /// named by the grid.
    /// </para>
    /// <para>
    /// <strong>The first draft let the implementation name the entry, and the review was right that this
    /// crossed the line.</strong> A <c>Text</c> property here means a registration can label the entry
    /// anything - <em>Send to SAP</em> - and then the interface is a one-slot menu extension point with
    /// a suggestive name rather than one verb. It is gone. The entry says whatever
    /// <c>RadzenFastGrid.ExportText</c> says, which is a parameter on the grid, localized through the
    /// same resources as every other word it draws, and settable per grid by whoever declares it. An
    /// application that wants a differently named <em>action</em> is asking for §39's extension point,
    /// and should be made to ask for it.
    /// </para>
    /// <para>
    /// The resolution is <see cref="IFastGridQueryExecutor" />'s, which is the pattern this grid already
    /// had for a capability an application may or may not have registered: asked of the service provider
    /// once, cached, and null is an ordinary answer rather than a fault.
    /// </para>
    /// <para>
    /// <strong>Nothing about a spreadsheet appears here</strong>, and that is what keeps the trim graph
    /// clean. The method is generic so that the implementation gets the grid's own row type; the call
    /// site knows it statically, so there is no reflection and nothing to annotate.
    /// </para>
    /// </remarks>
    public interface IFastGridExporter
    {
        /// <summary>Exports the grid, however this implementation exports things.</summary>
        /// <typeparam name="TItem">The grid's row type.</typeparam>
        /// <param name="grid">The grid whose menu the entry was clicked in.</param>
        Task ExportAsync<TItem>(RadzenFastGrid<TItem> grid);
    }
}
