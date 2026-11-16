
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Accessibility;

public static class RunwayIdentifier
{
    private static readonly Regex DesignatorPattern = new Regex(
        @"^(?<number>\d{1,2})(?<suffix>[LCR]?)$",
        RegexOptions.CultureInvariant);

    public static string Normalize(string value)
    {
        string normalized = (value ?? String.Empty).Trim().ToUpperInvariant().Replace(" ", String.Empty);
        Match match = DesignatorPattern.Match(normalized);
        if (!match.Success)
            return normalized;

        int number;
        if (!Int32.TryParse(match.Groups["number"].Value, out number) ||
            number < 1 || number > 36)
            return normalized;

        return number.ToString("00") + match.Groups["suffix"].Value;
    }
}

[DataContract]
public class RunwayConfiguration
{
    [DataMember(Name = "columns")]
    public List<RunwayColumn> Columns { get; set; }

    [DataMember(Name = "autoWx", EmitDefaultValue = false)]
    public AutoWxSettings AutoWx { get; set; }

    public static RunwayConfiguration Load(string path)
    {
        if (!File.Exists(path))
            throw new ConfigurationException("Configuration file not found: " + path);

        try
        {
            RunwayConfiguration configuration;
            using (FileStream stream = File.OpenRead(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(RunwayConfiguration));
                configuration = (RunwayConfiguration)serializer.ReadObject(stream);
            }

            if (configuration == null)
                throw new ConfigurationException("Configuration is empty: " + path);

            configuration.Validate();
            return configuration;
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConfigurationException("Could not read configuration " + path + ": " + ex.Message);
        }
    }

    public void Validate()
    {
        if (Columns == null || Columns.Count == 0)
            throw new ConfigurationException("Configuration must contain at least one column.");

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int airportCount = 0;

        foreach (RunwayColumn column in Columns)
        {
            if (column == null || column.Airports == null || column.Airports.Count == 0)
                throw new ConfigurationException("Every column must contain at least one airport.");

            foreach (AirportConfiguration airport in column.Airports)
            {
                airportCount++;
                if (airport == null || String.IsNullOrWhiteSpace(airport.Code))
                    throw new ConfigurationException("Every airport must have a non-empty code.");

                airport.Code = airport.Code.Trim().ToUpperInvariant();
                if (!codes.Add(airport.Code))
                    throw new ConfigurationException("Duplicate airport code: " + airport.Code);

                ValidateState(airport.Code, "west", airport.West);
                ValidateState(airport.Code, "east", airport.East);
                if (airport.AutoWeather != null)
                    ValidateAutoWeather(airport);
            }
        }

        if (airportCount == 0)
            throw new ConfigurationException("Configuration must contain at least one airport.");

        ValidateAutoWxSettings(AutoWx, "autoWx");
    }

    private static void ValidateState(string code, string name, RunwayState state)
    {
        if (state == null)
            throw new ConfigurationException(code + " is missing the " + name + " state.");
        if (String.IsNullOrWhiteSpace(state.Dep) || String.IsNullOrWhiteSpace(state.Arr))
            throw new ConfigurationException(code + " " + name + " must define non-empty DEP and ARR runways.");

        state.Dep = RunwayIdentifier.Normalize(state.Dep);
        state.Arr = RunwayIdentifier.Normalize(state.Arr);
    }

    private static void ValidateAutoWxSettings(AutoWxSettings settings, string name)
    {
        if (settings == null)
            return;

        if (settings.NoFactorWind.HasValue && settings.NoFactorWind.Value < 0)
            throw new ConfigurationException(name + " noFactorWind cannot be negative.");
        if (settings.PrefWind.HasValue && settings.PrefWind.Value < 0)
            throw new ConfigurationException(name + " prefWind cannot be negative.");
        if (settings.AssumedWindDir.HasValue &&
            (settings.AssumedWindDir.Value < 0 || settings.AssumedWindDir.Value > 360))
            throw new ConfigurationException(name + " assumedWindDir must be between 0 and 360.");
    }

    private static void ValidateAutoWeather(AirportConfiguration airport)
    {
        AutoWeatherAirport auto = airport.AutoWeather;
        ValidateAutoWxSettings(auto, airport.Code + " auto");

        if (auto.Runways == null || auto.Runways.Count == 0)
            throw new ConfigurationException(airport.Code + " auto must define at least one runway.");

        var available = new HashSet<string>(GetConfiguredRunways(airport), StringComparer.OrdinalIgnoreCase);
        var autoRunways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AutoWeatherRunway runway in auto.Runways)
        {
            if (runway == null || String.IsNullOrWhiteSpace(runway.Id) || !runway.TrueHeading.HasValue)
                throw new ConfigurationException(airport.Code + " auto runway must define id and trueHeading.");

            runway.Id = RunwayIdentifier.Normalize(runway.Id);
            if (runway.TrueHeading.Value < 0 || runway.TrueHeading.Value > 360)
                throw new ConfigurationException(airport.Code + " auto runway " + runway.Id +
                    " trueHeading must be between 0 and 360.");
            if (!available.Contains(runway.Id))
                throw new ConfigurationException(airport.Code + " auto runway " + runway.Id +
                    " is not present in west/east presets.");
            if (!autoRunways.Add(runway.Id))
                throw new ConfigurationException(airport.Code + " auto contains duplicate runway " + runway.Id + ".");
        }

        ValidatePreferredRunways(airport, "preferredDep", auto.PreferredDep, autoRunways);
        ValidatePreferredRunways(airport, "preferredArr", auto.PreferredArr, autoRunways);

        if (!String.IsNullOrWhiteSpace(auto.MetarFrom))
            auto.MetarFrom = auto.MetarFrom.Trim().ToUpperInvariant();
    }

    private static void ValidatePreferredRunways(
        AirportConfiguration airport,
        string name,
        List<string> runways,
        HashSet<string> available)
    {
        if (runways == null)
            return;
        if (runways.Count == 0)
            throw new ConfigurationException(airport.Code + " auto " + name + " cannot be empty.");

        for (int i = 0; i < runways.Count; i++)
        {
            runways[i] = RunwayIdentifier.Normalize(runways[i]);
            if (!available.Contains(runways[i]))
                throw new ConfigurationException(airport.Code + " auto " + name +
                    " references unknown runway " + runways[i] + ".");
        }
    }

    public static List<string> GetConfiguredRunways(AirportConfiguration airport)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        AddConfiguredRunway(result, seen, airport.West.Dep);
        AddConfiguredRunway(result, seen, airport.West.Arr);
        AddConfiguredRunway(result, seen, airport.East.Dep);
        AddConfiguredRunway(result, seen, airport.East.Arr);
        return result;
    }

    private static void AddConfiguredRunway(List<string> result, HashSet<string> seen, string runway)
    {
        string normalized = RunwayIdentifier.Normalize(runway);
        if (seen.Add(normalized))
            result.Add(normalized);
    }

    public IEnumerable<AirportConfiguration> GetAirports()
    {
        foreach (RunwayColumn column in Columns)
            foreach (AirportConfiguration airport in column.Airports)
                yield return airport;
    }
}

[DataContract]
public class RunwayColumn
{
    [DataMember(Name = "airports")]
    public List<AirportConfiguration> Airports { get; set; }
}

[DataContract]
public class AirportConfiguration
{
    [DataMember(Name = "code")]
    public string Code { get; set; }

    [DataMember(Name = "west")]
    public RunwayState West { get; set; }

    [DataMember(Name = "east")]
    public RunwayState East { get; set; }

    [DataMember(Name = "auto", EmitDefaultValue = false)]
    public AutoWeatherAirport AutoWeather { get; set; }
}

[DataContract]
public class RunwayState
{
    [DataMember(Name = "dep")]
    public string Dep { get; set; }

    [DataMember(Name = "arr")]
    public string Arr { get; set; }
}

[DataContract]
public class AutoWxSettings
{
    [DataMember(Name = "noFactorWind", EmitDefaultValue = false)]
    public int? NoFactorWind { get; set; }

    [DataMember(Name = "prefWind", EmitDefaultValue = false)]
    public int? PrefWind { get; set; }

    [DataMember(Name = "assumedWindDir", EmitDefaultValue = false)]
    public int? AssumedWindDir { get; set; }
}

[DataContract]
public class AutoWeatherAirport : AutoWxSettings
{
    [DataMember(Name = "metarFrom", EmitDefaultValue = false)]
    public string MetarFrom { get; set; }

    [DataMember(Name = "runways")]
    public List<AutoWeatherRunway> Runways { get; set; }

    [DataMember(Name = "preferredDep", EmitDefaultValue = false)]
    public List<string> PreferredDep { get; set; }

    [DataMember(Name = "preferredArr", EmitDefaultValue = false)]
    public List<string> PreferredArr { get; set; }
}

[DataContract]
public class AutoWeatherRunway
{
    [DataMember(Name = "id")]
    public string Id { get; set; }

    [DataMember(Name = "trueHeading")]
    public int? TrueHeading { get; set; }
}

public class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
}

public class PresetButtonData
{
    public AirportConfiguration Airport;
    public RunwayState State;
    public string Key;
}

public class AirportRunwaySelection
{
    public AirportConfiguration Airport;
    public RunwayState State;
    public string Description;
}

public class AutoRunwayPlan
{
    public readonly List<AirportRunwaySelection> Selections =
        new List<AirportRunwaySelection>();
    public readonly List<string> Skipped = new List<string>();
}

internal static class NetworkTextDownloader
{
    private const int TimeoutMilliseconds = 12000;

    internal static string Download(string url, string accept)
    {
        try
        {
            // TLS 1.2 is required by the public VATSIM endpoints. The numeric
            // value keeps compilation compatible with the .NET 4 compiler.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = accept;
            request.UserAgent = "EuroScopeRunwayPresetManager/1.0";
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.KeepAlive = false;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string content = reader.ReadToEnd();
                if (String.IsNullOrWhiteSpace(content))
                    throw new Exception("The server returned an empty response.");
                return content;
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            string status = response == null ? String.Empty :
                " (HTTP " + ((int)response.StatusCode).ToString() + ")";
            throw new Exception("Download failed" + status + ": " + ex.Message, ex);
        }
    }
}

