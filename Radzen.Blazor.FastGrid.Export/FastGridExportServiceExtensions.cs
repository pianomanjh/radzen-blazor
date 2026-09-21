using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// Turns the band menu's export entry on, for every grid in the application.
    /// </summary>
    public static class FastGridExportServiceExtensions
    {
        /// <summary>
        /// Registers the workbook export, so a grid showing the band menu offers an <em>Export</em> entry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One line, and every grid with <c>ShowGridMenu</c> gains the entry:
        /// </para>
        /// <code>
        /// builder.Services.AddRadzenFastGridExport();
        /// </code>
        /// <para>
        /// Named for <c>AddRadzenCookieThemeService</c> in <c>Radzen.Blazor</c>, which is the shape a
        /// consumer of these packages has already met.
        /// </para>
        /// </remarks>
        /// <param name="services">The application's services.</param>
        /// <param name="configure">What the export does, if not the default of saving an .xlsx file.</param>
        /// <returns>The same collection, so calls chain.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
        public static IServiceCollection AddRadzenFastGridExport(this IServiceCollection services,
            Action<FastGridExportServiceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var options = new FastGridExportServiceOptions();

            configure?.Invoke(options);

            // Scoped, because the implementation holds a JavaScript module reference and that belongs to
            // one circuit on Blazor Server.
            //
            // GetService rather than GetRequiredService for the runtime: a prerendering pass has none,
            // and the exporter's own path for that is to build the workbook and not save it - which is
            // better than a grid that cannot render because nothing can download yet.
            services.AddScoped<IFastGridExporter>(provider =>
                new WorkbookExporter(options, provider.GetService<IJSRuntime>()));

            return services;
        }
    }
}
