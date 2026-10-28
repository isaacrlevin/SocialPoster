using Microsoft.Extensions.Hosting;

namespace SocialPoster.MAUI;

public partial class App : Application
{
    private readonly IServiceProvider services;
    private CancellationTokenSource? stopping;

    public App(IServiceProvider services)
    {
        this.services = services;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "SocialPoster", Width = 1280, Height = 900 };
        window.Created += (_, _) => StartHostedServices();
        window.Destroying += (_, _) => _ = StopHostedServicesAsync();
#if WINDOWS
        window.Created += (_, _) => DeferWindowsCloseUntilServicesStop(window);
#endif
        return window;
    }

    // MAUI has no generic host, so run the hosted services (the scheduling worker) for the window's lifetime.
    private void StartHostedServices()
    {
        if (stopping is not null) return;
        stopping = new CancellationTokenSource();
        foreach (var service in services.GetServices<IHostedService>())
            _ = service.StartAsync(stopping.Token);
    }

    private async Task StopHostedServicesAsync()
    {
        if (stopping is null) return;
        var cancellation = stopping;
        cancellation.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await Task.WhenAll(services.GetServices<IHostedService>()
                .Select(service => service.StopAsync(timeout.Token))).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally
        {
            cancellation.Dispose();
            stopping = null;
        }
    }

#if WINDOWS
    private void DeferWindowsCloseUntilServicesStop(Window window)
    {
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        var allowClose = false;
        var shutdownStarted = false;

        appWindow.Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (shutdownStarted) return;
            shutdownStarted = true;

            try
            {
                await StopHostedServicesAsync();
            }
            catch (Exception) { }

            allowClose = true;
            nativeWindow.Close();
        };
    }
#endif
}
