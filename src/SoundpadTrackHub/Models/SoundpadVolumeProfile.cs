namespace SoundpadTrackHub.Models
{
    /// <summary>
    /// Профили громкости Soundpad: «Файл → Настройки → Аудио → Нормализация громкости».
    /// </summary>
    public enum SoundpadVolumeProfile
    {
        /// <summary>Громкость голоса 80 дБ, динамическая регулировка.</summary>
        Normal = 0,

        /// <summary>95 дБ, динамическая регулировка.</summary>
        Loud = 1,

        /// <summary>107 дБ, динамическая регулировка.</summary>
        Loud2 = 2,

        /// <summary>107 дБ, фиксированная регулировка, подстройка 30 дБ.</summary>
        SuperLoud = 3,
    }

    /// <summary>Значения нормализации Soundpad.</summary>
    public sealed record SoundpadNormalization(int TargetDecibel, bool Dynamic, int? FixedAdjustmentDecibel)
    {
        public string Describe() => Dynamic
            ? $"{TargetDecibel} дБ, динамическая"
            : $"{TargetDecibel} дБ, фиксированная, подстройка {FixedAdjustmentDecibel} дБ";
    }

    public static class SoundpadVolumeProfileExtensions
    {
        public static SoundpadNormalization Settings(this SoundpadVolumeProfile profile) => profile switch
        {
            SoundpadVolumeProfile.Loud => new SoundpadNormalization(95, true, null),
            SoundpadVolumeProfile.Loud2 => new SoundpadNormalization(107, true, null),
            SoundpadVolumeProfile.SuperLoud => new SoundpadNormalization(107, false, 30),
            _ => new SoundpadNormalization(80, true, null),
        };

        public static string DisplayName(this SoundpadVolumeProfile profile) => profile switch
        {
            SoundpadVolumeProfile.Loud => "Громко",
            SoundpadVolumeProfile.Loud2 => "Громко 2",
            SoundpadVolumeProfile.SuperLoud => "Супер громко",
            _ => "Нормально",
        };

        /// <summary>Какой профиль соответствует текущим значениям Soundpad (null — пользовательские).</summary>
        public static SoundpadVolumeProfile? Match(SoundpadNormalization current)
        {
            foreach (var p in new[] { SoundpadVolumeProfile.Normal, SoundpadVolumeProfile.Loud, SoundpadVolumeProfile.Loud2, SoundpadVolumeProfile.SuperLoud })
            {
                var s = p.Settings();
                if (s.TargetDecibel == current.TargetDecibel && s.Dynamic == current.Dynamic
                    && (s.Dynamic || s.FixedAdjustmentDecibel == current.FixedAdjustmentDecibel))
                {
                    return p;
                }
            }

            return null;
        }
    }
}
