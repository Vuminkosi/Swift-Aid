namespace SwiftAid.Mobile;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
#if ANDROID
		blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
#endif
	}

#if ANDROID
	private static void OnBlazorWebViewInitialized(
		object? sender,
		Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs eventArgs)
	{
		if (eventArgs.WebView is Android.Webkit.WebView webView)
		{
			webView.SetWebChromeClient(new Platforms.Android.EllaiMicrophoneWebChromeClient());
		}
	}
#endif
}
