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
        window.Destroying += (_, _) => StopHostedServices();
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

    private void StopHostedServices()
    {
        if (stopping is null) return;
        stopping.Cancel();
        foreach (var service in services.GetServices<IHostedService>())
            _ = service.StopAsync(CancellationToken.None);
        stopping = null;
    }
}
