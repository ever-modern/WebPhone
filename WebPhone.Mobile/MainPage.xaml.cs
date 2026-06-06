#if ANDROID
using Android;
using Android.Content.PM;
using Android.Webkit;
using Microsoft.AspNetCore.Components.WebView.Maui;
#endif
using Microsoft.Maui.ApplicationModel;

namespace WebPhone.Android
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
#if ANDROID
            ConfigureAndroidWebView();
#endif
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await EnsureMediaPermissionsAsync();
        }

        private static async Task EnsureMediaPermissionsAsync()
        {
#if ANDROID
            var camera = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (camera != PermissionStatus.Granted)
                camera = await Permissions.RequestAsync<Permissions.Camera>();

            var mic = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (mic != PermissionStatus.Granted)
                mic = await Permissions.RequestAsync<Permissions.Microphone>();

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var bt = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                    Platform.AppContext,
                    Manifest.Permission.BluetoothConnect
                );

                if (bt != Permission.Granted)
                {
                    AndroidX.Core.App.ActivityCompat.RequestPermissions(
                        Platform.CurrentActivity!,
                        [Manifest.Permission.BluetoothConnect],
                        1001
                    );
                }
            }
#endif
        }

#if ANDROID
        private void ConfigureAndroidWebView()
        {
            BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
                "WebPhoneAndroidMediaPermissions",
                (handler, view) =>
                {
                    if (handler.PlatformView is not global::Android.Webkit.WebView webView)
                        return;

                    webView.Settings.MediaPlaybackRequiresUserGesture = false;
                    webView.Settings.JavaScriptEnabled = true;

                    webView.SetWebChromeClient(new MediaPermissionWebChromeClient());
                }
            );
        }

        private sealed class MediaPermissionWebChromeClient : WebChromeClient
        {
            public override async void OnPermissionRequest(PermissionRequest? request)
            {
                if (request is null)
                    return;

                var camStatus = await Permissions.CheckStatusAsync<Permissions.Camera>();
                if (camStatus != PermissionStatus.Granted)
                    camStatus = await Permissions.RequestAsync<Permissions.Camera>();

                var micStatus = await Permissions.CheckStatusAsync<Permissions.Microphone>();
                if (micStatus != PermissionStatus.Granted)
                    micStatus = await Permissions.RequestAsync<Permissions.Microphone>();

                if (camStatus == PermissionStatus.Granted && micStatus == PermissionStatus.Granted)
                    request.Grant(request.GetResources());
                else
                    request.Deny();
            }
        }
#endif
    }
}
