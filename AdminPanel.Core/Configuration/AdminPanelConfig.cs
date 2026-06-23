using System.Text.Json.Serialization;

namespace AdminPanel.Configuration;

/// <summary>Configuration bound from .assets/configs/adminpanel.json.</summary>
internal sealed class AdminPanelConfig
{
    [JsonPropertyName("Database")]
    public DatabaseConfig Database { get; set; } = new();

    [JsonPropertyName("PollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 30;

    [JsonPropertyName("SlapDamage")]
    public float SlapDamage { get; set; } = 10f;

    [JsonPropertyName("Command")]
    public string Command { get; set; } = "admin";

    [JsonPropertyName("ServerTag")]
    public string ServerTag { get; set; } = "all";
}

internal sealed class DatabaseConfig
{
    [JsonPropertyName("Host")]
    public string Host { get; set; } = "10.1.1.180";

    [JsonPropertyName("Port")]
    public int Port { get; set; } = 3306;

    [JsonPropertyName("Database")]
    public string Database { get; set; } = "adminpanel";

    [JsonPropertyName("User")]
    public string User { get; set; } = "adminpanel";

    [JsonPropertyName("Password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("MaxPoolSize")]
    public int MaxPoolSize { get; set; } = 4;
}