internal sealed class WindObservation
{
    internal int Direction;
    internal int Speed;
    internal string Description;
    internal bool IsVariable;
}

public static class AutoWxPlanner
{
    private static readonly Regex WindPattern = new Regex(
        @"(?<![A-Z0-9])(?<direction>\d{3}|VRB)(?<speed>\d{2,3})(?:G\d{2,3})?(?<unit>KT|MPS|KMH)(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static AutoRunwayPlan Build(RunwayConfiguration configuration, Action<string> progress)
    {
        var plan = new AutoRunwayPlan();
        var metars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AutoWxSettings settings = configuration.AutoWx ?? new AutoWxSettings();

        foreach (AirportConfiguration airport in configuration.GetAirports())
        {
            if (airport.AutoWeather == null)
            {
                plan.Skipped.Add(airport.Code + ": no auto weather definition in runways.json.");
                continue;
            }

            string source = String.IsNullOrWhiteSpace(airport.AutoWeather.MetarFrom)
                ? airport.Code
                : airport.AutoWeather.MetarFrom;
            string metar;
            string failure;
            if (!metars.TryGetValue(source, out metar) && !failures.TryGetValue(source, out failure))
            {
                try
                {
                    if (progress != null)
                        progress("Auto WX: downloading VATSIM METAR for " + source + "...");
                    metar = NetworkTextDownloader.Download(
                        "https://metar.vatsim.net/metar.php?id=" + Uri.EscapeDataString(source),
                        "text/plain");
                    metars[source] = metar;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    failures[source] = failure;
                }
            }

            if (failures.TryGetValue(source, out failure))
            {
                plan.Skipped.Add(airport.Code + ": METAR " + source + " unavailable (" + failure + ").");
                continue;
            }

            RunwayState state;
            string details;
            if (!TrySelectState(airport, settings, metar, source, out state, out details))
            {
                plan.Skipped.Add(airport.Code + ": " + details);
                continue;
            }

            plan.Selections.Add(new AirportRunwaySelection
            {
                Airport = airport,
                State = state,
                Description = details
            });
        }

        return plan;
    }

    private static bool TrySelectState(
        AirportConfiguration airport,
        AutoWxSettings global,
        string metar,
        string source,
        out RunwayState state,
        out string details)
    {
        state = null;
        details = null;
        WindObservation wind;
        string error;
        int assumedDirection = airport.AutoWeather.AssumedWindDir ??
            global.AssumedWindDir ?? 270;
        if (!TryParseWind(metar, assumedDirection, out wind, out error))
        {
            details = "METAR " + source + " could not be used: " + error;
            return false;
        }

        int noFactorWind = airport.AutoWeather.NoFactorWind ??
            global.NoFactorWind ?? 5;
        int prefWind = airport.AutoWeather.PrefWind ?? global.PrefWind ?? 15;
        int direction = wind.Direction;

        // This deliberately mirrors arse: preferred-arrival airports use the
        // 15 kt threshold; all other airports use the 5 kt threshold.
        int minimumWind = airport.AutoWeather.PreferredArr == null
            ? noFactorWind
            : prefWind;
        bool usingAssumedForLightWind = wind.Speed < minimumWind;
        if (usingAssumedForLightWind)
            direction = assumedDirection;

        string dep;
        if (!TrySelectRunway(airport.AutoWeather, direction,
                airport.AutoWeather.PreferredDep, out dep, out error))
        {
            details = "could not choose DEP: " + error;
            return false;
        }

        string arr;
        if (!TrySelectRunway(airport.AutoWeather, direction,
                airport.AutoWeather.PreferredArr, out arr, out error))
        {
            details = "could not choose ARR: " + error;
            return false;
        }

        state = new RunwayState { Dep = dep, Arr = arr };
        string assumption = String.Empty;
        if (wind.IsVariable)
        {
            assumption = "; variable direction; using configured default " +
                assumedDirection.ToString("000") + " degrees";
            if (usingAssumedForLightWind)
                assumption += " (below " + minimumWind.ToString() + " kt threshold)";
        }
        else if (usingAssumedForLightWind)
        {
            assumption = "; below " + minimumWind.ToString() +
                " kt threshold; using configured default " +
                assumedDirection.ToString("000") + " degrees";
        }

        details = "METAR " + source + " " + wind.Description + assumption +
            " -> " + FormatState(state);
        return true;
    }

    private static bool TryParseWind(
        string raw,
        int assumedDirection,
        out WindObservation wind,
        out string error)
    {
        wind = null;
        error = null;
        string normalized = Regex.Replace(raw ?? String.Empty, @"(^|\s)//(?=\s|$)", "$1");
        Match match = WindPattern.Match(normalized);
        if (!match.Success)
        {
            error = "no supported wind group was found";
            return false;
        }

        int speed;
        if (!Int32.TryParse(match.Groups["speed"].Value, out speed))
        {
            error = "wind speed is invalid";
            return false;
        }

        string unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit == "MPS")
            speed *= 2;
        else if (unit == "KMH")
            speed /= 2;

        string directionToken = match.Groups["direction"].Value.ToUpperInvariant();
        int direction = assumedDirection;
        if (directionToken != "VRB")
        {
            if (!Int32.TryParse(directionToken, out direction) || direction < 0 || direction > 360)
            {
                error = "wind direction is invalid";
                return false;
            }
        }

        wind = new WindObservation
        {
            Direction = direction,
            Speed = speed,
            Description = directionToken + " " + speed.ToString() + "kt",
            IsVariable = directionToken == "VRB"
        };
        return true;
    }

    private static bool TrySelectRunway(
        AutoWeatherAirport airport,
        int windDirection,
        List<string> preferred,
        out string selected,
        out string error)
    {
        selected = null;
        error = null;

        if (preferred != null)
        {
            foreach (string preferredId in preferred)
            {
                AutoWeatherRunway runway = FindRunway(airport, preferredId);
                if (runway != null && GetWindDifference(runway.TrueHeading.Value, windDirection) < 90)
                {
                    selected = runway.Id;
                    return true;
                }
            }

            error = "no preferred runway has a headwind component";
            return false;
        }

        int difference = 180;
        foreach (AutoWeatherRunway runway in airport.Runways)
        {
            int candidateDifference = GetWindDifference(runway.TrueHeading.Value, windDirection);
            if (candidateDifference < difference)
            {
                difference = candidateDifference;
                selected = runway.Id;
            }
        }

        if (selected == null)
        {
            error = "no configured runway is available";
            return false;
        }

        return true;
    }

    private static AutoWeatherRunway FindRunway(AutoWeatherAirport airport, string id)
    {
        foreach (AutoWeatherRunway runway in airport.Runways)
            if (runway.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return runway;
        return null;
    }

    private static int GetWindDifference(int runwayHeading, int windDirection)
    {
        int rawDifference = Math.Abs(runwayHeading - windDirection);
        return Math.Min(360 - rawDifference, rawDifference);
    }

    internal static string FormatState(RunwayState state)
    {
        return state.Dep.Equals(state.Arr, StringComparison.OrdinalIgnoreCase)
            ? state.Dep
            : state.Dep + "/" + state.Arr;
    }
}

[DataContract]
public class VatsimAtis
{
    [DataMember(Name = "callsign")]
    public string Callsign { get; set; }

    [DataMember(Name = "text_atis")]
    public List<string> TextAtis { get; set; }

    [DataMember(Name = "atis_code")]
    public string AtisCode { get; set; }

    [DataMember(Name = "last_updated")]
    public string LastUpdated { get; set; }
}

[DataContract]
public class VatsimNetworkSnapshot
{
    [DataMember(Name = "atis")]
    public List<VatsimAtis> Atis { get; set; }
}

public static class VatsimAtisClient
{
    private const string AtisEndpoint = "https://data.vatsim.net/v3/afv-atis-data.json";
    private const string NetworkEndpoint = "https://data.vatsim.net/v3/vatsim-data.json";

    public static List<VatsimAtis> Download()
    {
        Exception compactFeedError;
        try
        {
            return Deserialize<List<VatsimAtis>>(
                NetworkTextDownloader.Download(AtisEndpoint, "application/json")) ??
                new List<VatsimAtis>();
        }
        catch (Exception ex)
        {
            compactFeedError = ex;
        }

        try
        {
            VatsimNetworkSnapshot snapshot = Deserialize<VatsimNetworkSnapshot>(
                NetworkTextDownloader.Download(NetworkEndpoint, "application/json"));
            if (snapshot == null || snapshot.Atis == null)
                throw new Exception("The VATSIM network feed did not contain an ATIS list.");
            return snapshot.Atis;
        }
        catch (Exception fallbackError)
        {
            throw new Exception(
                "Could not download VATSIM ATIS data. ATIS feed: " + compactFeedError.Message +
                "; network-feed fallback: " + fallbackError.Message,
                fallbackError);
        }
    }

    private static T Deserialize<T>(string json)
    {
        using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            return (T)serializer.ReadObject(stream);
        }
    }
}

internal enum AtisStationKind
{
    Combined,
    Departure,
    Arrival
}

internal sealed class AtisRunwayMention
{
    internal string Runway;
    internal bool Departure;
    internal bool Arrival;
    internal bool Generic;
}

internal sealed class PhaseRunwayCandidates
{
    internal string Value;
    internal bool Conflict;
    internal bool HasGenericEvidence;
    internal string Evidence;

    internal void Add(string runway, bool generic, string evidence)
    {
        if (Value == null)
        {
            Value = runway;
            Evidence = evidence;
        }
        else if (!Value.Equals(runway, StringComparison.OrdinalIgnoreCase))
        {
            Conflict = true;
        }

        HasGenericEvidence |= generic;
    }
}

