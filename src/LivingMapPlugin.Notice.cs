using System;
using BepInEx.Configuration;
using UnityEngine;

namespace LivingMap
{
    // A one-time note about what is new, shown the first time the big map is opened with a
    // version that has one: the game's own Yes/No popup. Yes keeps the new feature, No turns it
    // off; either way the version is remembered in the config and the note never comes back
    // (for this version - a later one with a note of its own shows that once).
    public partial class LivingMapPlugin
    {
        private const string NoticeVersion = "0.16.0";
        private ConfigEntry<string> _cfgNoticeSeen;
        private bool _noticeDone;

        private void BindNoticeConfig()
        {
            _cfgNoticeSeen = Config.Bind(SecGeneral, "LastSeenNotice", "",
                Hidden("Internal: the version whose what's-new note has been shown. Do not edit."));
        }

        private void NoticeTick()
        {
            if (_noticeDone) return;
            if (_cfgNoticeSeen == null || _cfgNoticeSeen.Value == NoticeVersion) { _noticeDone = true; return; }
            if (!_ready || _mm == null || _mm.m_mode != Minimap.MapMode.Large || Player.m_localPlayer == null) return;
            if (!UnifiedPopup.IsAvailable() || UnifiedPopup.IsVisible()) return;
            _noticeDone = true;

            bool ru = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
            string header = ru ? "Living Map " + NoticeVersion + ": детальная карта" : "Living Map " + NoticeVersion + ": a detailed map";
            string text = ru
                ? "Если приблизить большую карту, она теперь рисуется детально, в стиле ванильной карты: постройки с крышами, дороги, поля и вырубки, и чем ближе, тем детальнее.\n\n"
                  + "Это экспериментальная функция. Если заметите ошибку, напишите, пожалуйста, на GitHub: github.com/tbsj1ga/LivingMapValheim/issues\n\n"
                  + "Выключить её можно и потом: в файле BepInEx/config/j1ga.livingmap.cfg, раздел [08 Detail], строка DetailEnabled = false.\n\n"
                  + "Оставить детальную карту включённой?"
                : "When you zoom the big map in, it is now drawn in detail, in the vanilla map's style: buildings with their roofs, paths, fields and cleared forest - the closer, the more detailed.\n\n"
                  + "This is an experimental feature. If you notice a problem, please report it on GitHub: github.com/tbsj1ga/LivingMapValheim/issues\n\n"
                  + "You can turn it off later too: in BepInEx/config/j1ga.livingmap.cfg, section [08 Detail], line DetailEnabled = false.\n\n"
                  + "Keep the detailed map on?";
            try
            {
                UnifiedPopup.Push(new YesNoPopup(header, text,
                    delegate { NoticeAnswered(true); },
                    delegate { NoticeAnswered(false); },
                    false));
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not show the what's-new note: " + e.Message);
            }
        }

        private void NoticeAnswered(bool keep)
        {
            try
            {
                if (!keep && _cfgDetail != null) _cfgDetail.Value = false;
                _cfgNoticeSeen.Value = NoticeVersion;
                Config.Save();
            }
            finally { UnifiedPopup.Pop(); }
        }
    }
}
