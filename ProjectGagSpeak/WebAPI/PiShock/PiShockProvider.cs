using GagSpeak.GagspeakConfiguration;
using GagSpeak.PlayerData.Pairs;
using GagSpeak.Services.Mediator;
using GagspeakAPI.Data.Struct;
using GagspeakAPI.Routes;
using System.Net;
using System.Text.Json;
using GagSpeak.GagspeakConfiguration.Models;
using SysJsonSerializer = System.Text.Json.JsonSerializer;

namespace GagSpeak.WebAPI;

public sealed class PiShockProvider : DisposableMediatorSubscriberBase
{
    private readonly GagspeakConfigService _mainConfig;
    private readonly PairManager _pairManager;
    private readonly MainHub _mainHub;
    private readonly HttpClient _httpClient;

    public PiShockProvider(ILogger<PiShockProvider> logger, GagspeakMediator mediator,
        GagspeakConfigService mainConfig, PairManager pairManager, MainHub mainHub) : base(logger, mediator)
    {
        _mainConfig = mainConfig;
        _pairManager = pairManager;
        _mainHub = mainHub;
        _httpClient = new HttpClient();

        Mediator.Subscribe<PiShockExecuteOperation>(this, (msg) => ExecuteOperation(msg.shareCode, msg.OpCode, msg.Intensity, msg.Duration));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _httpClient.Dispose();
    }

    // grab basic information from shock collar.
    private StringContent CreateGetInfoContent(string shareCode)
    {
        StringContent content = new(SysJsonSerializer.Serialize(new
        {
            UserName = _mainConfig.Current.PiShockUsername,
            Code = shareCode,
            Apikey = _mainConfig.Current.PiShockApiKey,
        }), Encoding.UTF8, "application/json");
        return content;
    }

    // For grabbing boolean permissions from a share code.
    private StringContent CreateDummyExecuteContent(string shareCode, int opCode)
    {
        StringContent content = new(SysJsonSerializer.Serialize(new
        {
            Username = _mainConfig.Current.PiShockUsername,
            Name = "GagSpeakProvider",
            Op = opCode,
            Intensity = 0,
            Duration = 0,
            Code = shareCode,
            Apikey = _mainConfig.Current.PiShockApiKey,
        }), Encoding.UTF8, "application/json");
        return content;
    }

    // Sends operation to shock collar
    private StringContent CreateExecuteOperationContent(string shareCode, int opCode, int intensity, int duration)
    {
        StringContent content = new(SysJsonSerializer.Serialize(new
        {
            Username = _mainConfig.Current.PiShockUsername,
            Name = "GagSpeakProvider",
            Op = opCode,
            Intensity = intensity,
            Duration = duration,
            Code = shareCode,
            Apikey = _mainConfig.Current.PiShockApiKey,
        }), Encoding.UTF8, "application/json");
        return content;
    }