public static class VatsimAtisPlanner
{
    private static readonly Regex NormalRunwayPattern = new Regex(
        @"(?<![A-Z0-9])RWY(?:S)?\s*(?<runway>\d\s*\d?(?:\s*[LCR])?)(?![A-Z0-9])",
        RegexOptions.CultureInvariant);
    private static readonly Regex InUseRunwayPattern = new Regex(
        @"(?<![A-Z0-9])RWY(?:S)?\s+(?:IN\s+USE|ACTIVE)\s*(?<runway>\d\s*\d?(?:\s*[LCR])?)(?![A-Z0-9])",
        RegexOptions.CultureInvariant);
    private static readonly Regex SpokenRunwayPattern = new Regex(
        @"(?<![A-Z0-9])RWY(?:S)?\s*(?<runway>(?:ZERO|ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|NINER)(?:[\s-]+(?:ZERO|ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|NINER))?(?:[\s-]+(?:LEFT|CENTER|CENTRE|RIGHT))?)(?![A-Z0-9])",
        RegexOptions.CultureInvariant);
    private static readonly Regex InUseSpokenRunwayPattern = new Regex(
        @"(?<![A-Z0-9])RWY(?:S)?\s+(?:IN\s+USE|ACTIVE)\s*(?<runway>(?:ZERO|ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|NINER)(?:[\s-]+(?:ZERO|ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|NINER))?(?:[\s-]+(?:LEFT|CENTER|CENTRE|RIGHT))?)(?![A-Z0-9])",
        RegexOptions.CultureInvariant);
    private static readonly Regex DeparturePattern = new Regex(
        @"\b(?:DEP|DEPG|DEPTG|DEPART(?:URE|URES|ING)?|TAKE\s*OFFS?)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex ArrivalPattern = new Regex(
        @"\b(?:ARR|ARRIV(?:AL|ALS|ING)?|LNDG|LAND(?:ING|INGS)?|APCH|APPROACH(?:ES)?|ILS|RNAV|VISUAL)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex DisallowedContextPattern = new Regex(
        @"\b(?:CLOSED|CLSD|PAPI|HOLD\s+SHORT|CAUTION|TAXI(?:WAY)?)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex ActiveRunwayPattern = new Regex(
        @"\b(?:IN\s+USE|ACTIVE|OPERATING)\b",
        RegexOptions.CultureInvariant);

    public static AutoRunwayPlan Build(RunwayConfiguration configuration, Action<string> progress)
    {
        if (progress != null)
            progress("Auto VATSIM: downloading current ATIS data...");

        List<VatsimAtis> allAtis = VatsimAtisClient.Download();
        return BuildFromAtis(configuration, allAtis);
    }

    // Kept separate from the download so the conservative ATIS interpretation
    // can also be exercised with a fixed VATSIM snapshot.
    public static AutoRunwayPlan BuildFromAtis(
        RunwayConfiguration configuration,
        IEnumerable<VatsimAtis> atisRecords)
    {
        var plan = new AutoRunwayPlan();
        var allAtis = new List<VatsimAtis>();
        if (atisRecords != null)
        {
            foreach (VatsimAtis atis in atisRecords)
                allAtis.Add(atis);
        }

        foreach (AirportConfiguration airport in configuration.GetAirports())
        {
            AirportRunwaySelection selection;
            string skipped;
            if (TryBuildSelection(airport, allAtis, out selection, out skipped))
                plan.Selections.Add(selection);
            else
                plan.Skipped.Add(airport.Code + ": " + skipped);
        }

        return plan;
    }

    private static bool TryBuildSelection(
        AirportConfiguration airport,
        List<VatsimAtis> allAtis,
        out AirportRunwaySelection selection,
        out string skipped)
    {
        selection = null;
        skipped = null;
        var records = new List<VatsimAtis>();
        foreach (VatsimAtis atis in allAtis)
        {
            AtisStationKind ignored;
            if (atis != null && TryGetStationKind(airport.Code, atis.Callsign, out ignored))
                records.Add(atis);
        }

        if (records.Count == 0)
        {
            skipped = "no matching VATSIM ATIS is online.";
            return false;
        }

        var departure = new PhaseRunwayCandidates();
        var arrival = new PhaseRunwayCandidates();
        List<string> configuredRunways = RunwayConfiguration.GetConfiguredRunways(airport);
        foreach (VatsimAtis atis in records)
        {
            AtisStationKind kind;
            TryGetStationKind(airport.Code, atis.Callsign, out kind);
            if (atis.TextAtis == null)
                continue;

            foreach (string rawLine in atis.TextAtis)
            {
                List<AtisRunwayMention> mentions = ParseLine(
                    rawLine, configuredRunways, kind != AtisStationKind.Combined);
                foreach (AtisRunwayMention mention in mentions)
                    AddMention(kind, mention, atis.Callsign, departure, arrival);
            }
        }

        if (departure.Conflict || arrival.Conflict)
        {
            skipped = "ATIS reports conflicting " +
                (departure.Conflict && arrival.Conflict ? "DEP and ARR" :
                departure.Conflict ? "DEP" : "ARR") + " runways.";
            return false;
        }

        if (departure.Value == null && arrival.Value == null)
        {
            skipped = "ATIS is online, but no safe active-runway phrase matched this configuration.";
            return false;
        }

        RunwayState state;
        if (departure.Value != null && arrival.Value != null)
        {
            if (departure.HasGenericEvidence || arrival.HasGenericEvidence)
            {
                if (!TryFindExactPreset(airport, departure.Value, arrival.Value, out state))
                {
                    skipped = "generic \"runway in use\" wording does not map to one configured preset.";
                    return false;
                }
            }
            else
            {
                // Both phases were stated explicitly. The exact pair may be a
                // valid operational state even if it is not one of the two UI
                // preset labels, but both designators were checked against the
                // configuration before reaching this point.
                state = new RunwayState { Dep = departure.Value, Arr = arrival.Value };
            }
        }
        else if (departure.Value != null)
        {
            if (!TryFindUniquePreset(airport, true, departure.Value, out state))
            {
                skipped = "ATIS states only DEP " + departure.Value +
                    "; it does not identify one configured DEP/ARR preset.";
                return false;
            }
        }
        else
        {
            if (!TryFindUniquePreset(airport, false, arrival.Value, out state))
            {
                skipped = "ATIS states only ARR " + arrival.Value +
                    "; it does not identify one configured DEP/ARR preset.";
                return false;
            }
        }

        selection = new AirportRunwaySelection
        {
            Airport = airport,
            State = state,
            Description = "VATSIM " + JoinCallsigns(records) + " -> " +
                AutoWxPlanner.FormatState(state)
        };
        return true;
    }

