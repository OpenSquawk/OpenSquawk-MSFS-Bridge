#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class BridgeConfig
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal static class BridgeConfigService
{
    public const string ConfigFileName = "bridge-config.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static BridgeConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new BridgeConfig
                {
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<BridgeConfig>(json, Options);
            if (config == null)
            {
                return new BridgeConfig
                {
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }

            if (config.CreatedAt == default)
            {
                config.CreatedAt = DateTimeOffset.UtcNow;
            }

            return config;
        }
        catch
        {
            return new BridgeConfig
            {
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public static void Save(string path, BridgeConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (config.CreatedAt == default)
        {
            config.CreatedAt = DateTimeOffset.UtcNow;
        }

        var json = JsonSerializer.Serialize(config, Options);
        File.WriteAllText(path, json);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
