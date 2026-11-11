using System;
using System.Globalization;
using Microsoft.Win32;
using SoundpadTrackHub.Models;

namespace SoundpadTrackHub.Integration
{
    public interface ISoundpadSettingsStore
    {
        SoundpadNormalization? Read();

        void Write(SoundpadNormalization value);
    }

    /// <summary>
    /// Настройки нормализации Soundpad в HKCU\Software\Leppsoft\Soundpad\MainFrame:
    /// normalizeTargetDecibel (строка, «Громкость голоса»), normalizeVolume (1 — динамическая,
    /// 0 — фиксированная), fixedVolumeAdjustmentDecibel (строка, «Подстройка громкости»).
    /// Soundpad читает их при запуске и записывает при выходе, поэтому писать можно
    /// только пока Soundpad закрыт. Другие значения Soundpad не трогаются.
    /// </summary>
    public sealed class SoundpadRegistryStore : ISoundpadSettingsStore
    {
        public const string KeyPath = @"Software\Leppsoft\Soundpad\MainFrame";
        public const string TargetValue = "normalizeTargetDecibel";
        public const string DynamicValue = "normalizeVolume";
        public const string FixedValue = "fixedVolumeAdjustmentDecibel";

        public SoundpadNormalization? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (key is null)
            {
                return null;
            }

            var target = ParseDb(key.GetValue(TargetValue));
            if (target is null)
            {
                return null;
            }

            var dynamic = key.GetValue(DynamicValue) is int d ? d != 0 : true;
            return new SoundpadNormalization(target.Value, dynamic, ParseDb(key.GetValue(FixedValue)));
        }

        public void Write(SoundpadNormalization value)
        {
            // Ключ создаёт сам Soundpad. Если его нет — Soundpad ни разу не запускался, писать некуда.
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                            ?? throw new InvalidOperationException("Настройки Soundpad не найдены. Запустите Soundpad хотя бы один раз.");
            key.SetValue(TargetValue, value.TargetDecibel.ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue(DynamicValue, value.Dynamic ? 1 : 0, RegistryValueKind.DWord);
            if (value.FixedAdjustmentDecibel is { } fixedDb)
            {
                key.SetValue(FixedValue, fixedDb.ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
            }
        }

        private static int? ParseDb(object? raw)
        {
            var s = raw?.ToString()?.Replace(',', '.').Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)Math.Round(v) : null;
        }
    }
}
