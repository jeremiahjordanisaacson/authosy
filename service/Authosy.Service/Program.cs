using Authosy.Service;
using Authosy.Service.Models;
using Authosy.Service.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/authosy-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Add Serilog
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console()
        .WriteTo.File("logs/authosy-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
        .WriteTo.EventLog("Authosy", manageEventSource: false));

    // Bind configuration
    builder.Services.Configure<AuthosyConfig>(builder.Configuration.GetSection("Authosy"));

    // Register services
    builder.Services.AddHttpClient<RssFeedService>();
    builder.Services.AddSingleton<ClusteringService>();
    builder.Services.AddSingleton<ClaudeCliService>();
    builder.Services.AddSingleton<MarkdownService>();
    builder.Services.AddSingleton<StateService>();
    builder.Services.AddSingleton<GitService>();
    builder.Services.AddHttpClient<ImageService>();

    // Windows Service support
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Authosy News Service";
    });

    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
