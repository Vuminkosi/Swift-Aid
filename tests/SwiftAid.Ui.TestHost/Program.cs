using MudBlazor.Services;
using SwiftAid.Ui.TestHost.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.EllaiDisplayedReceiptContext>();
builder.Services.AddScoped<SwiftAid.UI.Ellai.Guidance.EllaiCaseFilterContext>();

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Run();
