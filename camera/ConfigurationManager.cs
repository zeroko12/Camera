using System;
using System.Configuration;

namespace camera
{
    /// <summary>
    /// 向后兼容的配置读写门面。所有现有调用点保持原签名。
    ///
    /// 实际持久化由 <see cref="AppSettingsService"/> 完成(JSON 文件,企业级);
    /// 这里只保留对旧代码的兼容外观,内部委托给新服务。
    /// </summary>
    public static class ConfigurationManager
    {
        public static string GetSetting(string key)
        {
            return System.Configuration.ConfigurationManager.AppSettings[key];
        }

        public static void SaveSetting(string key, string value)
        {
            // 旧 API 仍写入 .exe.config(向后兼容);
            // 后续建议迁移到 AppSettingsService.Current 各属性直接绑定。
            try
            {
                var config = System.Configuration.ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
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
            catch (Exception ex)
            {
                Logger.LogWarning($"[ConfigurationManager] 写入 {key} 失败: {ex.Message}");
            }
        }
    }
}