    private static bool TryGetStationKind(string airport, string callsign, out AtisStationKind kind)
    {
        kind = AtisStationKind.Combined;
        if (String.IsNullOrWhiteSpace(callsign))
            return false;

        string upper = callsign.Trim().ToUpperInvariant();
        if (upper.Equals(airport + "_ATIS", StringComparison.OrdinalIgnoreCase))
            return true;
        if (upper.Equals(airport + "_D_ATIS", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtisStationKind.Departure;
            return true;
        }
        if (upper.Equals(airport + "_A_ATIS", StringComparison.OrdinalIgnoreCase))
        {
            kind = AtisStationKind.Arrival;
            return true;
        }
        return false;
    }

    private static void AddMention(
        AtisStationKind stationKind,
        AtisRunwayMention mention,
        string callsign,
        PhaseRunwayCandidates departure,
        PhaseRunwayCandidates arrival)
    {
        if (stationKind == AtisStationKind.Departure)
        {
            departure.Add(mention.Runway, false, callsign);
            return;
        }
        if (stationKind == AtisStationKind.Arrival)
        {
            arrival.Add(mention.Runway, false, callsign);
            return;
        }

        if (mention.Departure)
            departure.Add(mention.Runway, mention.Generic, callsign);
        if (mention.Arrival)
            arrival.Add(mention.Runway, mention.Generic, callsign);
    }

    private static List<AtisRunwayMention> ParseLine(
        string rawLine,
        List<string> configuredRunways,
        bool allowBareRunway)
    {
        var result = new List<AtisRunwayMention>();
        string line = NormalizeAtisLine(rawLine);
        if (line.Length == 0)
            return result;

        var specialOffsets = new HashSet<int>();
        foreach (Match match in InUseRunwayPattern.Matches(line))
        {
            specialOffsets.Add(match.Index);
            AddMentionFromMatch(result, line, match, true, configuredRunways, allowBareRunway);
        }
        foreach (Match match in InUseSpokenRunwayPattern.Matches(line))
        {
            specialOffsets.Add(match.Index);
            AddMentionFromMatch(result, line, match, true, configuredRunways, allowBareRunway);
        }

        foreach (Match match in NormalRunwayPattern.Matches(line))
        {
            if (specialOffsets.Contains(match.Index))
                continue;
            AddMentionFromMatch(result, line, match, false, configuredRunways, allowBareRunway);
        }
        foreach (Match match in SpokenRunwayPattern.Matches(line))
        {
            if (specialOffsets.Contains(match.Index))
                continue;
            AddMentionFromMatch(result, line, match, false, configuredRunways, allowBareRunway);
        }

        return result;
    }

    private static void AddMentionFromMatch(
        List<AtisRunwayMention> result,
        string line,
        Match match,
        bool inUseSyntax,
        List<string> configuredRunways,
        bool allowBareRunway)
    {
        string runway = NormalizeAtisRunway(match.Groups["runway"].Value);
        if (!ContainsRunway(configuredRunways, runway))
            return;

        string before = GetBeforeContext(line, match.Index);
        string after = GetAfterContext(line, match.Index + match.Length);
        if (IsDisallowedContext(before, after))
            return;

        bool departure = DeparturePattern.IsMatch(before);
        bool arrival = ArrivalPattern.IsMatch(before);
        if (!departure && !arrival)
        {
            departure = DeparturePattern.IsMatch(after);
            arrival = ArrivalPattern.IsMatch(after);
        }

        bool generic = false;
        if (!departure && !arrival &&
            (inUseSyntax || ActiveRunwayPattern.IsMatch(before + " " + after)))
        {
            departure = true;
            arrival = true;
            generic = true;
        }

        // D_ATIS and A_ATIS may intentionally contain just "RWY 25". The
        // callsign itself supplies the phase, so it is safe to retain that
        // otherwise context-free, exact configured designator.
        if (!departure && !arrival && allowBareRunway)
            departure = true;

        if (!departure && !arrival)
            return;

        result.Add(new AtisRunwayMention
        {
            Runway = runway,
            Departure = departure,
            Arrival = arrival,
            Generic = generic
        });
    }

    private static bool TryFindExactPreset(
        AirportConfiguration airport,
        string dep,
        string arr,
        out RunwayState state)
    {
        state = null;
        if (StateMatches(airport.West, dep, arr))
        {
            state = airport.West;
            return true;
        }
        if (StateMatches(airport.East, dep, arr))
        {
            state = airport.East;
            return true;
        }
        return false;
    }

    private static bool TryFindUniquePreset(
        AirportConfiguration airport,
        bool isDeparture,
        string runway,
        out RunwayState state)
    {
        state = null;
        RunwayState first = null;
        RunwayState[] presets = new RunwayState[] { airport.West, airport.East };
        foreach (RunwayState preset in presets)
        {
            string candidate = isDeparture ? preset.Dep : preset.Arr;
            if (!candidate.Equals(runway, StringComparison.OrdinalIgnoreCase))
                continue;

            if (first == null)
                first = preset;
            else if (!StateMatches(first, preset.Dep, preset.Arr))
                return false;
        }

        state = first;
        return state != null;
    }

    private static bool StateMatches(RunwayState state, string dep, string arr)
    {
        return state.Dep.Equals(dep, StringComparison.OrdinalIgnoreCase) &&
               state.Arr.Equals(arr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsRunway(List<string> runways, string candidate)
    {
        foreach (string runway in runways)
            if (runway.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string NormalizeAtisLine(string rawLine)
    {
        string line = (rawLine ?? String.Empty).Trim().ToUpperInvariant();
        line = Regex.Replace(line, @"\bRUNWAYS?\b", "RWY");
        line = Regex.Replace(line, @"\bR\s*W\s*Y(?=\s|\d|$)", "RWY");
        line = Regex.Replace(line, @"\bR\s*Y(?=\s|\d|$)", "RWY");
        return Regex.Replace(line, @"\s+", " ");
    }

    private static string NormalizeAtisRunway(string rawValue)
    {
        string numeric = RunwayIdentifier.Normalize(rawValue);
        if (Regex.IsMatch(numeric, @"^\d{2}[LCR]?$") )
            return numeric;

        string[] tokens = (rawValue ?? String.Empty).ToUpperInvariant().Replace("-", " ")
            .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string digits = String.Empty;
        string suffix = String.Empty;
        foreach (string token in tokens)
        {
            string digit = SpokenDigit(token);
            if (digit != null && suffix.Length == 0 && digits.Length < 2)
            {
                digits += digit;
                continue;
            }

            if (digits.Length > 0 && suffix.Length == 0)
            {
                if (token == "LEFT") suffix = "L";
                else if (token == "CENTER" || token == "CENTRE") suffix = "C";
                else if (token == "RIGHT") suffix = "R";
                else return numeric;
                continue;
            }

            return numeric;
        }

        return digits.Length == 0 ? numeric : RunwayIdentifier.Normalize(digits + suffix);
    }

    private static string SpokenDigit(string token)
    {
        switch (token)
        {
            case "ZERO": return "0";
            case "ONE": return "1";
            case "TWO": return "2";
            case "THREE": return "3";
            case "FOUR": return "4";
            case "FIVE": return "5";
            case "SIX": return "6";
            case "SEVEN": return "7";
            case "EIGHT": return "8";
            case "NINE":
            case "NINER": return "9";
            default: return null;
        }
    }

    private static bool IsDisallowedContext(string before, string after)
    {
        if (DisallowedContextPattern.IsMatch(before + " " + after))
            return true;
        return Regex.IsMatch(before, @"\bWIND\s*$", RegexOptions.CultureInvariant);
    }

    private static string GetBeforeContext(string line, int index)
    {
        int start = 0;
        if (index > 0)
        {
            int previousRunway = line.LastIndexOf("RWY", index - 1, StringComparison.Ordinal);
            if (previousRunway >= 0)
                start = previousRunway + 3;

            int punctuation = LastPunctuation(line, index - 1);
            if (punctuation >= 0 && punctuation + 1 > start)
                start = punctuation + 1;
        }
        return line.Substring(start, index - start);
    }

    private static string GetAfterContext(string line, int index)
    {
        int end = line.Length;
        int nextRunway = line.IndexOf("RWY", index, StringComparison.Ordinal);
        if (nextRunway >= 0)
            end = nextRunway;

        int punctuation = FirstPunctuation(line, index);
        if (punctuation >= 0 && punctuation < end)
            end = punctuation;
        return line.Substring(index, end - index);
    }

    private static int LastPunctuation(string line, int startIndex)
    {
        for (int i = startIndex; i >= 0; i--)
            if (line[i] == '.' || line[i] == ';' || line[i] == ':')
                return i;
        return -1;
    }

    private static int FirstPunctuation(string line, int startIndex)
    {
        for (int i = startIndex; i < line.Length; i++)
            if (line[i] == '.' || line[i] == ';' || line[i] == ':')
                return i;
        return -1;
    }

    private static string JoinCallsigns(List<VatsimAtis> records)
    {
        var values = new List<string>();
        foreach (VatsimAtis record in records)
        {
            if (!String.IsNullOrWhiteSpace(record.Callsign) && !values.Contains(record.Callsign))
                values.Add(record.Callsign);
        }
        return String.Join(", ", values.ToArray());
    }
}

public class MainForm : Form
{
    private FlowLayoutPanel panel;
    private Label status;
    private readonly RunwayConfiguration configuration;
    private readonly Dictionary<string, Button> presetButtons =
        new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
    // Only this collection is eligible for a write to EuroScope.  Selecting a
    // tile, an all-airports command, or an automatic source never clicks the
    // EuroScope selector by itself.
    private readonly Dictionary<string, AirportRunwaySelection> pendingSelections =
        new Dictionary<string, AirportRunwaySelection>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activePresetKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool currentStatesKnown;
    private readonly List<Button> actionButtons = new List<Button>();
    private Button applyButton;

    public MainForm(RunwayConfiguration configuration)
    {
        this.configuration = configuration;
        SuspendLayout();

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96.0f, 96.0f);
        Text = "EuroScope Runway Preset Manager";
        ClientSize = new Size(1020, 470);
        MinimumSize = new Size(720, 400);
        Padding = new Padding(12);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel();
        root.ColumnCount = 1;
        root.RowCount = 3;
        root.Dock = DockStyle.Fill;
        root.Margin = new Padding(0);
        root.Padding = new Padding(0);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        panel = new FlowLayoutPanel();
        panel.FlowDirection = FlowDirection.LeftToRight;
        panel.WrapContents = true;
        panel.AutoScroll = true;
        panel.Dock = DockStyle.Fill;
        panel.Margin = new Padding(0);
        panel.Padding = new Padding(0);
        root.Controls.Add(panel, 0, 0);

        foreach (RunwayColumn column in configuration.Columns)
            panel.Controls.Add(BuildGroupColumn(column));

        status = new Label();
        status.Text = "Choose preset tiles, then click APPLY. Green = current EuroScope state; yellow = selected change.";
        status.AutoSize = false;
        status.Height = 34;
        status.MinimumSize = new Size(0, 34);
        status.AutoEllipsis = true;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Margin = new Padding(0, 8, 0, 4);
        status.Dock = DockStyle.Fill;
        root.Controls.Add(status, 0, 1);

        root.Controls.Add(BuildCommandPanel(), 0, 2);

        this.Shown += (s,e) => RefreshCurrentStates();
        ResumeLayout(true);
    }

    private Control BuildCommandPanel()
    {
        var commands = new TableLayoutPanel();
        commands.ColumnCount = 3;
        commands.RowCount = 2;
        commands.AutoSize = true;
        commands.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        commands.Dock = DockStyle.Fill;
        commands.Margin = new Padding(0);
        commands.Padding = new Padding(0);
        commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334f));
        commands.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commands.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        commands.Controls.Add(CreateCommandButton("All RWY West", (s, e) => SelectAllConfiguration(false)), 0, 0);
        commands.Controls.Add(CreateCommandButton("All RWY East", (s, e) => SelectAllConfiguration(true)), 1, 0);
        commands.Controls.Add(CreateCommandButton("Auto WX based", (s, e) => ApplyAutoWeather()), 2, 0);
        commands.Controls.Add(CreateCommandButton("Auto Current VATSIM", (s, e) => ApplyAutoVatsim()), 0, 1);
        applyButton = CreateCommandButton("APPLY", (s, e) => ApplyPendingSelections());
        commands.Controls.Add(applyButton, 1, 1);
        commands.Controls.Add(CreateCommandButton("Refresh", (s, e) => RefreshCurrentStates()), 2, 1);
        UpdateApplyButton();
        return commands;
    }

    private Button CreateCommandButton(string text, EventHandler click)
    {
        var button = new Button();
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(0, 34);
        button.Padding = new Padding(10, 3, 10, 3);
        button.Margin = new Padding(2);
        button.Dock = DockStyle.Fill;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Click += click;
        RegisterActionButton(button);
        return button;
    }

    private Control BuildGroupColumn(RunwayColumn column)
    {
        var col = new FlowLayoutPanel();
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.AutoScroll = false;
        col.AutoSize = true;
        col.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        col.MinimumSize = new Size(188, 0);
        col.Margin = new Padding(0, 0, 8, 8);

        foreach (AirportConfiguration airport in column.Airports)
            col.Controls.Add(BuildAirportRow(airport));

        return col;
    }

    private Control BuildAirportRow(AirportConfiguration airport)
    {
        var box = new GroupBox();
        box.Text = airport.Code;
        box.Width = 188;
        box.Height = 70;
        box.Margin = new Padding(0, 0, 0, 4);

        box.Controls.Add(BuildPresetButton(airport, airport.West, "west", new Point(12, 27)));
        box.Controls.Add(BuildPresetButton(airport, airport.East, "east", new Point(96, 27)));

        return box;
    }

    private Button BuildPresetButton(AirportConfiguration airport, RunwayState state, string side, Point location)
    {
        string key = airport.Code + ":" + side;
        var button = new Button();
        button.Text = FormatPresetLabel(state);
        button.Width = 76;
        button.Height = 30;
        button.Location = location;
        button.Tag = new PresetButtonData { Airport = airport, State = state, Key = key };
        button.Click += PresetClick;
        presetButtons[key] = button;
        RegisterActionButton(button);
        return button;
    }

    private static string FormatPresetLabel(RunwayState state)
    {
        return AutoWxPlanner.FormatState(state);
    }

    private void PresetClick(object sender, EventArgs e)
    {
        var btn = (Button)sender;
        var data = (PresetButtonData)btn.Tag;

        AirportRunwaySelection existing;
        if (pendingSelections.TryGetValue(data.Airport.Code, out existing) &&
            StatesEqual(existing.State, data.State))
        {
            pendingSelections.Remove(data.Airport.Code);
            status.Text = "Selection cleared for " + data.Airport.Code + ".";
        }
        else
        {
            pendingSelections[data.Airport.Code] = new AirportRunwaySelection
            {
                Airport = data.Airport,
                State = data.State,
                Description = "Manual preset " + FormatPresetLabel(data.State)
            };
            status.Text = "Selected " + data.Airport.Code + " " +
                FormatPresetLabel(data.State) + ". Click APPLY to change EuroScope.";
        }

        RenderPresetMarks();
        UpdateApplyButton();
    }

    private void SelectAllConfiguration(bool east)
    {
        pendingSelections.Clear();
        foreach (AirportConfiguration airport in configuration.GetAirports())
        {
            RunwayState state = east ? airport.East : airport.West;
            pendingSelections[airport.Code] = new AirportRunwaySelection
            {
                Airport = airport,
                State = state,
                Description = east ? "All RWY East" : "All RWY West"
            };
        }

        bool currentStatesRead = RefreshCurrentStates(false);
        RenderPresetMarks();
        UpdateApplyButton();
        status.Text = (east ? "All RWY East" : "All RWY West") + ": selected " +
            pendingSelections.Count.ToString() + " airport(s); EuroScope was not changed. " +
            (currentStatesRead
                ? "Click APPLY to apply the selection."
                : "Current EuroScope state could not be read; open the selector and Refresh before APPLY.");
    }

    private void ApplyAutoWeather()
    {
        EnableButtons(false);
        try
        {
            AutoRunwayPlan plan = AutoWxPlanner.Build(configuration, ReportProgress);
            SelectAutomaticPlan("Auto WX based", plan);
        }
        catch (Exception ex)
        {
            status.Text = "ERROR: " + ex.Message;
        }
        finally
        {
            EnableButtons(true);
        }
    }

    private void ApplyAutoVatsim()
    {
        EnableButtons(false);
        try
        {
            AutoRunwayPlan plan = VatsimAtisPlanner.Build(configuration, ReportProgress);
            SelectAutomaticPlan("Auto Current VATSIM", plan);
        }
        catch (Exception ex)
        {
            status.Text = "ERROR: " + ex.Message;
        }
        finally
        {
            EnableButtons(true);
        }
    }

    private void SelectAutomaticPlan(string name, AutoRunwayPlan plan)
    {
        // A partial ATIS/METAR plan is an overlay, never a replacement for the
        // controller's existing selection.  In particular, a missing ATIS for
        // one airport must not clear proposed changes for every other airport.
        bool refreshed = RefreshCurrentStates(false);
        if (!refreshed || !currentStatesKnown)
        {
            RenderPresetMarks();
            UpdateApplyButton();
            status.Text = name + ": the current EuroScope state could not be refreshed, so no " +
                "automatic tiles were changed. Open the selector and click Refresh first; " +
                "your existing selections and green tiles were kept.";
            return;
        }

        int updated = 0;
        int alreadyCurrent = 0;
        int alreadySelected = 0;

        foreach (AirportRunwaySelection selection in plan.Selections)
        {
            string key;
            if (selection == null || selection.Airport == null || selection.State == null ||
                !TryGetPresetKey(selection.Airport, selection.State, out key))
            {
                string airport = selection == null || selection.Airport == null
                    ? "Unknown airport"
                    : selection.Airport.Code;
                string state = selection == null || selection.State == null
                    ? "invalid runway state"
                    : FormatPresetLabel(selection.State);
                plan.Skipped.Add(airport + ": " + state +
                    " does not match a visible preset tile.");
                continue;
            }

            // A matching green tile already represents the requested state.
            // Do not add a needless pending change or replace an existing
            // manual choice for that airport.
            if (activePresetKeys.Contains(key))
            {
                alreadyCurrent++;
                continue;
            }

            AirportRunwaySelection existing;
            if (pendingSelections.TryGetValue(selection.Airport.Code, out existing) &&
                StatesEqual(existing.State, selection.State))
            {
                alreadySelected++;
                continue;
            }

            pendingSelections[selection.Airport.Code] = selection;
            updated++;
        }

        RenderPresetMarks();
        UpdateApplyButton();

        status.Text = name + ": " + updated.ToString() + " changed selection(s), " +
            alreadyCurrent.ToString() + " already match EuroScope" +
            (alreadySelected == 0 ? String.Empty : ", " + alreadySelected.ToString() + " already selected") +
            (plan.Skipped.Count == 0 ? "." : "; skipped " + plan.Skipped.Count.ToString() + ".") +
            " Existing selections and green current tiles were kept. EuroScope was not changed." +
            " Click APPLY to apply the selected changes.";
    }

    private void ApplyPendingSelections()
    {
        List<AirportRunwaySelection> selections = GetPendingSelectionsInConfigurationOrder();
        if (selections.Count == 0)
        {
            status.Text = "No preset tiles are selected. Choose tiles, then click APPLY.";
            UpdateApplyButton();
            return;
        }

        if (!ConfirmApply(selections.Count))
            return;

        EnableButtons(false);
        try
        {
            status.Text = "APPLY: changing " + selections.Count.ToString() + " selected airport(s)...";
            Application.DoEvents();

            var mgr = new EuroScopeManager(configuration);
            mgr.ApplyStates(selections);

            string verification;
            if (!mgr.VerifyStates(selections, out verification))
                throw new Exception("Verification failed: " + verification);

            pendingSelections.Clear();
            bool refreshed = RefreshCurrentStates(false);
            status.Text = "APPLY: verified " + selections.Count.ToString() + " airport(s)." +
                (refreshed ? String.Empty : " Open the selector and Refresh to read the current state again.");
        }
        catch (Exception ex)
        {
            status.Text = "ERROR: " + ex.Message +
                " Selected tiles were kept; inspect EuroScope, then Refresh before retrying APPLY.";
        }
        finally
        {
            RenderPresetMarks();
            EnableButtons(true);
        }
    }

    private List<AirportRunwaySelection> GetPendingSelectionsInConfigurationOrder()
    {
        var selections = new List<AirportRunwaySelection>();
        foreach (AirportConfiguration airport in configuration.GetAirports())
        {
            AirportRunwaySelection selection;
            if (pendingSelections.TryGetValue(airport.Code, out selection))
                selections.Add(selection);
        }
        return selections;
    }

    private void ReportProgress(string message)
    {
        status.Text = message;
        Application.DoEvents();
    }

    private void RefreshCurrentStates()
    {
        RefreshCurrentStates(true);
    }

    private bool RefreshCurrentStates(bool updateStatus)
    {
        // Read into a temporary set.  A failed refresh must not erase the last
        // known green state while the controller is preparing a proposal.
        var refreshedActivePresetKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var mgr = new EuroScopeManager(configuration);

            foreach (AirportConfiguration airport in configuration.GetAirports())
            {
                string details;
                if (mgr.VerifyPreset(airport, airport.West, out details))
                    refreshedActivePresetKeys.Add(airport.Code + ":west");
                else if (IsStateReadFailure(details))
                    throw new Exception(details);

                if (mgr.VerifyPreset(airport, airport.East, out details))
                    refreshedActivePresetKeys.Add(airport.Code + ":east");
                else if (IsStateReadFailure(details))
                    throw new Exception(details);
            }

            activePresetKeys.Clear();
            foreach (string key in refreshedActivePresetKeys)
                activePresetKeys.Add(key);
            currentStatesKnown = true;
            RenderPresetMarks();
            if (updateStatus)
                status.Text = "Current EuroScope runway states refreshed.";
            return true;
        }
        catch (Exception ex)
        {
            RenderPresetMarks();
            if (updateStatus)
            {
                status.Text = ex.Message.Equals(
                    "EuroScope runway selector dialog not found.",
                    StringComparison.OrdinalIgnoreCase)
                    ? "EuroScope runway selector is not open. Open the Active airport/runway selector window in EuroScope, then click Refresh."
                    : "State read failed: " + ex.Message;
            }
            return false;
        }
    }

    private static bool IsStateReadFailure(string details)
    {
        return !String.IsNullOrWhiteSpace(details) &&
            details.StartsWith("cannot read ", StringComparison.OrdinalIgnoreCase);
    }

    private void RenderPresetMarks()
    {
        foreach (var kv in presetButtons)
        {
            Button b = kv.Value;
            b.BackColor = SystemColors.Control;
            b.UseVisualStyleBackColor = true;
        }

        foreach (string key in activePresetKeys)
            MarkButton(key, Color.LightGreen);

        foreach (AirportRunwaySelection selection in pendingSelections.Values)
        {
            string key;
            if (TryGetPresetKey(selection.Airport, selection.State, out key) &&
                !activePresetKeys.Contains(key))
                MarkButton(key, Color.Khaki);
        }
    }

    private static bool TryGetPresetKey(
        AirportConfiguration airport,
        RunwayState state,
        out string key)
    {
        key = null;
        if (airport == null || state == null)
            return false;

        if (StatesEqual(airport.West, state))
        {
            key = airport.Code + ":west";
            return true;
        }
        if (StatesEqual(airport.East, state))
        {
            key = airport.Code + ":east";
            return true;
        }
        return false;
    }

    private static bool StatesEqual(RunwayState first, RunwayState second)
    {
        return first != null && second != null &&
            RunwayIdentifier.Normalize(first.Dep).Equals(
                RunwayIdentifier.Normalize(second.Dep), StringComparison.OrdinalIgnoreCase) &&
            RunwayIdentifier.Normalize(first.Arr).Equals(
                RunwayIdentifier.Normalize(second.Arr), StringComparison.OrdinalIgnoreCase);
    }

    private void MarkButton(string key, Color color)
    {
        Button b;
        if (!presetButtons.TryGetValue(key, out b))
            return;

        b.UseVisualStyleBackColor = false;
        b.BackColor = color;
    }

    private bool ConfirmApply(int selectionCount)
    {
        DialogResult result = MessageBox.Show(
            this,
            "APPLY will now change " + selectionCount.ToString() +
            " selected airport configuration(s) in the EuroScope runway list. " +
            "Every requested state will be verified after it is applied. " +
            "Keep the Active airport/runway selector open until the operation is complete.",
            "EuroScope Runway Preset Manager",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        return result == DialogResult.OK;
    }

    private void EnableButtons(bool enabled)
    {
        foreach (Button button in actionButtons)
            button.Enabled = enabled;
        if (enabled)
            UpdateApplyButton();
    }

    private void UpdateApplyButton()
    {
        if (applyButton != null)
            applyButton.Enabled = pendingSelections.Count > 0;
    }

    private void RegisterActionButton(Button button)
    {
        actionButtons.Add(button);
    }

}

public class EuroScopeManager
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetPhysicalCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetPhysicalCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(
        uint nInputs,
        [In] INPUT[] pInputs,
        int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private readonly RunwayConfiguration configuration;

    public EuroScopeManager(RunwayConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public void ApplyAllConfiguration(bool opposite)
    {
        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd == IntPtr.Zero)
            throw new Exception("EuroScope runway selector dialog not found.");

        AutomationElement dlg = AutomationElement.FromHandle(dlgHwnd);
        if (dlg == null)
            throw new Exception("Could not attach to EuroScope dialog.");

        AutomationElement grid = dlg.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1012")
        );
        if (grid == null)
            throw new Exception("Runway list not found.");

        foreach (AirportConfiguration airport in configuration.GetAirports())
            ApplyAirportState(dlgHwnd, grid, airport, opposite ? airport.East : airport.West);
    }

    public void ApplyStates(IEnumerable<AirportRunwaySelection> selections)
    {
        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd == IntPtr.Zero)
            throw new Exception("EuroScope runway selector dialog not found.");

        AutomationElement dlg = AutomationElement.FromHandle(dlgHwnd);
        if (dlg == null)
            throw new Exception("Could not attach to EuroScope dialog.");

        AutomationElement grid = dlg.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1012")
        );
        if (grid == null)
            throw new Exception("Runway list not found.");

