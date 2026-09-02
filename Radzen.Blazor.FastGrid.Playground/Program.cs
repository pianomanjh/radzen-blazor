using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

var app = builder.Build();

// MapStaticAssets, not UseStaticFiles: from .NET 9 the framework's own scripts - blazor.web.js among
// them - and every referenced package's _content assets are served through the static-asset endpoints.
app.MapStaticAssets();
app.UseAntiforgery();
app.MapRazorComponents<Radzen.Blazor.FastGrid.Playground.App>().AddInteractiveServerRenderMode();

app.Run();