    public async Task<PiShockPermissions> GetPermissionsFromCode(string shareCode)
    {
        ShockService shockService = _mainConfig.Current.ShockService;
        if (shockService == ShockService.PiShock)
        {
            try
            {
                var jsonContent = CreateGetInfoContent(shareCode);

                Logger.LogTrace("PiShock Request Info URI Firing: {piShockUri}", GagspeakPiShock.GetInfoPath());
                var response = await _httpClient.PostAsync(GagspeakPiShock.GetInfoPath(), jsonContent).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Logger.LogTrace("PiShock Request Info Response: {response}", response);
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var jsonDocument = JsonDocument.Parse(content);
                    var root = jsonDocument.RootElement;

                    int maxIntensity = root.GetProperty("maxIntensity").GetInt32();
                    int maxShockDuration = root.GetProperty("maxDuration").GetInt32();

                    Logger.LogTrace("Obtaining boolean values by passing dummy requests to share code");
                    var result = await ConstructPermissionObject(shareCode, maxIntensity, maxShockDuration);
                    Logger.LogTrace("PiShock Permissions obtained: {result}", result);
                    return result;
                }
                else if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    Logger.LogWarning("The Credentials for your API Key and Username do not match any profile in PiShock");
                    return new();
                }
                else
                {
                    Logger.LogError("The ShareCode for this profile does not exist, or this is a simple error 404: {statusCode}", response.StatusCode);
                    return new();
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError(ex, "Error getting PiShock permissions from share code");
                return new PiShockPermissions();
            }
        }
        else
        {
            try
            {
                var getInfoPath = GagspeakOpenShock.GetInfoPath(shareCode);
                Logger.LogTrace("OpenShock Request Info URI Firing {openshockUri}", getInfoPath);
                var response = await _httpClient.GetAsync(getInfoPath).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var jsonDocument = JsonDocument.Parse(content);
                    var root = jsonDocument.RootElement;

                    const int
                        hubIndex =
                            0; // Openshock supports multiple physical hubs, but to reduce complexity, we're only using the first one.
                    const int
                        shockerIndex =
                            0; // Openshock supports multiple shockers per hub, but to reduce complexity, we're only using the first one.

                    int maxIntensity =
                        root.GetProperty("data").GetProperty("devices")[hubIndex].GetProperty("shockers")[shockerIndex]
                            .GetProperty("limits").GetProperty("intensity").GetInt32();
                    int maxShockDuration =
                        root.GetProperty("data").GetProperty("devices")[hubIndex].GetProperty("shockers")[shockerIndex]
                            .GetProperty("limits").GetProperty("duration").GetInt32();

                    // Openshock permissions are fetched via the API at the same time as the max intensity and duration
                    bool allowShocks =
                        root.GetProperty("data").GetProperty("devices")[hubIndex].GetProperty("shockers")[shockerIndex]
                            .GetProperty("permissions").GetProperty("shock").GetBoolean();
                    bool allowVibrations =
                        root.GetProperty("data").GetProperty("devices")[hubIndex].GetProperty("shockers")[shockerIndex]
                            .GetProperty("permissions").GetProperty("vibrate").GetBoolean();
                    bool allowBeeps =
                        root.GetProperty("data").GetProperty("devices")[hubIndex].GetProperty("shockers")[shockerIndex]
                            .GetProperty("permissions").GetProperty("sound").GetBoolean();
                    
                    var result = new PiShockPermissions() { AllowShocks = allowShocks, AllowVibrations = allowVibrations, AllowBeeps = allowBeeps, MaxIntensity = maxIntensity, MaxDuration = maxShockDuration };
                    Logger.LogTrace("Openshock Permissions Obtained: {result}", result);
                    return result;
                }
                else
                {
                    Logger.LogError("The share code is invalid or this is a simple error 404: {statusCode}", response.StatusCode);
                    return new();
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError(ex, "Error getting OpenShock permissions from share code");
                return new PiShockPermissions();
            }
        }
    }

    private async Task<PiShockPermissions> ConstructPermissionObject(string shareCode, int intensityLimit, int durationLimit)
    {
        // Shock, Vibrate, Beep. In that order
        int[] opCodes = { 0, 1, 2 };
        bool shocks = false;
        bool vibrations = false;
        bool beeps = false;

        try
        {
            foreach (var opCode in opCodes)
            {
                var jsonContent = CreateDummyExecuteContent(shareCode, opCode);
                var response = await _httpClient.PostAsync(GagspeakPiShock.ExecuteOperationPath(), jsonContent).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK) continue;

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                switch (opCode)
                {
                    case 0:
                        shocks = content! == "Operation Attempted."; break;
                    case 1:
                        vibrations = content! == "Operation Attempted."; break;
                    case 2:
                        beeps = content! == "Operation Attempted."; break;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "Error executing operation on PiShock");
        }

        return new PiShockPermissions() { AllowShocks = shocks, AllowVibrations = vibrations, AllowBeeps = beeps, MaxIntensity = intensityLimit, MaxDuration = durationLimit };
    }


    public async void ExecuteOperation(string shareCode, int opCode, int intensity, int duration)
    {
        ShockService shockService = _mainConfig.Current.ShockService;
        if (shockService == ShockService.PiShock)
        {
            try
            {
                var jsonContent = CreateExecuteOperationContent(shareCode, opCode, intensity, duration);
                var response = await _httpClient.PostAsync(GagspeakPiShock.ExecuteOperationPath(), jsonContent)
                                                .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Logger.LogError("Error executing operation on PiShock. Status returned: " + response.StatusCode);
                    return;
                }

                var contentStr = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Logger.LogDebug("PiShock Request Sent to Shock Collar Successfully! Content returned was:\n" +
                                contentStr);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError(ex, "Error executing operation on PiShock");
            }
        }
        else if (shockService == ShockService.OpenShock)
        {
            try
            {
                // This is messy and slow.
                // Openshock is working on permissions per API token. In the future permissions should be handled per token
                // and share codes should not be used at all.
                var permissions = await GetPermissionsFromCode(shareCode);
                
                if (!permissions.AllowShocks && opCode == 0)
                {
                    Logger.LogDebug("Shock not allowed by share code. Not executing operation.");
                    return;
                }

                if (!permissions.AllowVibrations && opCode == 1)
                {
                    Logger.LogDebug("Vibration not allowed by share code. Not executing operation.");
                    return;
                }

                if (!permissions.AllowBeeps && opCode == 2)
                {
                    Logger.LogDebug("Beep not allowed by share code. Not executing operation.");
                    return;
                }

                if (permissions.MaxIntensity < intensity)
                {
                    Logger.LogDebug("Intensity higher than allowed by share code ({intensity}). Setting to maximum ({maxintensity}).", intensity, permissions.MaxIntensity);
                    intensity = permissions.MaxIntensity;
                }

                if (permissions.MaxDuration < duration)
                {
                    Logger.LogDebug("Duration higher than allowed by share code ({duration}). Setting to maximum ({maxduration}).", duration, permissions.MaxDuration);
                    duration = permissions.MaxDuration;
                }
                
                string shockMode = opCode switch // This can probably be made an enum or something a little cleaner.
                {
                    0 => "Shock",
                    1 => "Vibrate",
                    2 => "Sound",
                    _ => "None"
                };
                
                StringContent jsonContent = new(SysJsonSerializer.Serialize(new Dictionary<string, object>
                {
                    { "shocks", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                { "id", "8dc6755e-0f39-494e-8bb4-22377d469e3b" },
                                { "type", shockMode },
                                { "intensity", intensity },
                                { "duration", duration },
                                { "exclusive", true }
                            }
                        }
                    },
                    { "customName", "GagSpeakProvider"}
                }), Encoding.UTF8, "application/json");

                using (var request = new HttpRequestMessage(HttpMethod.Post, GagspeakOpenShock.ExecuteOperationPath()))
                {
                    request.Content = jsonContent;
                    request.Headers.Add("Openshocktoken", _mainConfig.Current.PiShockApiKey);
        
                    HttpResponseMessage response = _httpClient.SendAsync(request).Result;

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Logger.LogError("Error executing operation on OpenShock. Status returned: {status}\nContent: {data}", response.StatusCode, jsonContent);
                    }
                    else
                    {
                        var contentStr = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Logger.LogDebug("OpenShock Request Sent to Shock Collar Successfully! Content returned was:\n" +
                                        contentStr);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError(ex, "Error executing operation on OpenShock");
            }
        }
    }
}
