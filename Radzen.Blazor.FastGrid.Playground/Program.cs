using Radzen.FastGrid.Export;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

// §40's export, enabled the way an application enables it: reference the package, register it once.
// Every grid with ShowGridMenu then offers an Export entry.
builder.Services.AddRadzenFastGridExport(o =>
{
    o.SheetName = "People";
    o.FileName = _ => $"people-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
});

var app = builder.Build();

// MapStaticAssets, not UseStaticFiles: from .NET 9 the framework's own scripts - blazor.web.js among
// them - and every referenced package's _content assets are served through the static-asset endpoints.
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<Radzen.Blazor.FastGrid.Playground.App>().AddInteractiveServerRenderMode();

app.Run();
