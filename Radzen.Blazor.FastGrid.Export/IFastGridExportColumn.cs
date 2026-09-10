using System;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// What a column says about its own export, when the defaults are not what it wants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <c>IExcelExportColumn&lt;TItem&gt;</c> from the consuming application §38 surveyed,
    /// unchanged but for its name. It is adopted rather than redesigned because the survey found it
    /// doing one job well: a column exports a value the grid does not draw - or does not draw in a form
    /// a spreadsheet can sort - by implementing this, which a delegate parameter on the grid could not
    /// express per column.
    /// </para>
    /// <para>
    /// <strong>Implementing it is the exception.</strong> Every bound column exports without it: §4's
    /// column already holds the accessor its cells are drawn from, so a lookup column exports its name
    /// rather than its id and a collection column its joined list, with nothing declared. The case that
    /// needs this is the one the grid cannot reach - a <c>TemplateColumn</c>, whose cell is a
    /// <c>RenderFragment</c> and whose only route to text is a renderer pass per cell.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The grid's row type.</typeparam>
    public interface IFastGridExportColumn<TItem>
    {
        /// <summary>The value to export for a row, in place of what the column would otherwise give.</summary>
        Func<TItem, object?>? ExportValue { get; }

        /// <summary>The header text, when it should differ from the column's title.</summary>
        string? ExportTitle { get; }

        /// <summary>The spreadsheet number format for the column, such as <c>#,##0.00</c>.</summary>
        string? ExportFormat { get; }

        /// <summary>Whether the column is left out of the export entirely.</summary>
        bool ExportIgnore { get; }
    }
}
