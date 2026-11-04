using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace SwiftAid.UI.Components;

/// <summary>
/// Contains animation rendering failures without logging or exposing exception details.
/// </summary>
public sealed class SwiftAidLoaderErrorBoundary : ErrorBoundary
{
    [Parameter]
    public EventCallback<Exception> OnLottieError { get; set; }

    protected override Task OnErrorAsync(Exception exception) => OnLottieError.InvokeAsync(exception);
}