        foreach (AirportRunwaySelection selection in selections)
        {
            if (selection == null || selection.Airport == null || selection.State == null)
                throw new Exception("Auto selection contains an invalid airport state.");
            ApplyAirportState(dlgHwnd, grid, selection.Airport, selection.State);
        }
    }

    public bool VerifyAllConfiguration(bool opposite, out string details)
    {
        AutomationElement grid = GetGrid();
        foreach (AirportConfiguration airport in configuration.GetAirports())
        {
            RunwayState selected = opposite ? airport.East : airport.West;
            if (!VerifyPreset(grid, airport, selected, out details))
            {
                details = airport.Code + ": " + details;
                return false;
            }
        }

        details = "OK";
        return true;
    }

    public bool VerifyStates(IEnumerable<AirportRunwaySelection> selections, out string details)
    {
        AutomationElement grid = GetGrid();
        foreach (AirportRunwaySelection selection in selections)
        {
            if (selection == null || selection.Airport == null || selection.State == null)
            {
                details = "auto selection contains an invalid airport state";
                return false;
            }

            if (!VerifyPreset(grid, selection.Airport, selection.State, out details))
            {
                details = selection.Airport.Code + ": " + details;
                return false;
            }
        }

        details = "OK";
        return true;
    }

    private AutomationElement GetGrid()
    {
        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd == IntPtr.Zero)
            throw new Exception("EuroScope runway selector dialog not found.");

        AutomationElement dlg = AutomationElement.FromHandle(dlgHwnd);
        if (dlg == null)
            throw new Exception("Could not attach to EuroScope dialog.");

        AutomationElement grid = dlg.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1012")
        );
        if (grid == null)
            throw new Exception("Runway list not found.");

        return grid;
    }

    private IntPtr GetMsaaGridHandle(IntPtr dlgHwnd, AutomationElement grid)
    {
        IntPtr hwnd = GetDlgItem(dlgHwnd, 1012);
        if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            return hwnd;

        try
        {
            hwnd = new IntPtr(grid.Current.NativeWindowHandle);
            return hwnd != IntPtr.Zero && IsWindow(hwnd) ? hwnd : IntPtr.Zero;
        }
        catch (ElementNotAvailableException)
        {
            return IntPtr.Zero;
        }
    }

    public bool TryGetRunwayState(string airport, string runway, out bool depActive, out bool arrActive)
    {
        return TryGetRunwayState(GetGrid(), airport, runway, out depActive, out arrActive);
    }

    private bool TryGetRunwayState(
        AutomationElement grid,
        string airport,
        string runway,
        out bool depActive,
        out bool arrActive)
    {
        depActive = false;
        arrActive = false;

        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd != IntPtr.Zero)
        {
            string msaaError;
            MsaaGridActivator.Result msaaResult = MsaaGridActivator.TryGetRunwayState(
                GetMsaaGridHandle(dlgHwnd, grid), airport, runway,
                out depActive, out arrActive, out msaaError);
            if (msaaResult == MsaaGridActivator.Result.Success)
                return true;
            if (msaaResult == MsaaGridActivator.Result.Unsafe)
                return false;
        }

        AutomationElement row = FindRunwayRow(grid, airport, runway);
        if (row == null)
            return false;

        AutomationElementCollection cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
        if (cells.Count < 6)
            return false;

        depActive = SafeName(cells[4]).Equals("ICON:1", StringComparison.OrdinalIgnoreCase);
        arrActive = SafeName(cells[5]).Equals("ICON:2", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public bool VerifyPreset(AirportConfiguration airport, RunwayState selected, out string details)
    {
        return VerifyPreset(GetGrid(), airport, selected, out details);
    }

    private bool VerifyPreset(
        AutomationElement grid,
        AirportConfiguration airport,
        RunwayState selected,
        out string details)
    {
        foreach (string runway in GetConfiguredRunways(airport))
        {
            bool dep, arr;
            if (!TryGetRunwayState(grid, airport.Code, runway, out dep, out arr))
            {
                details = "cannot read " + airport.Code + " " + runway;
                return false;
            }

            bool depWanted = runway.Equals(selected.Dep, StringComparison.OrdinalIgnoreCase);
            bool arrWanted = runway.Equals(selected.Arr, StringComparison.OrdinalIgnoreCase);

            if (dep != depWanted || arr != arrWanted)
            {
                details = runway + " DEP=" + dep + " ARR=" + arr +
                          " expected DEP=" + depWanted + " ARR=" + arrWanted;
                return false;
            }
        }

        details = "OK";
        return true;
    }

    public void ApplyPreset(AirportConfiguration airport, RunwayState selected)
    {
        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd == IntPtr.Zero)
            throw new Exception("EuroScope runway selector dialog not found.");

        AutomationElement dlg = AutomationElement.FromHandle(dlgHwnd);
        if (dlg == null)
            throw new Exception("Could not attach to EuroScope dialog.");

        AutomationElement grid = dlg.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1012")
        );
        if (grid == null)
            throw new Exception("Runway list not found.");

        ApplyAirportState(dlgHwnd, grid, airport, selected);
    }

    private void ApplyAirportState(
        IntPtr dlgHwnd,
        AutomationElement grid,
        AirportConfiguration airport,
        RunwayState selected)
    {
        ValidateSelectedState(airport, selected);
        foreach (string runway in GetConfiguredRunways(airport))
            SetRunwayState(
                dlgHwnd,
                grid,
                airport.Code,
                runway,
                runway.Equals(selected.Dep, StringComparison.OrdinalIgnoreCase),
                runway.Equals(selected.Arr, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> GetConfiguredRunways(AirportConfiguration airport)
    {
        return RunwayConfiguration.GetConfiguredRunways(airport);
    }

    private void ValidateSelectedState(AirportConfiguration airport, RunwayState selected)
    {
        if (selected == null || String.IsNullOrWhiteSpace(selected.Dep) || String.IsNullOrWhiteSpace(selected.Arr))
            throw new Exception("Selected runway state for " + airport.Code + " is invalid.");

        List<string> available = RunwayConfiguration.GetConfiguredRunways(airport);
        string dep = RunwayIdentifier.Normalize(selected.Dep);
        string arr = RunwayIdentifier.Normalize(selected.Arr);
        if (!available.Contains(dep) || !available.Contains(arr))
            throw new Exception("Selected runway state " + dep + "/" + arr +
                " is not configured for " + airport.Code + ".");

        selected.Dep = dep;
        selected.Arr = arr;
    }

    private AutomationElement FindRunwayRow(AutomationElement grid, string airport, string runway)
    {
        AutomationElementCollection rows = grid.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)
        );

        foreach (AutomationElement row in rows)
        {
            AutomationElementCollection cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
            if (cells.Count < 6) continue;

            string apt = SafeName(cells[0]).Trim();
            string rwy = SafeName(cells[3]).Trim();

            if (apt.Equals(airport, StringComparison.OrdinalIgnoreCase) &&
                rwy.Equals(runway, StringComparison.OrdinalIgnoreCase))
                return row;
        }

        return null;
    }

    private void SetRunwayState(
        IntPtr dlgHwnd,
        AutomationElement grid,
        string airport,
        string runway,
        bool depWanted,
        bool arrWanted)
    {
        string msaaError;
        MsaaGridActivator.Result msaaResult = MsaaGridActivator.TrySetRunwayState(
            GetMsaaGridHandle(dlgHwnd, grid), airport, runway,
            depWanted, arrWanted, out msaaError);
        if (msaaResult == MsaaGridActivator.Result.Success)
            return;
        if (msaaResult == MsaaGridActivator.Result.Unsafe)
            throw new Exception(
                "MSAA could not safely update " + airport + " RWY " + runway + ": " + msaaError);

        // EuroScope's legacy accessibility tree may expose only rows that are
        // currently painted. Scroll the requested row into view, then give
        // MSAA one more chance before any coordinate-based fallback.
        GetVisibleRunwayCells(grid, airport, runway);
        msaaResult = MsaaGridActivator.TrySetRunwayState(
            GetMsaaGridHandle(dlgHwnd, grid), airport, runway,
            depWanted, arrWanted, out msaaError);
        if (msaaResult == MsaaGridActivator.Result.Success)
            return;
        if (msaaResult == MsaaGridActivator.Result.Unsafe)
            throw new Exception(
                "MSAA could not safely update " + airport + " RWY " + runway + ": " + msaaError);

        SetRunwayCellState(
            dlgHwnd, grid, airport, runway, 4, "ICON:1", depWanted, "DEP");

        // Re-read from a newly resolved row after DEP. EuroScope virtualizes
        // its UIA elements, so retaining a cell can turn an ARR action into a
        // click in another airport or column.
        SetRunwayCellState(
            dlgHwnd, grid, airport, runway, 5, "ICON:2", arrWanted, "ARR");
    }

    private AutomationElementCollection GetVisibleRunwayCells(
        AutomationElement grid,
        string airport,
        string runway)
    {
        AutomationElement row = FindRunwayRow(grid, airport, runway);
        if (row == null)
            throw new Exception("Could not find " + airport + " RWY " + runway + ".");

        ScrollIntoView(row);
        Thread.Sleep(80);

        // A legacy virtualized list can temporarily stop enumerating rows just
        // after ScrollIntoView. Retain the original row only when its current
        // cells still prove that it is the requested ICAO/runway.
        AutomationElementCollection cells;
        try
        {
            if (TryGetRequestedRunwayCells(row, airport, runway, out cells))
                return cells;
        }
        catch (ElementNotAvailableException)
        {
            // Re-find below.
        }

        // If the original row was recycled, wait for a freshly enumerated row
        // and validate its ICAO/runway before using a cell from it.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(60);

            try
            {
                row = FindRunwayRow(grid, airport, runway);
                if (row == null)
                    continue;

                if (TryGetRequestedRunwayCells(row, airport, runway, out cells))
                    return cells;
            }
            catch (ElementNotAvailableException)
            {
                // The grid is still recycling an element; poll for a fresh one.
            }
        }

        throw new Exception("Could not obtain a stable visible row for " + airport + " RWY " + runway + ".");
    }

    private bool TryGetRequestedRunwayCells(
        AutomationElement row,
        string airport,
        string runway,
        out AutomationElementCollection cells)
    {
        cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
        return IsRequestedRunwayRow(cells, airport, runway);
    }

    private bool IsRequestedRunwayRow(
        AutomationElementCollection cells,
        string airport,
        string runway)
    {
        return cells != null && cells.Count >= 6 &&
               SafeName(cells[0]).Trim().Equals(airport, StringComparison.OrdinalIgnoreCase) &&
               SafeName(cells[3]).Trim().Equals(runway, StringComparison.OrdinalIgnoreCase);
    }

    private void SetRunwayCellState(
        IntPtr dlgHwnd,
        AutomationElement grid,
        string airport,
        string runway,
        int cellIndex,
        string activeName,
        bool wanted,
        string label)
    {
        AutomationElementCollection cells = GetVisibleRunwayCells(grid, airport, runway);
        bool active = SafeName(cells[cellIndex]).Equals(activeName, StringComparison.OrdinalIgnoreCase);
        if (active == wanted)
            return;

        ClickCellPhysically(
            dlgHwnd, grid, airport, runway, cellIndex, activeName, wanted);
        Thread.Sleep(120);
        WaitForRunwayCellState(grid, airport, runway, cellIndex, activeName, wanted, label);
    }

    private void WaitForRunwayCellState(
        AutomationElement grid,
        string airport,
        string runway,
        int cellIndex,
        string activeName,
        bool wanted,
        string label)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            AutomationElementCollection cells = GetVisibleRunwayCells(grid, airport, runway);
            bool active = SafeName(cells[cellIndex]).Equals(activeName, StringComparison.OrdinalIgnoreCase);
            if (active == wanted)
                return;

            Thread.Sleep(100);
        }

        throw new Exception(
            "EuroScope did not apply " + airport + " RWY " + runway + " " + label +
            " state after the validated native ListView click.");
    }

    // EuroScope ignores posted mouse messages. When MSAA is unavailable, use
    // the native report-list geometry where possible; UIA geometry is accepted
    // only after a screen hit-test proves its row and column are still live.
    private void ClickCellPhysically(
        IntPtr dlgHwnd,
        AutomationElement grid,
        string airport,
        string runway,
        int expectedCellIndex,
        string activeName,
        bool wanted)
    {
        ForegroundSelector(dlgHwnd);

        System.Windows.Rect nativeRect;
        string nativeError;
        NativeListViewGeometry.Result nativeResult =
            NativeListViewGeometry.TryGetCellScreenRectangle(
                GetMsaaGridHandle(dlgHwnd, grid), airport, runway,
                expectedCellIndex, out nativeRect, out nativeError);

        System.Windows.Point point;
        if (nativeResult == NativeListViewGeometry.Result.Success)
        {
            point = new System.Windows.Point(
                nativeRect.Left + nativeRect.Width / 2.0,
                nativeRect.Top + nativeRect.Height / 2.0);
        }
        else
        {
            throw new Exception(
                "Could not safely locate " + airport + " RWY " + runway +
                " in EuroScope's native list: " + nativeError + " No click was sent.");
        }

        if (!IsPointInNativeRunwayGrid(dlgHwnd, grid, point))
            throw new Exception(
                "Target point is not inside the EuroScope runway grid. No click was sent.");

        POINT target = new POINT
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y)
        };
        POINT saved;
        if (!GetPhysicalCursorPos(out saved))
            throw new Exception("Could not save the physical mouse position. No click was sent.");

        bool cursorMoved = false;
        try
        {
            if (!SetPhysicalCursorPos(target.X, target.Y))
                throw new Exception("Could not move the physical mouse cursor. No click was sent.");
            cursorMoved = true;

            // Give Parallels and the Windows input desktop time to receive the
            // position before injecting only the button-down/button-up events.
            Thread.Sleep(40);

            POINT actual;
            if (!GetPhysicalCursorPos(out actual) ||
                Math.Abs(actual.X - target.X) > 1 ||
                Math.Abs(actual.Y - target.Y) > 1)
                throw new Exception("Physical mouse position changed before the click. No click was sent.");

            if (!IsPointInNativeRunwayGrid(dlgHwnd, grid, point))
                throw new Exception("Target changed before the click. No click was sent.");

            // Set and check the physical point once more immediately before
            // injecting the button events.
            if (!SetPhysicalCursorPos(target.X, target.Y))
                throw new Exception("Could not restore the physical target point. No click was sent.");
            if (!GetPhysicalCursorPos(out actual) ||
                Math.Abs(actual.X - target.X) > 1 ||
                Math.Abs(actual.Y - target.Y) > 1)
                throw new Exception("Physical mouse position changed before the click. No click was sent.");

            INPUT[] buttonDown = new INPUT[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN }
                }
            };
            INPUT[] buttonUp = new INPUT[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP }
                }
            };
            bool buttonIsDown = false;
            try
            {
                if (SendInput(1, buttonDown, Marshal.SizeOf(typeof(INPUT))) != 1)
                    throw new Exception(
                        "Windows rejected the physical mouse-down (possibly UIPI). No click was sent.");

                buttonIsDown = true;

                // EuroScope is a legacy custom ListView client.  Preserve a
                // real press interval instead of delivering a zero-duration
                // batched click, so it can observe the down/capture state.
                Thread.Sleep(80);

                if (SendInput(1, buttonUp, Marshal.SizeOf(typeof(INPUT))) != 1)
                    throw new Exception(
                        "Windows rejected the physical mouse-up (possibly UIPI). The button was released defensively.");

                buttonIsDown = false;

                // Keep the cursor over the validated cell until EuroScope has
                // consumed the completed gesture.  Restoring it immediately
                // can make a legacy handler observe the saved position rather
                // than the position at mouse-up.
                Thread.Sleep(160);
            }
            finally
            {
                if (buttonIsDown)
                    SendInput(1, buttonUp, Marshal.SizeOf(typeof(INPUT)));
            }
        }
        finally
        {
            if (cursorMoved)
            {
                // If the user or Parallels already moved the pointer after
                // the completed gesture, do not pull it back unexpectedly.
                POINT current;
                if (GetPhysicalCursorPos(out current) &&
                    Math.Abs(current.X - target.X) <= 1 &&
                    Math.Abs(current.Y - target.Y) <= 1)
                    SetPhysicalCursorPos(saved.X, saved.Y);
            }
        }
    }

    private void ForegroundSelector(IntPtr dlgHwnd)
    {
        SetForegroundWindow(dlgHwnd);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (GetForegroundWindow() == dlgHwnd)
                return;

            Thread.Sleep(25);
        }

        throw new Exception("EuroScope selector is not foreground. No click was sent.");
    }

    private void ScrollIntoView(AutomationElement row)
    {
        object pat;
        if (row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out pat))
            ((ScrollItemPattern)pat).ScrollIntoView();
    }

    private string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch { return ""; }
    }

    private bool IsPointInNativeRunwayGrid(
        IntPtr dlgHwnd,
        AutomationElement grid,
        System.Windows.Point point)
    {
        IntPtr gridHwnd = GetMsaaGridHandle(dlgHwnd, grid);
        if (gridHwnd == IntPtr.Zero || !IsWindow(gridHwnd) ||
            !IsPointInsideWindow(gridHwnd, point))
            return false;

        IntPtr hit = WindowFromPoint(new POINT
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y)
        });
        return hit != IntPtr.Zero &&
               (hit == gridHwnd || IsChild(gridHwnd, hit));
    }

    private bool IsPointInsideWindow(IntPtr hwnd, System.Windows.Point point)
    {
        RECT rect;
        return GetWindowRect(hwnd, out rect) &&
               point.X >= rect.Left && point.X < rect.Right &&
               point.Y >= rect.Top && point.Y < rect.Bottom;
    }

}

