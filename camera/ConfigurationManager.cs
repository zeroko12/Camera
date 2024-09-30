using System;
using System.Configuration;

namespace camera
{
    public static class ConfigurationManager
    {
        public static string GetSetting(string key)
        {
            return System.Configuration.ConfigurationManager.AppSettings[key];
        }

        public static void SaveSetting(string key, string value)
        {
            Configuration config = System.Configuration.ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            if (config.AppSettings.Settings[key] == null)
            {
                config.AppSettings.Settings.Add(key, value);
            }
            else
            {
                config.AppSettings.Settings[key].Value = value;
            }
            config.Save(ConfigurationSaveMode.Modified);
            System.Configuration.ConfigurationManager.RefreshSection("appSettings");
        }
    }
}