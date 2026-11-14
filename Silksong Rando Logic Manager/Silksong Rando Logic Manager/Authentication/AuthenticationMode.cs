namespace Silksong_Rando_Logic_Manager.Authentication;

public sealed record AuthenticationMode(bool Bypass)
{
    public static AuthenticationMode Read(IConfiguration configuration)
    {
        return configuration["Authentication:Mode"] switch
        {
            "required" => new(false),
            "bypass" => new(true),
            _ => throw new InvalidOperationException("Invalid authentication startup configuration. Set Authentication:Mode to exactly required or bypass in Config/appsettings.json (or explicit environment/command line). No automatic fallback is available.")
        };
    }
}
