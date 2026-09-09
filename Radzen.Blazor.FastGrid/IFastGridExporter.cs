using System.Threading;
using System.Threading.Tasks;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Something that can export a grid, found in the service provider rather than referenced.
    /// </summary>
    /// <remarks>
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
        /// <remarks>
        /// <para>
        /// <strong>The token is the grid's own lifetime</strong>, so an export outlives the grid only
        /// for as long as it takes to notice. A streamed export of a large grid runs for seconds and
        /// reads the grid while it does; a reader who navigates away in the middle of one has stopped
        /// wanting the file, and without this the write would run to the end and then hand a download to
        /// a page nobody is on. Implementations that write in one step can ignore it.
        /// </para>
        /// </remarks>
        /// <typeparam name="TItem">The grid's row type.</typeparam>
        /// <param name="grid">The grid whose menu the entry was clicked in.</param>
        /// <param name="cancellationToken">Cancelled when the grid is disposed.</param>
        Task ExportAsync<TItem>(RadzenFastGrid<TItem> grid, CancellationToken cancellationToken = default);
    }
}