// EuroScope exposes this selector through Microsoft Active Accessibility
// (MSAA). This helper deliberately identifies a cell by its accessibility
// tree, airport and runway text, rather than by a mouse coordinate.
internal static class MsaaGridActivator
{
    private const uint OBJID_CLIENT = 0xFFFFFFFCu;
    private const int CHILDID_SELF = 0;
    private const int STATE_SYSTEM_CHECKED = 0x00000010;
    private const int ROLE_SYSTEM_CHECKBUTTON = 0x0000002C;

    internal enum Result
    {
        Unavailable,
        Success,
        Unsafe,
        // The MSAA row was unambiguous, but its advertised default action did
        // not update the state. The caller may safely use the independently
        // validated physical-click fallback.
        NoEffect
    }

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint objectId,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleChildren(
        [MarshalAs(UnmanagedType.Interface)] IAccessible parent,
        int childStart,
        int childCount,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
        out int obtained);

    private sealed class Node
    {
        internal readonly IAccessible Owner;
        internal readonly object ChildId;
        internal readonly bool IsObject;

        internal Node(IAccessible owner, object childId, bool isObject)
        {
            Owner = owner;
            ChildId = childId;
            IsObject = isObject;
        }
    }

    internal static Result TryGetRunwayState(
        IntPtr gridHwnd,
        string airport,
        string runway,
        out bool depActive,
        out bool arrActive,
        out string error)
    {
        depActive = false;
        arrActive = false;

        List<Node> cells;
        Result result = TryFindRow(gridHwnd, airport, runway, out cells, out error);
        if (result != Result.Success)
            return result;

        if (!TryGetCheckedState(cells[4], out depActive, out error) ||
            !TryGetCheckedState(cells[5], out arrActive, out error))
            return Result.Unavailable;

        return Result.Success;
    }

