
using Serilog;
namespace Api.Logging;
public static class SerilogConfig
{
    public static void Configure(IConfiguration cfg)
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(cfg)
            .CreateLogger();
    }
}
