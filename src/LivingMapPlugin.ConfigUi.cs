using BepInEx.Configuration;

namespace LivingMap
{
    // How the settings show in the ConfigurationManager window (F1): readable names, grouped by
    // what a player cares about, technical ones marked advanced (hidden until "Show advanced" is
    // ticked). The keys and sections in the .cfg file stay as they were, so values people have
    // already set are kept.
    public partial class LivingMapPlugin
    {
        private const string UiLayers = "1. Map layers";
        private const string UiDetail = "2. Close-up detail";
        private const string UiColours = "3. Colours";
        private const string UiAdvanced = "4. Advanced";
        private const string UiDebug = "5. Debug";

        private static ConfigDescription Ui(string description, string category, string name, int order, bool advanced)
        {
            return Ui(description, null, category, name, order, advanced);
        }

        private static ConfigDescription Ui(string description, AcceptableValueBase values, string category, string name, int order, bool advanced)
        {
            ConfigurationManagerAttributes a = new ConfigurationManagerAttributes();
            a.Category = category; a.DispName = name; a.Order = order; a.IsAdvanced = advanced;
            return new ConfigDescription(description, values, a);
        }

        private static ConfigDescription Hidden(string description)
        {
            ConfigurationManagerAttributes a = new ConfigurationManagerAttributes();
            a.Browsable = false;
            return new ConfigDescription(description, null, a);
        }
    }

    // Read by ConfigurationManager by field name; only the fields used here.
#pragma warning disable 0649, 0169, 0414
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
        public int? Order;
        public string DispName;
        public string Category;
        public bool? Browsable;
    }
#pragma warning restore 0649, 0169, 0414
}