    internal static Result TrySetRunwayState(
        IntPtr gridHwnd,
        string airport,
        string runway,
        bool depWanted,
        bool arrWanted,
        out string error)
    {
        bool depActive, arrActive;
        Result result = TryGetRunwayState(
            gridHwnd, airport, runway, out depActive, out arrActive, out error);
        if (result != Result.Success)
            return result;

        if (depActive != depWanted)
        {
            List<Node> cells;
            result = TryFindRow(gridHwnd, airport, runway, out cells, out error);
            if (result != Result.Success)
                return result;
            if (!TryGetCheckedState(cells[4], out depActive, out error))
                return Result.Unavailable;
            if (depActive != depWanted && !TryDoDefaultAction(cells[4], out error))
                return Result.NoEffect;

            Thread.Sleep(80);
            result = TryGetRunwayState(
                gridHwnd, airport, runway, out depActive, out arrActive, out error);
            if (result != Result.Success)
            {
                error = "MSAA state could not be re-read after changing DEP: " + error;
                return result;
            }
        }

        if (arrActive != arrWanted)
        {
            List<Node> cells;
            result = TryFindRow(gridHwnd, airport, runway, out cells, out error);
            if (result != Result.Success)
                return result;
            if (!TryGetCheckedState(cells[5], out arrActive, out error))
                return Result.Unavailable;
            if (arrActive != arrWanted && !TryDoDefaultAction(cells[5], out error))
                return Result.NoEffect;

            Thread.Sleep(80);
            result = TryGetRunwayState(
                gridHwnd, airport, runway, out depActive, out arrActive, out error);
            if (result != Result.Success)
            {
                error = "MSAA state could not be re-read after changing ARR: " + error;
                return result;
            }
        }

        if (depActive != depWanted || arrActive != arrWanted)
        {
            error = "MSAA action completed but did not produce the requested state.";
            return Result.NoEffect;
        }

        error = null;
        return Result.Success;
    }

