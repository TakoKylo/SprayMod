using System.Collections.Generic;
using Newtonsoft.Json;

namespace SprayMod
{
    /// <summary>
    /// One spray entry in sprays.json. A spray is either a local file (in SprayImages)
    /// or a web link. Order in the list = order in the wheel.
    /// </summary>
    public class SpraySpec
    {
        public string name;   // friendly display name (shown in wheel/settings)
        public string file;   // filename in SprayImages (local sprays)
        public string url;    // http(s) URL (link sprays, shared with everyone)

        [JsonIgnore] public bool IsUrl => !string.IsNullOrEmpty(url);
        [JsonIgnore] public string Source => IsUrl ? url : file;

        [JsonIgnore]
        public string DisplayName =>
            !string.IsNullOrEmpty(name) ? name : (IsUrl ? url : file);
    }

    /// <summary>
    /// The spray library manifest. Single source of truth for which sprays exist and in what order.
    /// </summary>
    public class SprayManifest
    {
        public List<SpraySpec> sprays = new List<SpraySpec>();
    }

    /// <summary>
    /// The whole mod config in ONE file (config/ModHub/Sprays/spraymod.json): client settings +
    /// the spray list. Migrated from the old split spray_client_config.json + sprays.json.
    /// </summary>
    public class SprayModData
    {
        public SprayClientConfig settings = new SprayClientConfig();
        public List<SpraySpec> sprays = new List<SpraySpec>();
    }
}