    private static Result TryFindRow(
        IntPtr gridHwnd,
        string airport,
        string runway,
        out List<Node> row,
        out string error)
    {
        row = null;
        error = null;

        if (gridHwnd == IntPtr.Zero)
        {
            error = "EuroScope grid has no native window handle.";
            return Result.Unavailable;
        }

        try
        {
            Guid iid = typeof(IAccessible).GUID;
            IAccessible root;
            int hr = AccessibleObjectFromWindow(gridHwnd, OBJID_CLIENT, ref iid, out root);
            if (hr < 0 || root == null)
            {
                error = "Could not obtain the grid MSAA object (HRESULT 0x" +
                    hr.ToString("X8") + ").";
                return Result.Unavailable;
            }

            var matches = new List<List<Node>>();
            if (!CollectRows(root, airport, runway, 12, matches))
            {
                error = "Could not enumerate the EuroScope MSAA tree.";
                return Result.Unavailable;
            }

            if (matches.Count == 0)
            {
                error = "No MSAA row was available for " + airport +
                    " RWY " + runway + ".";
                return Result.Unavailable;
            }

            if (matches.Count > 1)
            {
                error = "Expected exactly one MSAA row for " + airport +
                    " RWY " + runway + ", found " + matches.Count + ".";
                return Result.Unsafe;
            }

            row = matches[0];
            return Result.Success;
        }
        catch (COMException ex)
        {
            error = "MSAA query failed (HRESULT 0x" +
                ex.ErrorCode.ToString("X8") + ").";
            return Result.Unavailable;
        }
    }

    private static bool TryGetCheckedState(Node node, out bool active, out string error)
    {
        active = false;
        error = null;

        try
        {
            object stateValue = node.Owner.get_accState(node.ChildId);
            int state;
            if (!TryGetInt(stateValue, out state))
            {
                error = "The MSAA cell exposes an unreadable state.";
                return false;
            }

            object roleValue = node.Owner.get_accRole(node.ChildId);
            int role;
            bool isCheckbox = TryGetInt(roleValue, out role) &&
                              role == ROLE_SYSTEM_CHECKBUTTON;
            if (!isCheckbox)
            {
                error = "The requested MSAA cell is not a checkbox.";
                return false;
            }

            active = (state & STATE_SYSTEM_CHECKED) != 0;
            return true;
        }
        catch (COMException ex)
        {
            error = "Could not read the MSAA checkbox state (HRESULT 0x" +
                ex.ErrorCode.ToString("X8") + ").";
            return false;
        }
    }

    private static bool TryDoDefaultAction(Node node, out string error)
    {
        try
        {
            node.Owner.accDoDefaultAction(node.ChildId);
            error = null;
            return true;
        }
        catch (COMException ex)
        {
            error = "MSAA default action failed (HRESULT 0x" +
                ex.ErrorCode.ToString("X8") + ").";
            return false;
        }
    }

    private static bool TryGetInt(object value, out int number)
    {
        try
        {
            number = Convert.ToInt32(value);
            return true;
        }
        catch (Exception)
        {
            number = 0;
            return false;
        }
    }

    private static bool CollectRows(
        IAccessible parent,
        string airport,
        string runway,
        int depth,
        List<List<Node>> matches)
    {
        List<Node> children;
        if (!TryGetChildren(parent, out children))
            return false;

        if (children.Count >= 6 &&
            EqualName(SafeName(children[0]), airport) &&
            EqualName(SafeName(children[3]), runway))
            matches.Add(children);

        if (matches.Count > 1)
            return true;

        if (depth == 0)
            return true;

        foreach (Node child in children)
            if (child.IsObject &&
                !CollectRows(child.Owner, airport, runway, depth - 1, matches))
                return false;

        return true;
    }

    private static bool TryGetChildren(IAccessible parent, out List<Node> children)
    {
        children = new List<Node>();

        try
        {
            int count = parent.accChildCount;
            if (count <= 0)
                return true;
            if (count > 4096)
                return false;

            object[] raw = new object[count];
            int obtained;
            int hr = AccessibleChildren(parent, 0, count, raw, out obtained);
            if (hr < 0)
                return false;

            for (int i = 0; i < obtained; i++)
            {
                Node node = MakeNode(parent, raw[i]);
                if (node != null)
                    children.Add(node);
            }

            return true;
        }
        catch (COMException)
        {
            children = null;
            return false;
        }
    }

    private static Node MakeNode(IAccessible parent, object raw)
    {
        IAccessible childObject = raw as IAccessible;
        if (childObject != null)
            return new Node(childObject, CHILDID_SELF, true);

        if (raw is int)
        {
            try
            {
                childObject = parent.get_accChild(raw) as IAccessible;
                if (childObject != null)
                    return new Node(childObject, CHILDID_SELF, true);
            }
            catch (COMException)
            {
                // This is a simple MSAA child. Keep its parent/child ID pair.
            }

            return new Node(parent, raw, false);
        }

        return null;
    }

    private static string SafeName(Node node)
    {
        try { return node.Owner.get_accName(node.ChildId) ?? String.Empty; }
        catch (COMException) { return String.Empty; }
    }

    private static bool EqualName(string actual, string expected)
    {
        return String.Equals(
            (actual ?? String.Empty).Trim(),
            (expected ?? String.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runways.json");
            RunwayConfiguration configuration = RunwayConfiguration.Load(configPath);
            Application.Run(new MainForm(configuration));
        }
        catch (ConfigurationException ex)
        {
            MessageBox.Show(
                ex.Message,
                "EuroScope Runway Preset Manager - configuration error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
